using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using RemoteDesk.Contracts.Sessions;

namespace RemoteDesk.Transport.LocalRelay;

public delegate bool RelayTicketValidator(
    Guid sessionId,
    Guid deviceId,
    string sessionTicket,
    out bool allowInputControl,
    out string? errorMessage);

internal sealed record RelayHandshakeRequest(
    Guid SessionId,
    Guid DeviceId,
    string PeerRole, // "AGENT" or "OPERATOR"
    string SessionTicket);

internal sealed record RelayHandshakeResponse(
    bool Accepted,
    bool AllowInputControl,
    string? ErrorMessage);

internal sealed record RelayWireEnvelope(
    string PacketType,
    ScreenFramePacket? Frame = null,
    RemoteInputEventPacket? Input = null,
    InputControlAckPacket? InputAck = null,
    SessionDisconnectPacket? Disconnect = null);

/// <summary>
/// Encrypted TCP/TLS Local Relay server (default port 52100).
/// Authenticates both Agent and Operator using their short-lived SessionTicket,
/// pairs them by SessionId, and forwards encrypted stream frames and input packets.
/// </summary>
public sealed class LocalRelayServer : IAsyncDisposable
{
    private sealed class PendingRelaySlot
    {
        public TcpClient? AgentClient { get; set; }
        public SslStream? AgentStream { get; set; }
        public TcpClient? OperatorClient { get; set; }
        public SslStream? OperatorStream { get; set; }
        public bool AllowInputControl { get; set; }
        public TaskCompletionSource<bool> PairedTcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> PumpCompletedTcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public object SyncRoot { get; } = new();
    }

    private readonly int _port;
    private readonly RelayTicketValidator _ticketValidator;
    private readonly X509Certificate2 _ephemeralCert;
    private readonly ConcurrentDictionary<Guid, PendingRelaySlot> _slots = new();

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoopTask;

    public LocalRelayServer(int port, RelayTicketValidator ticketValidator)
    {
        _port = port;
        _ticketValidator = ticketValidator;
        _ephemeralCert = CreateEphemeralTlsCertificate();
    }

    public int BoundPort { get; private set; }

    public void Start()
    {
        _listener = new TcpListener(IPAddress.Any, _port);
        _listener.Start();
        BoundPort = ((IPEndPoint)_listener.LocalEndpoint).Port;

        _cts = new CancellationTokenSource();
        _acceptLoopTask = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener is not null)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(ct);
                client.NoDelay = true;
                _ = Task.Run(() => HandlePeerAsync(client, ct), ct);
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

    private async Task HandlePeerAsync(TcpClient client, CancellationToken ct)
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

            var req = await ReadFramedJsonAsync<RelayHandshakeRequest>(sslStream, ct);
            if (req is null)
            {
                return;
            }

            if (!_ticketValidator(req.SessionId, req.DeviceId, req.SessionTicket, out var allowInputControl, out var error))
            {
                await WriteFramedJsonAsync(
                    sslStream,
                    new RelayHandshakeResponse(false, false, error ?? "Relay rejected session ticket."),
                    ct);
                return;
            }

            var slot = _slots.GetOrAdd(req.SessionId, _ => new PendingRelaySlot());
            bool bothReady = false;
            bool isOperator = string.Equals(req.PeerRole, "OPERATOR", StringComparison.OrdinalIgnoreCase);

            lock (slot.SyncRoot)
            {
                slot.AllowInputControl = allowInputControl;
                if (isOperator)
                {
                    slot.OperatorClient = client;
                    slot.OperatorStream = sslStream;
                }
                else
                {
                    slot.AgentClient = client;
                    slot.AgentStream = sslStream;
                }

                if (slot.AgentStream is not null && slot.OperatorStream is not null)
                {
                    bothReady = true;
                    slot.PairedTcs.TrySetResult(true);
                }
            }

            var pairedTask = slot.PairedTcs.Task;
            var completed = await Task.WhenAny(pairedTask, Task.Delay(TimeSpan.FromSeconds(10), ct));
            if (completed != pairedTask)
            {
                _slots.TryRemove(req.SessionId, out _);
                await WriteFramedJsonAsync(
                    sslStream,
                    new RelayHandshakeResponse(false, false, "Timed out waiting for peer on Local Relay."),
                    ct);
                return;
            }

            if (bothReady)
            {
                try
                {
                    await WriteFramedJsonAsync(
                        slot.AgentStream!,
                        new RelayHandshakeResponse(true, slot.AllowInputControl, null),
                        ct);
                    await WriteFramedJsonAsync(
                        slot.OperatorStream!,
                        new RelayHandshakeResponse(true, slot.AllowInputControl, null),
                        ct);

                    using var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    var agentToOp = PumpRawFramesAsync(slot.AgentStream!, slot.OperatorStream!, pumpCts);
                    var opToAgent = PumpRawFramesAsync(slot.OperatorStream!, slot.AgentStream!, pumpCts);

                    await Task.WhenAny(agentToOp, opToAgent);
                    pumpCts.Cancel();
                    await Task.WhenAll(
                        agentToOp.ContinueWith(_ => { }),
                        opToAgent.ContinueWith(_ => { }));
                }
                finally
                {
                    _slots.TryRemove(req.SessionId, out _);
                    slot.PumpCompletedTcs.TrySetResult(true);
                }
            }
            else
            {
                await slot.PumpCompletedTcs.Task.WaitAsync(ct);
            }
        }
    }

    private static async Task PumpRawFramesAsync(
        Stream source,
        Stream destination,
        CancellationTokenSource sharedCts)
    {
        var lengthBuf = new byte[4];
        try
        {
            while (!sharedCts.Token.IsCancellationRequested)
            {
                var readLen = await ReadExactAsync(source, lengthBuf, sharedCts.Token);
                if (readLen == 0)
                {
                    break;
                }

                var frameLen = BinaryPrimitives.ReadInt32BigEndian(lengthBuf);
                if (frameLen <= 0 || frameLen > 16 * 1024 * 1024)
                {
                    break;
                }

                var payload = new byte[frameLen];
                await ReadExactAsync(source, payload, sharedCts.Token);

                await destination.WriteAsync(lengthBuf, sharedCts.Token);
                await destination.WriteAsync(payload, sharedCts.Token);
                await destination.FlushAsync(sharedCts.Token);
            }
        }
        catch
        {
            // Peer closed connection
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
        }
    }

    internal static async Task WriteFramedJsonAsync<T>(Stream stream, T value, CancellationToken ct)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(value);
        var lengthPrefix = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(lengthPrefix, payload.Length);
        await stream.WriteAsync(lengthPrefix, ct);
        await stream.WriteAsync(payload, ct);
        await stream.FlushAsync(ct);
    }

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
            throw new InvalidDataException($"Invalid relay frame length: {length}");
        }

        var buffer = new byte[length];
        await ReadExactAsync(stream, buffer, ct);
        return JsonSerializer.Deserialize<T>(buffer);
    }

    internal static async Task<int> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
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

    private static X509Certificate2 CreateEphemeralTlsCertificate()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(
            "CN=remotedesk-relay.local",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        var cert = req.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddDays(7));

        var pfxBytes = cert.Export(X509ContentType.Pfx);
        return X509CertificateLoader.LoadPkcs12(pfxBytes, password: null, X509KeyStorageFlags.Exportable);
    }
}

