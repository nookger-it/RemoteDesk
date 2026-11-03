using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using RemoteDesk.Contracts.Sessions;
using RemoteDesk.Transport.Abstractions;

namespace RemoteDesk.Transport.LocalRelay;

public sealed class LocalRelayTransport : IRemoteTransport
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private TcpClient? _tcpClient;
    private SslStream? _sslStream;
    private CancellationTokenSource? _receiveCts;
    private Task? _receiveLoopTask;
    private Guid _activeSessionId;

    private long _totalFrames;
    private long _totalBytes;
    private double _smoothedLatencyMs;
    private DateTimeOffset _connectedAtUtc;

    public string TransportName => "LocalRelay";

    public event Action<ScreenFramePacket>? FrameReceived;

    public event Action<InputControlAckPacket>? InputAckReceived;

    public event Action<SessionDisconnectPacket>? RemoteDisconnected;

    public async Task<TransportConnectionResult> ConnectAsync(
        SessionConnectionInfo session,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (string.IsNullOrWhiteSpace(session.SessionTicket) ||
            session.TicketExpiresAt <= DateTimeOffset.UtcNow)
        {
            return new TransportConnectionResult(
                IsConnected: false,
                ActiveTransportType: TransportName,
                FailureReason: "Session ticket is missing or expired.");
        }

        try
        {
            _activeSessionId = session.SessionId;
            _tcpClient = new TcpClient { NoDelay = true };
            await _tcpClient.ConnectAsync(session.RelayAddress, session.RelayPort, cancellationToken);

            _sslStream = new SslStream(
                _tcpClient.GetStream(),
                leaveInnerStreamOpen: false,
                userCertificateValidationCallback: (_, _, _, _) => true);

            await _sslStream.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions
                {
                    TargetHost = "remotedesk-relay.local",
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
                },
                cancellationToken);

            var handshakeReq = new RelayHandshakeRequest(
                session.SessionId,
                session.DeviceId,
                "OPERATOR",
                session.SessionTicket);

            await LocalRelayServer.WriteFramedJsonAsync(_sslStream, handshakeReq, cancellationToken);
            var handshakeResp = await LocalRelayServer.ReadFramedJsonAsync<RelayHandshakeResponse>(_sslStream, cancellationToken);

            if (handshakeResp is null || !handshakeResp.Accepted)
            {
                await DisconnectAsync(cancellationToken);
                return new TransportConnectionResult(
                    IsConnected: false,
                    ActiveTransportType: TransportName,
                    FailureReason: handshakeResp?.ErrorMessage ?? "Local Relay rejected session handshake.");
            }

            _connectedAtUtc = DateTimeOffset.UtcNow;
            _totalFrames = 0;
            _totalBytes = 0;
            _smoothedLatencyMs = 0;

            _receiveCts = new CancellationTokenSource();
            _receiveLoopTask = Task.Run(() => ReceiveFramesLoopAsync(_sslStream, _receiveCts.Token), CancellationToken.None);

            return new TransportConnectionResult(
                IsConnected: true,
                ActiveTransportType: TransportName);
        }
        catch (Exception ex)
        {
            await DisconnectAsync(CancellationToken.None);
            return new TransportConnectionResult(
                IsConnected: false,
                ActiveTransportType: TransportName,
                FailureReason: ex.Message);
        }
    }

    public async Task SendInputEventAsync(
        RemoteInputEventPacket inputEvent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inputEvent);
        var stream = _sslStream;
        if (stream is null)
        {
            throw new InvalidOperationException("LocalRelayTransport is not connected.");
        }

        var envelope = new RelayWireEnvelope("INPUT", Input: inputEvent);
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await LocalRelayServer.WriteFramedJsonAsync(stream, envelope, cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public StreamTelemetrySnapshot GetTelemetrySnapshot()
    {
        var elapsedSec = Math.Max(0.25, (DateTimeOffset.UtcNow - _connectedAtUtc).TotalSeconds);
        var frames = Interlocked.Read(ref _totalFrames);
        var bytes = Interlocked.Read(ref _totalBytes);
        var fps = frames > 0 ? Math.Round(frames / elapsedSec, 1) : 0;
        var kbps = bytes > 0 ? Math.Round((bytes * 8.0 / 1000.0) / elapsedSec, 1) : 0;

        return new StreamTelemetrySnapshot(
            CurrentFps: fps,
            AverageLatencyMs: Math.Round(_smoothedLatencyMs, 1),
            BandwidthKbps: kbps,
            TotalFramesReceived: frames,
            TotalBytesReceived: bytes);
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_sslStream is { CanWrite: true } stream && _activeSessionId != Guid.Empty)
            {
                await _writeLock.WaitAsync(TimeSpan.FromMilliseconds(150), cancellationToken);
                try
                {
                    await LocalRelayServer.WriteFramedJsonAsync(
                        stream,
                        new RelayWireEnvelope(
                            "DISCONNECT",
                            Disconnect: new SessionDisconnectPacket(_activeSessionId, "Operator", "Operator closed relay session")),
                        cancellationToken);
                }
                catch
                {
                    // Ignore disconnect notification failure
                }
                finally
                {
                    _writeLock.Release();
                }
            }

            _receiveCts?.Cancel();
            if (_receiveLoopTask is not null)
            {
                await Task.WhenAny(_receiveLoopTask, Task.Delay(300, cancellationToken));
            }
        }
        catch
        {
            // Ignore shutdown cancellation
        }
        finally
        {
            _sslStream?.Dispose();
            _sslStream = null;
            _tcpClient?.Dispose();
            _tcpClient = null;
            _receiveCts?.Dispose();
            _receiveCts = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync(CancellationToken.None);
        _writeLock.Dispose();
    }

    private async Task ReceiveFramesLoopAsync(Stream stream, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var envelope = await LocalRelayServer.ReadFramedJsonAsync<RelayWireEnvelope>(stream, ct);
                if (envelope is null)
                {
                    break;
                }

                if (envelope.PacketType == "FRAME" && envelope.Frame is { } frame)
                {
                    Interlocked.Increment(ref _totalFrames);
                    Interlocked.Add(ref _totalBytes, frame.Payload.Length);

                    var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    var sampleLatency = Math.Max(1, nowMs - frame.CapturedAtUnixMs);
                    _smoothedLatencyMs = _smoothedLatencyMs <= 0
                        ? sampleLatency
                        : (_smoothedLatencyMs * 0.8) + (sampleLatency * 0.2);

                    FrameReceived?.Invoke(frame);
                }
                else if (envelope.PacketType == "INPUT_ACK" && envelope.InputAck is { } ack)
                {
                    InputAckReceived?.Invoke(ack);
                }
                else if (envelope.PacketType == "DISCONNECT" && envelope.Disconnect is { } disc)
                {
                    RemoteDisconnected?.Invoke(disc);
                    break;
                }
            }
        }
        catch
        {
            // Stream closed or cancelled
        }
    }
}
