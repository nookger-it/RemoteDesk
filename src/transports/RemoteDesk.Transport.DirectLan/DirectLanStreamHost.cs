using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using RemoteDesk.Contracts.Sessions;
using RemoteDesk.Transport.Abstractions;

namespace RemoteDesk.Transport.DirectLan;

public delegate bool SessionTicketValidator(
    Guid sessionId,
    Guid deviceId,
    string sessionTicket,
    out bool allowInputControl,
    out string? errorMessage);

/// <summary>
/// Encrypted TCP/TLS Direct LAN server hosted by Agent.Interactive (ports 52000-52010).
/// Validates the short-lived SessionTicket before streaming screen frames and enforces
/// the Input Permission Gate on incoming mouse/keyboard events.
/// </summary>
public sealed class DirectLanStreamHost : IAsyncDisposable
{
    private readonly int _port;
    private readonly Guid _deviceId;
    private readonly SessionTicketValidator _ticketValidator;
    private readonly Func<long, CancellationToken, Task<ScreenFramePacket>> _frameProvider;
    private readonly Func<RemoteInputEventPacket, bool, CancellationToken, Task<InputControlAckPacket>>? _inputHandler;
    private readonly Action<StreamQualityProfile>? _qualityHandler;
    private int _targetFps;
    private readonly X509Certificate2 _ephemeralCert;
    private readonly SemaphoreSlim _activeStreamWriteLock = new(1, 1);

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoopTask;
    private SslStream? _currentClientStream;
    private Guid _currentSessionId;

    public DirectLanStreamHost(
        int port,
        Guid deviceId,
        SessionTicketValidator ticketValidator,
        Func<long, CancellationToken, Task<ScreenFramePacket>> frameProvider,
        int targetFps = 12,
        Func<RemoteInputEventPacket, bool, CancellationToken, Task<InputControlAckPacket>>? inputHandler = null,
        Action<StreamQualityProfile>? qualityHandler = null)
    {
        _port = port;
        _deviceId = deviceId;
        _ticketValidator = ticketValidator;
        _frameProvider = frameProvider;
        _targetFps = Math.Clamp(targetFps, 1, 60);
        _inputHandler = inputHandler;
        _qualityHandler = qualityHandler;
        _ephemeralCert = CreateEphemeralTlsCertificate();
    }

    public int BoundPort { get; private set; }

    /// <summary>Target frames per second; can be changed live during a session.</summary>
    public int TargetFps
    {
        get => Volatile.Read(ref _targetFps);
        set => Volatile.Write(ref _targetFps, Math.Clamp(value, 1, 60));
    }

    /// <summary>True while an Operator is connected over Direct LAN.</summary>
    public bool HasActiveSession => _currentClientStream is not null;

    public event Action<Guid, string>? SessionEndedByPeerOrLocal;

