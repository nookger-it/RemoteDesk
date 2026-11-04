namespace RemoteDesk.Contracts.Sessions;

public sealed record DeviceEndpointDto(
    string EndpointType,
    string Address,
    int Port);

public sealed record DeviceEnrollmentRequest(
    Guid OrganizationId,
    Guid SiteId,
    Guid? ExistingDeviceId,
    string Hostname,
    string AgentVersion,
    string PublicKey,
    IReadOnlyList<DeviceEndpointDto> Endpoints);

public sealed record DeviceEnrollmentResponse(
    Guid DeviceId,
    string Status,
    int HeartbeatIntervalSeconds);

public sealed record DeviceHeartbeatRequest(
    Guid DeviceId,
    string AgentVersion,
    IReadOnlyList<DeviceEndpointDto> Endpoints);

public sealed record DeviceHeartbeatResponse(
    Guid DeviceId,
    string Status,
    DateTimeOffset ServerTimeUtc,
    SessionStartInstructionDto? PendingSession = null);

public sealed record SessionStartInstructionDto(
    Guid SessionId,
    Guid OperatorUserId,
    string OperatorDisplayName,
    string SessionTicket,
    DateTimeOffset TicketExpiresAt,
    int DirectPort,
    bool AllowInputControl);

public sealed record DeviceSummaryDto(
    Guid Id,
    Guid OrganizationId,
    string OrganizationName,
    Guid SiteId,
    string SiteName,
    string Hostname,
    string AgentVersion,
    string Status,
    bool IsOnline,
    DateTimeOffset LastSeenAt,
    IReadOnlyList<DeviceEndpointDto> Endpoints);

public sealed record RevokeDeviceRequest(
    Guid? ActorUserId,
    string Reason);

public sealed record OrganizationDto(
    Guid Id,
    string Name,
    string Status);

public sealed record SiteDto(
    Guid Id,
    Guid OrganizationId,
    string Name,
    string Status);

public sealed record DashboardOverviewDto(
    int TotalDevices,
    int OnlineDevices,
    int OfflineDevices,
    int RevokedDevices,
    int ActiveSessions,
    int MaxConcurrentSessions,
    IReadOnlyList<OrganizationDto> Organizations,
    IReadOnlyList<SiteDto> Sites);

public sealed record StartSessionRequest(
    Guid OrganizationId,
    Guid DeviceId,
    Guid OperatorUserId,
    string OperatorDisplayName,
    bool AllowInputControl,
    string? IdempotencyKey = null,
    int? LeaseDurationSeconds = null);

public sealed record StartSessionResponse(
    SessionConnectionInfo ConnectionInfo,
    int ReservedSlotIndex);

public sealed record EndSessionRequest(
    Guid? ActorUserId,
    string Reason);

public sealed record SessionConnectionInfo(
    Guid SessionId,
    Guid DeviceId,
    Guid OperatorUserId,
    string SessionTicket,
    DateTimeOffset TicketExpiresAt,
    string DirectAddress,
    int DirectPort,
    string RelayAddress,
    int RelayPort,
    bool AllowInputControl);

public sealed record ScreenFramePacket(
    Guid SessionId,
    long FrameSequence,
    long CapturedAtUnixMs,
    int Width,
    int Height,
    string Encoding,
    byte[] Payload);

public sealed record StreamTelemetrySnapshot(
    double CurrentFps,
    double AverageLatencyMs,
    double BandwidthKbps,
    long TotalFramesReceived,
    long TotalBytesReceived);

public enum RemoteInputEventType
{
    MouseMove = 1,
    MouseDown = 2,
    MouseUp = 3,
    MouseWheel = 4,
    KeyDown = 5,
    KeyUp = 6
}

public enum MouseButtonType
{
    None = 0,
    Left = 1,
    Right = 2,
    Middle = 3
}

public sealed record RemoteInputEventPacket(
    Guid SessionId,
    RemoteInputEventType EventType,
    double NormalizedX,
    double NormalizedY,
    MouseButtonType MouseButton,
    int WheelDelta,
    int VirtualKeyCode,
    long SentAtUnixMs);

public sealed record InputControlAckPacket(
    Guid SessionId,
    bool Accepted,
    string? Reason,
    long ProcessedEventsCount,
    long BlockedEventsCount);

public sealed record SessionDisconnectPacket(
    Guid SessionId,
    string InitiatedBy,
    string Reason);

public sealed record OperatorLoginRequest(
    string Email,
    string Password,
    string? MfaCode = null);

public sealed record OperatorLoginResponse(
    Guid UserId,
    Guid OrganizationId,
    string Email,
    string DisplayName,
    IReadOnlyList<string> Roles,
    string AccessToken,
    DateTimeOffset ExpiresAt);

public sealed record GrantDevicePermissionRequest(
    Guid UserId,
    string PermissionLevel,
    Guid ApprovedByUserId,
    DateTimeOffset? ExpiresAt = null);

public sealed record SessionLeaseRenewalRequest(
    Guid OperatorUserId,
    int ExtendSeconds = 60);

public sealed record ActiveSessionDto(
    Guid SessionId,
    Guid DeviceId,
    string DeviceHostname,
    Guid OperatorUserId,
    int SlotIndex,
    string TransportType,
    string Status,
    DateTimeOffset StartedAt,
    DateTimeOffset LeaseExpiresAt);

public sealed record AuditLogDto(
    Guid Id,
    Guid OrganizationId,
    Guid? ActorUserId,
    Guid? DeviceId,
    Guid? SessionId,
    string Action,
    string Result,
    string SourceIp,
    DateTimeOffset CreatedAt);

public sealed record FileTransferPacket(
    Guid SessionId,
    Guid TransferId,
    string FileName,
    long TotalBytes,
    int ChunkIndex,
    int TotalChunks,
    byte[] Data);

public sealed record FileTransferAckPacket(
    Guid SessionId,
    Guid TransferId,
    string FileName,
    int ChunkIndex,
    int TotalChunks,
    bool Completed,
    bool Accepted,
    string? SavedPathOrError);

public sealed record ClipboardSyncPacket(
    Guid SessionId,
    string Text,
    bool TriggerPaste = false);




