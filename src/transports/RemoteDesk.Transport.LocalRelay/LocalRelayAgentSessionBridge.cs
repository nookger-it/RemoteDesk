using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using RemoteDesk.Contracts.Sessions;

namespace RemoteDesk.Transport.LocalRelay;

/// <summary>
/// Outbound TLS connector used by Agent.Interactive to stream frames and receive input
/// through the Local Relay Server (port 52100) when Direct LAN is unreachable.
/// </summary>
public sealed class LocalRelayAgentSessionBridge : IAsyncDisposable
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly string _relayAddress;
    private readonly int _relayPort;
    private readonly Guid _sessionId;
    private readonly Guid _deviceId;
    private readonly string _sessionTicket;
    private readonly Func<long, CancellationToken, Task<ScreenFramePacket>> _frameProvider;
    private readonly Func<RemoteInputEventPacket, bool, CancellationToken, Task<InputControlAckPacket>>? _inputHandler;
    private readonly int _targetFps;

    private CancellationTokenSource? _cts;
    private Task? _bridgeTask;

    public LocalRelayAgentSessionBridge(
        string relayAddress,
        int relayPort,
        Guid sessionId,
        Guid deviceId,
        string sessionTicket,
        Func<long, CancellationToken, Task<ScreenFramePacket>> frameProvider,
        int targetFps = 12,
        Func<RemoteInputEventPacket, bool, CancellationToken, Task<InputControlAckPacket>>? inputHandler = null)
    {
        _relayAddress = relayAddress;
        _relayPort = relayPort;
        _sessionId = sessionId;
        _deviceId = deviceId;
        _sessionTicket = sessionTicket;
        _frameProvider = frameProvider;
        _targetFps = Math.Clamp(targetFps, 1, 30);
        _inputHandler = inputHandler;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _bridgeTask = Task.Run(() => RunBridgeAsync(_cts.Token));
    }

    private async Task RunBridgeAsync(CancellationToken ct)
    {
        try
        {
            using var tcpClient = new TcpClient { NoDelay = true };
            await tcpClient.ConnectAsync(_relayAddress, _relayPort, ct);

            using var sslStream = new SslStream(
                tcpClient.GetStream(),
                leaveInnerStreamOpen: false,
                userCertificateValidationCallback: (_, _, _, _) => true);

            await sslStream.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions
                {
                    TargetHost = "remotedesk-relay.local",
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
                },
                ct);

            var req = new RelayHandshakeRequest(_sessionId, _deviceId, "AGENT", _sessionTicket);
            await LocalRelayServer.WriteFramedJsonAsync(sslStream, req, ct);

            var resp = await LocalRelayServer.ReadFramedJsonAsync<RelayHandshakeResponse>(sslStream, ct);
            if (resp is null || !resp.Accepted)
            {
                return;
            }

            var allowInputControl = resp.AllowInputControl;
            using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            var receiveInputTask = Task.Run(async () =>
            {
                try
                {
                    while (!sessionCts.Token.IsCancellationRequested)
                    {
                        var envelope = await LocalRelayServer.ReadFramedJsonAsync<RelayWireEnvelope>(sslStream, sessionCts.Token);
                        if (envelope is null)
                        {
                            break;
                        }

                        if (envelope.PacketType == "INPUT" && envelope.Input is { } inputPacket && _inputHandler is not null)
                        {
                            var ack = await _inputHandler(inputPacket, allowInputControl, sessionCts.Token);
                            await _writeLock.WaitAsync(sessionCts.Token);
                            try
                            {
                                await LocalRelayServer.WriteFramedJsonAsync(
                                    sslStream,
                                    new RelayWireEnvelope("INPUT_ACK", InputAck: ack),
                                    sessionCts.Token);
                            }
                            finally
                            {
                                _writeLock.Release();
                            }
                        }
                        else if (envelope.PacketType == "DISCONNECT")
                        {
                            break;
                        }
                    }
                }
                catch
                {
                    // Stream closed
                }
                finally
                {
                    sessionCts.Cancel();
                }
            }, sessionCts.Token);

            var frameInterval = TimeSpan.FromMilliseconds(1000.0 / _targetFps);
            long sequence = 0;

            while (!sessionCts.Token.IsCancellationRequested && tcpClient.Connected)
            {
                sequence++;
                var frame = await _frameProvider(sequence, sessionCts.Token);
                await _writeLock.WaitAsync(sessionCts.Token);
                try
                {
                    await LocalRelayServer.WriteFramedJsonAsync(
                        sslStream,
                        new RelayWireEnvelope("FRAME", Frame: frame),
                        sessionCts.Token);
                }
                finally
                {
                    _writeLock.Release();
                }

                await Task.Delay(frameInterval, sessionCts.Token);
            }

            await Task.WhenAny(receiveInputTask, Task.Delay(200));
        }
        catch
        {
            // Relay unreachable or session ended
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            _cts?.Cancel();
            if (_bridgeTask is not null)
            {
                await Task.WhenAny(_bridgeTask, Task.Delay(300));
            }
        }
        catch
        {
            // Ignore shutdown errors
        }
        finally
        {
            _cts?.Dispose();
            _writeLock.Dispose();
        }
    }
}