    public void Start()
    {
        if (OperatingSystem.IsWindows())
        {
            _ = timeBeginPeriod(1);
        }

        _listener = new TcpListener(IPAddress.Any, _port);
        _listener.Start();
        BoundPort = ((IPEndPoint)_listener.LocalEndpoint).Port;

        _cts = new CancellationTokenSource();
        _acceptLoopTask = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    /// <summary>
    /// Allows the local user at the Agent PC to immediately terminate the active remote session.
    /// </summary>
    public async Task DisconnectActiveSessionByLocalUserAsync(string reason = "Local user pressed Disconnect on Agent PC")
    {
        var stream = _currentClientStream;
        var sessionId = _currentSessionId;
        if (stream is not null && sessionId != Guid.Empty)
        {
            await _activeStreamWriteLock.WaitAsync(TimeSpan.FromMilliseconds(200));
            try
            {
                await DirectLanTransport.WriteFramedJsonAsync(
                    stream,
                    new DirectLanWireEnvelope(
                        "DISCONNECT",
                        Disconnect: new SessionDisconnectPacket(sessionId, "AgentLocalUser", reason)),
                    CancellationToken.None);
            }
            catch
            {
                // Ignore if client already closed
            }
            finally
            {
                _activeStreamWriteLock.Release();
            }

            try
            {
                stream.Close();
            }
            catch
            {
                // Ignore
            }
        }

        if (sessionId != Guid.Empty)
        {
            SessionEndedByPeerOrLocal?.Invoke(sessionId, reason);
        }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener is not null)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(ct);
                client.NoDelay = true;
                client.SendBufferSize = 262144;
                client.ReceiveBufferSize = 65536;
                _ = Task.Run(() => HandleClientAsync(client, ct), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Continue accepting subsequent connections
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            using var sslStream = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
            await sslStream.AuthenticateAsServerAsync(
                new SslServerAuthenticationOptions
                {
                    ServerCertificate = _ephemeralCert,
                    ClientCertificateRequired = false,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
                },
                ct);

            var req = await DirectLanTransport.ReadFramedJsonAsync<DirectLanHandshakeRequest>(sslStream, ct);
            if (req is null)
            {
                return;
            }

            string? error = null;
            bool allowInputControl = false;
            if ((_deviceId != Guid.Empty && req.DeviceId != _deviceId) ||
                !_ticketValidator(req.SessionId, req.DeviceId, req.SessionTicket, out allowInputControl, out error))
            {
                await DirectLanTransport.WriteFramedJsonAsync(
                    sslStream,
                    new DirectLanHandshakeResponse(false, false, error ?? "Invalid session ticket."),
                    ct);
                return;
            }

            await DirectLanTransport.WriteFramedJsonAsync(
                sslStream,
                new DirectLanHandshakeResponse(true, allowInputControl, null),
                ct);

            _currentClientStream = sslStream;
            _currentSessionId = req.SessionId;

            using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            var receiveInputTask = Task.Run(async () =>
            {
                try
                {
                    while (!sessionCts.Token.IsCancellationRequested)
                    {
                        var envelope = await DirectLanTransport.ReadFramedJsonAsync<DirectLanWireEnvelope>(sslStream, sessionCts.Token);
                        if (envelope is null)
                        {
                            break;
                        }

                        if (envelope.PacketType == "INPUT" && envelope.Input is { } inputPacket && _inputHandler is not null)
                        {
                            var ack = await _inputHandler(inputPacket, allowInputControl, sessionCts.Token);
                            await _activeStreamWriteLock.WaitAsync(sessionCts.Token);
                            try
                            {
                                await DirectLanTransport.WriteFramedJsonAsync(
                                    sslStream,
                                    new DirectLanWireEnvelope("INPUT_ACK", InputAck: ack),
                                    sessionCts.Token);
                            }
                            finally
                            {
                                _activeStreamWriteLock.Release();
                            }
                        }
                        else if (envelope.PacketType == "QUALITY" && envelope.Quality is { } qualityProfile)
                        {
                            _qualityHandler?.Invoke(qualityProfile);
                        }
                        else if (envelope.PacketType == "FILE_CHUNK" && envelope.FileChunk is { } fileChunk)
                        {
                            var fileAck = await ProcessIncomingFileChunkAsync(fileChunk, allowInputControl);
                            await _activeStreamWriteLock.WaitAsync(sessionCts.Token);
                            try
                            {
                                await DirectLanTransport.WriteFramedJsonAsync(
                                    sslStream,
                                    new DirectLanWireEnvelope("FILE_ACK", FileAck: fileAck),
                                    sessionCts.Token);
                            }
                            finally
                            {
                                _activeStreamWriteLock.Release();
                            }
                        }
                        else if (envelope.PacketType == "DISCONNECT")
                        {
                            SessionEndedByPeerOrLocal?.Invoke(req.SessionId, envelope.Disconnect?.Reason ?? "Operator disconnected");
                            break;
                        }
                    }
                }
                catch
                {
                    // Client closed stream
                }
                finally
                {
                    sessionCts.Cancel();
                }
            }, sessionCts.Token);

            long sequence = 0;
            var pacing = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                // Pipeline: capture/encode the NEXT frame while the current one is being sent over TLS.
                var nextFrameTask = _frameProvider(++sequence, sessionCts.Token);

                while (!sessionCts.Token.IsCancellationRequested && client.Connected)
                {
                    var frameStartedAt = pacing.Elapsed;
                    var frame = await nextFrameTask;
                    nextFrameTask = _frameProvider(++sequence, sessionCts.Token);

                    // Empty payload = screen unchanged since the last frame => skip sending (saves bandwidth)
                    if (frame.Payload is { Length: > 0 })
                    {
                        await _activeStreamWriteLock.WaitAsync(sessionCts.Token);
                        try
                        {
                            await DirectLanTransport.WriteBinaryFrameAsync(sslStream, frame, sessionCts.Token);
                        }
                        finally
                        {
                            _activeStreamWriteLock.Release();
                        }
                    }

                    // Deadline pacing: only wait for what remains of the frame interval
                    var frameInterval = TimeSpan.FromMilliseconds(1000.0 / TargetFps);
                    var remaining = frameInterval - (pacing.Elapsed - frameStartedAt);
                    if (remaining >= TimeSpan.FromMilliseconds(3))
                    {
                        await Task.Delay(remaining, sessionCts.Token);
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
                await Task.WhenAny(receiveInputTask, Task.Delay(200));
                if (_currentClientStream == sslStream)
                {
                    _currentClientStream = null;
                    _currentSessionId = Guid.Empty;
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            _cts?.Cancel();
            _listener?.Stop();
            if (_acceptLoopTask is not null)
            {
                await Task.WhenAny(_acceptLoopTask, Task.Delay(300));
            }
        }
        catch
        {
            // Ignore shutdown errors
        }
        finally
        {
            _ephemeralCert.Dispose();
            _cts?.Dispose();
            _activeStreamWriteLock.Dispose();
        }
    }

    private static async Task<FileTransferAckPacket> ProcessIncomingFileChunkAsync(
        FileTransferPacket chunk,
        bool allowInputControl)
    {
        if (!allowInputControl)
        {
            return new FileTransferAckPacket(
                chunk.SessionId,
                chunk.TransferId,
                chunk.FileName,
                chunk.ChunkIndex,
                chunk.TotalChunks,
                Completed: false,
                Accepted: false,
                SavedPathOrError: "Permission denied: Session is View-Only.");
        }

        try
        {
            var safeName = Path.GetFileName(chunk.FileName);
            if (string.IsNullOrWhiteSpace(safeName))
            {
                safeName = $"file_{chunk.TransferId:N}.bin";
            }

            var desktopDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (string.IsNullOrWhiteSpace(desktopDir))
            {
                desktopDir = AppContext.BaseDirectory;
            }

            var receiveDir = Path.Combine(desktopDir, "RemoteDesk_ReceivedFiles");
            Directory.CreateDirectory(receiveDir);
            var targetPath = Path.Combine(receiveDir, safeName);

            var fileMode = chunk.ChunkIndex == 0 ? FileMode.Create : FileMode.Append;
            await using (var fs = new FileStream(targetPath, fileMode, FileAccess.Write, FileShare.Read, 65536, useAsync: true))
            {
                if (chunk.Data is { Length: > 0 })
                {
                    await fs.WriteAsync(chunk.Data);
                }
            }

            bool completed = (chunk.ChunkIndex + 1) >= Math.Max(1, chunk.TotalChunks);
            if (completed)
            {
                Console.WriteLine($"[Agent.Interactive] Received file '{safeName}' ({chunk.TotalBytes:N0} bytes) -> {targetPath}");
            }

            return new FileTransferAckPacket(
                chunk.SessionId,
                chunk.TransferId,
                safeName,
                chunk.ChunkIndex,
                chunk.TotalChunks,
                Completed: completed,
                Accepted: true,
                SavedPathOrError: targetPath);
        }
        catch (Exception ex)
        {
            return new FileTransferAckPacket(
                chunk.SessionId,
                chunk.TransferId,
                chunk.FileName,
                chunk.ChunkIndex,
                chunk.TotalChunks,
                Completed: false,
                Accepted: false,
                SavedPathOrError: ex.Message);
        }
    }

    private static X509Certificate2 CreateEphemeralTlsCertificate()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(
            "CN=remotedesk-agent.local",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        var cert = req.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddDays(7));

        var pfxBytes = cert.Export(X509ContentType.Pfx);
        return X509CertificateLoader.LoadPkcs12(pfxBytes, password: null, X509KeyStorageFlags.Exportable);
    }

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint uPeriod);
}
