using RemoteDesk.Contracts.Sessions;

namespace RemoteDesk.Transport.Abstractions;

public sealed record TransportConnectionResult(
    bool IsConnected,
    string ActiveTransportType,
    string? FailureReason = null);

public interface IRemoteTransport : IAsyncDisposable
{
    string TransportName { get; }

    event Action<ScreenFramePacket>? FrameReceived;

    event Action<InputControlAckPacket>? InputAckReceived;

    event Action<SessionDisconnectPacket>? RemoteDisconnected;

    StreamTelemetrySnapshot GetTelemetrySnapshot();

    Task<TransportConnectionResult> ConnectAsync(
        SessionConnectionInfo session,
        CancellationToken cancellationToken);

    Task SendInputEventAsync(
        RemoteInputEventPacket inputEvent,
        CancellationToken cancellationToken);

    Task DisconnectAsync(CancellationToken cancellationToken);
}
