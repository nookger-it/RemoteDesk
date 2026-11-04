using System.Buffers;
using System.Buffers.Binary;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json;
using RemoteDesk.Contracts.Sessions;
using RemoteDesk.Transport.Abstractions;

namespace RemoteDesk.Transport.DirectLan;

internal sealed record DirectLanHandshakeRequest(
    Guid SessionId,
    Guid DeviceId,
    Guid OperatorUserId,
    string SessionTicket);

internal sealed record DirectLanHandshakeResponse(
    bool Accepted,
    bool AllowInputControl,
    string? ErrorMessage);

internal sealed record DirectLanWireEnvelope(
    string PacketType,
    ScreenFramePacket? Frame = null,
    RemoteInputEventPacket? Input = null,
    InputControlAckPacket? InputAck = null,
    SessionDisconnectPacket? Disconnect = null,
    StreamQualityProfile? Quality = null);

public sealed class DirectLanTransport : IRemoteTransport, IStreamQualityControl
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

    // Rolling-window telemetry (so FPS/Bandwidth reflect "now", not the session average)
    private readonly object _telemetryLock = new();
    private DateTimeOffset _lastSnapshotAt;
    private long _lastSnapshotFrames;
    private long _lastSnapshotBytes;
    private double _lastFps;
    private double _lastKbps;

    public string TransportName => "DirectLan";

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
            _tcpClient = new TcpClient
            {
                NoDelay = true,
                ReceiveBufferSize = 262144,
                SendBufferSize = 65536
            };
            await _tcpClient.ConnectAsync(session.DirectAddress, session.DirectPort, cancellationToken);

            _sslStream = new SslStream(
                _tcpClient.GetStream(),
                leaveInnerStreamOpen: false,
                userCertificateValidationCallback: (_, _, _, _) => true);

            await _sslStream.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions
                {
                    TargetHost = "remotedesk-agent.local",
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
                },
                cancellationToken);

            var handshakeReq = new DirectLanHandshakeRequest(
                session.SessionId,
                session.DeviceId,
                session.OperatorUserId,
                session.SessionTicket);

            await WriteFramedJsonAsync(_sslStream, handshakeReq, cancellationToken);
            var handshakeResp = await ReadFramedJsonAsync<DirectLanHandshakeResponse>(_sslStream, cancellationToken);

            if (handshakeResp is null || !handshakeResp.Accepted)
            {
                await DisconnectAsync(cancellationToken);
                return new TransportConnectionResult(
                    IsConnected: false,
                    ActiveTransportType: TransportName,
                    FailureReason: handshakeResp?.ErrorMessage ?? "Agent rejected Direct LAN session handshake.");
            }

            _connectedAtUtc = DateTimeOffset.UtcNow;
            _totalFrames = 0;
            _totalBytes = 0;
            _smoothedLatencyMs = 0;
            _lastSnapshotAt = _connectedAtUtc;
            _lastSnapshotFrames = 0;
            _lastSnapshotBytes = 0;
            _lastFps = 0;
            _lastKbps = 0;

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
            throw new InvalidOperationException("DirectLanTransport is not connected.");
        }

        var envelope = new DirectLanWireEnvelope("INPUT", Input: inputEvent);
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await WriteFramedJsonAsync(stream, envelope, cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task SetQualityProfileAsync(StreamQualityProfile profile, CancellationToken cancellationToken)
    {
        var stream = _sslStream;
        if (stream is null)
        {
            return;
        }

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await WriteFramedJsonAsync(stream, new DirectLanWireEnvelope("QUALITY", Quality: profile), cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public StreamTelemetrySnapshot GetTelemetrySnapshot()
    {
        var frames = Interlocked.Read(ref _totalFrames);
        var bytes = Interlocked.Read(ref _totalBytes);
        var now = DateTimeOffset.UtcNow;

        lock (_telemetryLock)
        {
            var windowSec = (now - _lastSnapshotAt).TotalSeconds;
            if (windowSec >= 0.4)
            {
                _lastFps = Math.Round((frames - _lastSnapshotFrames) / windowSec, 1);
                _lastKbps = Math.Round(((bytes - _lastSnapshotBytes) * 8.0 / 1000.0) / windowSec, 1);
                _lastSnapshotAt = now;
                _lastSnapshotFrames = frames;
                _lastSnapshotBytes = bytes;
            }

            return new StreamTelemetrySnapshot(
                CurrentFps: _lastFps,
                AverageLatencyMs: Math.Round(_smoothedLatencyMs, 1),
                BandwidthKbps: _lastKbps,
                TotalFramesReceived: frames,
                TotalBytesReceived: bytes);
        }
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
                    await WriteFramedJsonAsync(
                        stream,
                        new DirectLanWireEnvelope(
                            "DISCONNECT",
                            Disconnect: new SessionDisconnectPacket(_activeSessionId, "Operator", "Operator closed session")),
                        cancellationToken);
                }
                catch
                {
                    // Ignore disconnect notification failure if socket already closed
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
                var envelope = await ReadEnvelopeAsync(stream, ct);
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

    internal static async Task WriteFramedJsonAsync<T>(Stream stream, T value, CancellationToken ct)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(value);
        var buffer = new byte[4 + payload.Length];
        BinaryPrimitives.WriteInt32BigEndian(buffer, payload.Length);
        payload.CopyTo(buffer, 4);
        // Single write => single TLS record (fewer syscalls, lower latency)
        await stream.WriteAsync(buffer, ct);
        await stream.FlushAsync(ct);
    }

    /// <summary>
    /// Writes a screen frame as raw binary (no Base64/JSON for the JPEG bytes).
    /// Wire layout: [int32 BE = -(4 + headerLen + payloadLen)] [int32 BE headerLen] [header JSON] [JPEG bytes].
    /// A negative length prefix marks a binary frame so JSON control packets remain compatible.
    /// </summary>
    internal static async Task WriteBinaryFrameAsync(Stream stream, ScreenFramePacket frame, CancellationToken ct)
    {
        var header = JsonSerializer.SerializeToUtf8Bytes(frame with { Payload = [] });
        var payload = frame.Payload;
        var bodyLength = 4 + header.Length + payload.Length;
        var total = 4 + bodyLength;

        var buffer = ArrayPool<byte>.Shared.Rent(total);
        try
        {
            BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(0, 4), -bodyLength);
            BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(4, 4), header.Length);
            header.CopyTo(buffer.AsSpan(8));
            payload.CopyTo(buffer.AsSpan(8 + header.Length));
            await stream.WriteAsync(buffer.AsMemory(0, total), ct);
            await stream.FlushAsync(ct);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Reads either a JSON envelope (positive length prefix) or a binary FRAME (negative length prefix).
    /// </summary>
    internal static async Task<DirectLanWireEnvelope?> ReadEnvelopeAsync(Stream stream, CancellationToken ct)
    {
        var lengthPrefix = new byte[4];
        var read = await ReadExactAsync(stream, lengthPrefix, ct);
        if (read == 0)
        {
            return null;
        }

        var length = BinaryPrimitives.ReadInt32BigEndian(lengthPrefix);
        if (length >= 0)
        {
            if (length == 0 || length > MaxPacketBytes)
            {
                throw new InvalidDataException($"Invalid frame length: {length}");
            }

            var json = new byte[length];
            await ReadExactAsync(stream, json, ct);
            return JsonSerializer.Deserialize<DirectLanWireEnvelope>(json);
        }

        var bodyLength = -length;
        if (bodyLength < 4 || bodyLength > MaxPacketBytes)
        {
            throw new InvalidDataException($"Invalid binary frame length: {bodyLength}");
        }

        var body = new byte[bodyLength];
        await ReadExactAsync(stream, body, ct);
        var headerLength = BinaryPrimitives.ReadInt32BigEndian(body.AsSpan(0, 4));
        if (headerLength <= 0 || headerLength > bodyLength - 4)
        {
            throw new InvalidDataException($"Invalid binary frame header length: {headerLength}");
        }

        var header = JsonSerializer.Deserialize<ScreenFramePacket>(body.AsSpan(4, headerLength));
        if (header is null)
        {
            return null;
        }

        var payload = body.AsSpan(4 + headerLength).ToArray();
        return new DirectLanWireEnvelope("FRAME", Frame: header with { Payload = payload });
    }

    private const int MaxPacketBytes = 32 * 1024 * 1024;

    internal static async Task<T?> ReadFramedJsonAsync<T>(Stream stream, CancellationToken ct)
    {
        var lengthPrefix = new byte[4];
        var read = await ReadExactAsync(stream, lengthPrefix, ct);
        if (read == 0)
        {
            return default;
        }

        var length = BinaryPrimitives.ReadInt32BigEndian(lengthPrefix);
        if (length <= 0 || length > 16 * 1024 * 1024)
        {
            throw new InvalidDataException($"Invalid frame length: {length}");
        }

        var buffer = new byte[length];
        await ReadExactAsync(stream, buffer, ct);
        return JsonSerializer.Deserialize<T>(buffer);
    }

    private static async Task<int> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int bytesRead = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), ct);
            if (bytesRead == 0)
            {
                return offset == 0 ? 0 : throw new EndOfStreamException();
            }

            offset += bytesRead;
        }

        return offset;
    }
}
