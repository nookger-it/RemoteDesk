namespace RemoteDesk.Domain.Entities;

public enum EntityStatus
{
    Active = 1,
    Disabled = 2,
    Revoked = 3
}

public enum DeviceStatus
{
    Offline = 0,
    Online = 1,
    Disabled = 2,
    Revoked = 3
}

public enum EndpointType
{
    DirectLan = 1,
    LocalRelay = 2
}

public enum RemotePermissionLevel
{
    None = 0,
    ViewOnly = 1,
    ViewAndControl = 2
}

public enum TransportType
{
    DirectLan = 1,
    LocalRelay = 2
}

public enum SessionStatus
{
    Reserved = 1,
    Active = 2,
    Ended = 3,
    TimedOut = 4,
    Failed = 5
}

public sealed class Organization
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public EntityStatus Status { get; set; } = EntityStatus.Active;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class Site
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required Guid OrganizationId { get; set; }
    public required string Name { get; set; }
    public EntityStatus Status { get; set; } = EntityStatus.Active;
}

public sealed class User
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required Guid OrganizationId { get; set; }
    public required string Email { get; set; }
    public required string DisplayName { get; set; }
    public EntityStatus Status { get; set; } = EntityStatus.Active;
}

public sealed class Role
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required Guid OrganizationId { get; set; }
    public required string Name { get; set; }
}

public sealed class UserRole
{
    public required Guid UserId { get; set; }
    public required Guid RoleId { get; set; }
}

public sealed class Device
{
    /// <summary>
    /// Persistent UUID representing the device identity (never IP-based).
    /// </summary>
    public Guid Id { get; set; } = Guid.NewGuid();
    public required Guid OrganizationId { get; set; }
    public required Guid SiteId { get; set; }
    public required string Hostname { get; set; }
    public required string AgentVersion { get; set; }
    public required string PublicKey { get; set; }
    public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.UtcNow;
    public DeviceStatus Status { get; set; } = DeviceStatus.Offline;
}

public sealed class DeviceEndpoint
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required Guid DeviceId { get; set; }
    public required EndpointType EndpointType { get; set; }
    public required string Address { get; set; }
    public required int Port { get; set; }
    public DateTimeOffset LastVerifiedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class DevicePermission
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required Guid DeviceId { get; set; }
    public Guid? UserId { get; set; }
    public Guid? RoleId { get; set; }
    public required RemotePermissionLevel Permission { get; set; }
    public required Guid ApprovedBy { get; set; }
    public DateTimeOffset ApprovedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ExpiresAt { get; set; }
}

public sealed class RemoteSession
{
    public const int MaxConcurrentSessions = 5;

    public Guid Id { get; set; } = Guid.NewGuid();
    public required Guid OrganizationId { get; set; }
    public required Guid DeviceId { get; set; }
    public required Guid OperatorUserId { get; set; }
    public required TransportType TransportType { get; set; }
    public required int SlotIndex { get; set; }
    public SessionStatus Status { get; set; } = SessionStatus.Reserved;
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LeaseExpiresAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public string? EndReason { get; set; }
}

public sealed class AuditLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required Guid OrganizationId { get; set; }
    public Guid? ActorUserId { get; set; }
    public Guid? DeviceId { get; set; }
    public Guid? SessionId { get; set; }
    public required string Action { get; set; }
    public required string Result { get; set; }
    public required string SourceIp { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

