using RemoteDesk.Contracts.Sessions;

namespace RemoteDesk.Transport.Abstractions;

/// <summary>
/// Implements the V1 Hybrid LAN Connection Policy:
/// 1. Try Direct LAN first (low latency, zero server bandwidth).
/// 2. If Direct LAN fails or times out (cross-subnet / firewall), automatically fallback to Local Server Relay.
/// </summary>
public sealed class HybridLanTransportOrchestrator : IRemoteTransport, IStreamQualityControl
{
    private readonly IRemoteTransport _directTransport;
    private readonly IRemoteTransport _relayTransport;
    private readonly TimeSpan _directAttemptTimeout;
    private IRemoteTransport? _activeTransport;

    public HybridLanTransportOrchestrator(
        IRemoteTransport directTransport,
        IRemoteTransport relayTransport,
        TimeSpan? directAttemptTimeout = null)
    {
        _directTransport = directTransport ?? throw new ArgumentNullException(nameof(directTransport));
        _relayTransport = relayTransport ?? throw new ArgumentNullException(nameof(relayTransport));
        _directAttemptTimeout = directAttemptTimeout ?? TimeSpan.FromMilliseconds(1500);
    }

    public string TransportName => _activeTransport?.TransportName ?? "HybridLan";

    public event Action<ScreenFramePacket>? FrameReceived;

    public event Action<InputControlAckPacket>? InputAckReceived;

    public event Action<SessionDisconnectPacket>? RemoteDisconnected;

    public async Task<TransportConnectionResult> ConnectAsync(
        SessionConnectionInfo session,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);

        // Step 1: Try Direct LAN first
        using (var directCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            directCts.CancelAfter(_directAttemptTimeout);
            try
            {
                AttachEvents(_directTransport);
                var directRes = await _directTransport.ConnectAsync(session, directCts.Token);
                if (directRes.IsConnected)
                {
                    _activeTransport = _directTransport;
                    return directRes;
                }

                DetachEvents(_directTransport);
            }
            catch
            {
                DetachEvents(_directTransport);
            }
        }

        // Step 2: Automatic fallback to Local Server Relay
        AttachEvents(_relayTransport);
        var relayRes = await _relayTransport.ConnectAsync(session, cancellationToken);
        if (relayRes.IsConnected)
        {
            _activeTransport = _relayTransport;
            return relayRes;
        }

        DetachEvents(_relayTransport);
        return new TransportConnectionResult(
            IsConnected: false,
            ActiveTransportType: "None",
            FailureReason: $"Both Direct LAN and Local Relay failed. Relay error: {relayRes.FailureReason}");
    }

    public Task SendInputEventAsync(
        RemoteInputEventPacket inputEvent,
        CancellationToken cancellationToken)
    {
        if (_activeTransport is null)
        {
            throw new InvalidOperationException("No active transport connection.");
        }

        return _activeTransport.SendInputEventAsync(inputEvent, cancellationToken);
    }

    public Task SetQualityProfileAsync(StreamQualityProfile profile, CancellationToken cancellationToken) =>
        _activeTransport is IStreamQualityControl qualityControl
            ? qualityControl.SetQualityProfileAsync(profile, cancellationToken)
            : Task.CompletedTask;

    public StreamTelemetrySnapshot GetTelemetrySnapshot() =>
        _activeTransport?.GetTelemetrySnapshot() ?? new StreamTelemetrySnapshot(0, 0, 0, 0, 0);

    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        if (_activeTransport is not null)
        {
            DetachEvents(_activeTransport);
            await _activeTransport.DisconnectAsync(cancellationToken);
            _activeTransport = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync(CancellationToken.None);
        await _directTransport.DisposeAsync();
        await _relayTransport.DisposeAsync();
    }

    private void AttachEvents(IRemoteTransport transport)
    {
        transport.FrameReceived += OnFrameReceived;
        transport.InputAckReceived += OnInputAckReceived;
        transport.RemoteDisconnected += OnRemoteDisconnected;
    }

    private void DetachEvents(IRemoteTransport transport)
    {
        transport.FrameReceived -= OnFrameReceived;
        transport.InputAckReceived -= OnInputAckReceived;
        transport.RemoteDisconnected -= OnRemoteDisconnected;
    }

    private void OnFrameReceived(ScreenFramePacket frame) => FrameReceived?.Invoke(frame);
    private void OnInputAckReceived(InputControlAckPacket ack) => InputAckReceived?.Invoke(ack);
    private void OnRemoteDisconnected(SessionDisconnectPacket disc) => RemoteDisconnected?.Invoke(disc);
}

