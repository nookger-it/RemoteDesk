using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using RemoteDesk.Application.Abstractions;
using RemoteDesk.Application.Security;
using RemoteDesk.Contracts.Sessions;
using RemoteDesk.Domain.Entities;

namespace RemoteDesk.Infrastructure.Persistence;

public sealed class RemoteDeskManagementService : IRemoteDeskManagementService
{
    public static readonly TimeSpan HeartbeatOnlineThreshold = TimeSpan.FromSeconds(45);
    public static readonly TimeSpan SessionTicketLifetime = TimeSpan.FromMinutes(2);
    public const int DefaultHeartbeatIntervalSeconds = 10;
    public const int DefaultDirectLanPort = 52000;
    public const int DefaultRelayPort = 52100;

    private static readonly SemaphoreSlim SessionSlotLock = new(1, 1);
    private static readonly ConcurrentDictionary<Guid, SessionStartInstructionDto> PendingAgentSessions = new();
    private static readonly ConcurrentDictionary<string, StartSessionResponse> IdempotentSessionCache = new();

    private readonly RemoteDeskDbContext _db;
    private readonly ISessionTicketService _ticketService;

    public RemoteDeskManagementService(
        RemoteDeskDbContext db,
        ISessionTicketService ticketService)
    {
        _db = db;
        _ticketService = ticketService;
    }

    public async Task<OperationResult<OperatorLoginResponse>> AuthenticateOperatorAsync(
        OperatorLoginRequest request,
        string sourceIp,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
        {
            return OperationResult<OperatorLoginResponse>.Failure("INVALID_CREDENTIALS", "Email and password are required.");
        }

        var normalizedEmail = request.Email.Trim().ToLowerInvariant();
        var user = await _db.Users.FirstOrDefaultAsync(
            u => u.Email.ToLower() == normalizedEmail && u.Status == EntityStatus.Active,
            cancellationToken);

        if (user is null || !string.Equals(request.Password, PilotDefaults.DefaultOperatorPassword, StringComparison.Ordinal))
        {
            await WriteAuditLogAsync(
                PilotDefaults.OrganizationId,
                user?.Id,
                null,
                null,
                "OPERATOR_LOGIN_FAILED",
                $"Invalid password for {normalizedEmail}",
                sourceIp,
                cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);

            return OperationResult<OperatorLoginResponse>.Failure("INVALID_CREDENTIALS", "Invalid email or password.");
        }

        var roleIds = await _db.UserRoles
            .Where(ur => ur.UserId == user.Id)
            .Select(ur => ur.RoleId)
            .ToListAsync(cancellationToken);

        var roles = await _db.Roles
            .Where(r => roleIds.Contains(r.Id))
            .Select(r => r.Name)
            .ToListAsync(cancellationToken);

        bool isAdmin = roles.Any(r => string.Equals(r, "OperatorAdmin", StringComparison.OrdinalIgnoreCase));
        if (isAdmin && !string.Equals(request.MfaCode, PilotDefaults.DefaultAdminMfaCode, StringComparison.Ordinal))
        {
            await WriteAuditLogAsync(
                user.OrganizationId,
                user.Id,
                null,
                null,
                "OPERATOR_MFA_FAILED",
                "Missing or invalid MFA code for OperatorAdmin",
                sourceIp,
                cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);

            return OperationResult<OperatorLoginResponse>.Failure("MFA_REQUIRED", "Valid MFA code is required for administrator accounts.");
        }

        var expiresAt = DateTimeOffset.UtcNow.AddHours(8);
        var tokenPayload = $"{user.Id}:{user.OrganizationId}:{expiresAt.ToUnixTimeSeconds()}";
        var tokenSig = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(tokenPayload + ":RemoteDeskAuth2026")));
        var accessToken = $"{Convert.ToBase64String(Encoding.UTF8.GetBytes(tokenPayload))}.{tokenSig}";

        await WriteAuditLogAsync(
            user.OrganizationId,
            user.Id,
            null,
            null,
            "OPERATOR_LOGIN_SUCCESS",
            $"Roles: {string.Join(',', roles)}",
            sourceIp,
            cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);

        return OperationResult<OperatorLoginResponse>.Success(
            new OperatorLoginResponse(
                user.Id,
                user.OrganizationId,
                user.Email,
                user.DisplayName,
                roles,
                accessToken,
                expiresAt));
    }

    public async Task<OperationResult<DeviceEnrollmentResponse>> EnrollDeviceAsync(
        DeviceEnrollmentRequest request,
        string sourceIp,
        CancellationToken cancellationToken)
    {
        var orgExists = await _db.Organizations.AnyAsync(
            o => o.Id == request.OrganizationId && o.Status == EntityStatus.Active,
            cancellationToken);
        if (!orgExists)
        {
            return OperationResult<DeviceEnrollmentResponse>.Failure("ORG_NOT_FOUND", "Organization not found or inactive.");
        }

        var siteExists = await _db.Sites.AnyAsync(
            s => s.Id == request.SiteId && s.OrganizationId == request.OrganizationId && s.Status == EntityStatus.Active,
            cancellationToken);
        if (!siteExists)
        {
            return OperationResult<DeviceEnrollmentResponse>.Failure("SITE_NOT_FOUND", "Site not found in organization.");
        }

        Device? device = null;
        if (request.ExistingDeviceId.HasValue && request.ExistingDeviceId.Value != Guid.Empty)
        {
            device = await _db.Devices.FirstOrDefaultAsync(
                d => d.Id == request.ExistingDeviceId.Value,
                cancellationToken);

            if (device is not null && (device.Status == DeviceStatus.Revoked || device.Status == DeviceStatus.Disabled))
            {
                await WriteAuditLogAsync(
                    request.OrganizationId,
                    null,
                    device.Id,
                    null,
                    "DEVICE_ENROLL_REJECTED",
                    $"Rejected enrollment for {device.Status} device",
                    sourceIp,
                    cancellationToken);
                await _db.SaveChangesAsync(cancellationToken);

                return OperationResult<DeviceEnrollmentResponse>.Failure(
                    "DEVICE_REVOKED",
                    $"Device is {device.Status} and cannot re-enroll.");
            }
        }

        var now = DateTimeOffset.UtcNow;
        if (device is null)
        {
            device = new Device
            {
                Id = Guid.NewGuid(),
                OrganizationId = request.OrganizationId,
                SiteId = request.SiteId,
                Hostname = request.Hostname.Trim(),
                AgentVersion = request.AgentVersion.Trim(),
                PublicKey = request.PublicKey.Trim(),
                LastSeenAt = now,
                Status = DeviceStatus.Online
            };
            _db.Devices.Add(device);
        }
        else
        {
            device.OrganizationId = request.OrganizationId;
            device.SiteId = request.SiteId;
            device.Hostname = request.Hostname.Trim();
            device.AgentVersion = request.AgentVersion.Trim();
            device.PublicKey = request.PublicKey.Trim();
            device.LastSeenAt = now;
            device.Status = DeviceStatus.Online;
        }

        await ReplaceEndpointsAsync(device.Id, request.Endpoints, now, cancellationToken);

        await WriteAuditLogAsync(
            device.OrganizationId,
            null,
            device.Id,
            null,
            "DEVICE_ENROLLED",
            "Success",
            sourceIp,
            cancellationToken);

        await _db.SaveChangesAsync(cancellationToken);

        return OperationResult<DeviceEnrollmentResponse>.Success(
            new DeviceEnrollmentResponse(
                device.Id,
                device.Status.ToString(),
                DefaultHeartbeatIntervalSeconds));
    }

    public async Task<OperationResult<DeviceHeartbeatResponse>> ProcessHeartbeatAsync(
        Guid deviceId,
        DeviceHeartbeatRequest request,
        string sourceIp,
        CancellationToken cancellationToken)
    {
        var device = await _db.Devices.FirstOrDefaultAsync(d => d.Id == deviceId, cancellationToken);
        if (device is null)
        {
            return OperationResult<DeviceHeartbeatResponse>.Failure("DEVICE_NOT_FOUND", "Device is not enrolled.");
        }

        if (device.Status == DeviceStatus.Revoked || device.Status == DeviceStatus.Disabled)
        {
            await WriteAuditLogAsync(
                device.OrganizationId,
                null,
                device.Id,
                null,
                "DEVICE_HEARTBEAT_REJECTED",
                $"Rejected heartbeat because device is {device.Status}",
                sourceIp,
                cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);

            return OperationResult<DeviceHeartbeatResponse>.Failure(
                "DEVICE_REVOKED",
                $"Device is {device.Status}. Remote access is disabled.");
        }

        var now = DateTimeOffset.UtcNow;
        device.LastSeenAt = now;
        device.AgentVersion = string.IsNullOrWhiteSpace(request.AgentVersion)
            ? device.AgentVersion
            : request.AgentVersion.Trim();
        device.Status = DeviceStatus.Online;

        if (request.Endpoints is { Count: > 0 })
        {
            await ReplaceEndpointsAsync(device.Id, request.Endpoints, now, cancellationToken);
        }

        await _db.SaveChangesAsync(cancellationToken);

        PendingAgentSessions.TryRemove(device.Id, out var pendingInstruction);

        return OperationResult<DeviceHeartbeatResponse>.Success(
            new DeviceHeartbeatResponse(
                device.Id,
                device.Status.ToString(),
                now,
                pendingInstruction));
    }

    public async Task<IReadOnlyList<DeviceSummaryDto>> GetDevicesAsync(
        Guid? organizationId,
        Guid? siteId,
        string? statusFilter,
        string? searchQuery,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var orgs = await _db.Organizations.ToDictionaryAsync(o => o.Id, o => o.Name, cancellationToken);
        var sites = await _db.Sites.ToDictionaryAsync(s => s.Id, s => s.Name, cancellationToken);

        var query = _db.Devices.AsNoTracking().AsQueryable();
        if (organizationId.HasValue)
        {
            query = query.Where(d => d.OrganizationId == organizationId.Value);
        }

        if (siteId.HasValue)
        {
            query = query.Where(d => d.SiteId == siteId.Value);
        }

        if (!string.IsNullOrWhiteSpace(searchQuery))
        {
            var term = searchQuery.Trim().ToLowerInvariant();
            query = query.Where(d => d.Hostname.ToLower().Contains(term));
        }

        var devices = await query.OrderBy(d => d.Hostname).ToListAsync(cancellationToken);
        var deviceIds = devices.Select(d => d.Id).ToList();
        var endpoints = await _db.DeviceEndpoints
            .AsNoTracking()
            .Where(e => deviceIds.Contains(e.DeviceId))
            .ToListAsync(cancellationToken);

        var endpointsByDevice = endpoints
            .GroupBy(e => e.DeviceId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<DeviceEndpointDto>)g
                    .Select(e => new DeviceEndpointDto(e.EndpointType.ToString(), e.Address, e.Port))
                    .ToList());

        var results = new List<DeviceSummaryDto>(devices.Count);
        foreach (var d in devices)
        {
            var (computedStatus, isOnline) = ComputeDeviceOnlineState(d, now);
            if (!string.IsNullOrWhiteSpace(statusFilter) &&
                !string.Equals(statusFilter, "ALL", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(computedStatus, statusFilter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            results.Add(new DeviceSummaryDto(
                d.Id,
                d.OrganizationId,
                orgs.GetValueOrDefault(d.OrganizationId, "Unknown Org"),
                d.SiteId,
                sites.GetValueOrDefault(d.SiteId, "Unknown Site"),
                d.Hostname,
                d.AgentVersion,
                computedStatus,
                isOnline,
                d.LastSeenAt,
                endpointsByDevice.GetValueOrDefault(d.Id, Array.Empty<DeviceEndpointDto>())));
        }

        return results;
    }

    public async Task<DeviceSummaryDto?> GetDeviceByIdAsync(
        Guid deviceId,
        CancellationToken cancellationToken)
    {
        var list = await GetDevicesAsync(null, null, null, null, cancellationToken);
        return list.FirstOrDefault(d => d.Id == deviceId);
    }

    public async Task<OperationResult<DeviceSummaryDto>> RevokeOrDisableDeviceAsync(
        Guid deviceId,
        bool permanentRevoke,
        RevokeDeviceRequest request,
        string sourceIp,
        CancellationToken cancellationToken)
    {
        var device = await _db.Devices.FirstOrDefaultAsync(d => d.Id == deviceId, cancellationToken);
        if (device is null)
        {
            return OperationResult<DeviceSummaryDto>.Failure("DEVICE_NOT_FOUND", "Device not found.");
        }

        device.Status = permanentRevoke ? DeviceStatus.Revoked : DeviceStatus.Disabled;

        var activeSessions = await _db.Sessions
            .Where(s => s.DeviceId == deviceId && (s.Status == SessionStatus.Reserved || s.Status == SessionStatus.Active))
            .ToListAsync(cancellationToken);

        var now = DateTimeOffset.UtcNow;
        foreach (var s in activeSessions)
        {
            s.Status = SessionStatus.Ended;
            s.EndedAt = now;
            s.EndReason = permanentRevoke ? "Device revoked" : "Device disabled";
        }

        await WriteAuditLogAsync(
            device.OrganizationId,
            request.ActorUserId,
            device.Id,
            null,
            permanentRevoke ? "DEVICE_REVOKED" : "DEVICE_DISABLED",
            request.Reason,
            sourceIp,
            cancellationToken);

        await _db.SaveChangesAsync(cancellationToken);

        var summary = await GetDeviceByIdAsync(deviceId, cancellationToken);
        return OperationResult<DeviceSummaryDto>.Success(summary!);
    }

    public async Task<OperationResult<DeviceSummaryDto>> EnableDeviceAsync(
        Guid deviceId,
        string sourceIp,
        CancellationToken cancellationToken)
    {
        var device = await _db.Devices.FirstOrDefaultAsync(d => d.Id == deviceId, cancellationToken);
        if (device is null)
        {
            return OperationResult<DeviceSummaryDto>.Failure("DEVICE_NOT_FOUND", "Device not found.");
        }

        device.Status = DeviceStatus.Online;
        device.LastSeenAt = DateTimeOffset.UtcNow;

        await WriteAuditLogAsync(
            device.OrganizationId,
            PilotDefaults.OperatorUserId,
            device.Id,
            null,
            "DEVICE_ENABLED",
            "Re-enabled device for remote access",
            sourceIp,
            cancellationToken);

        await _db.SaveChangesAsync(cancellationToken);

        var summary = await GetDeviceByIdAsync(deviceId, cancellationToken);
        return OperationResult<DeviceSummaryDto>.Success(summary!);
    }

    public async Task<OperationResult<bool>> GrantDevicePermissionAsync(
        Guid deviceId,
        GrantDevicePermissionRequest request,
        string sourceIp,
        CancellationToken cancellationToken)
    {
        var device = await _db.Devices.FirstOrDefaultAsync(d => d.Id == deviceId, cancellationToken);
        if (device is null)
        {
            return OperationResult<bool>.Failure("DEVICE_NOT_FOUND", "Device not found.");
        }

        var level = Enum.TryParse<RemotePermissionLevel>(request.PermissionLevel, true, out var parsed)
            ? parsed
            : RemotePermissionLevel.ViewAndControl;

        _db.DevicePermissions.Add(new DevicePermission
        {
            DeviceId = deviceId,
            UserId = request.UserId,
            Permission = level,
            ApprovedBy = request.ApprovedByUserId,
            ApprovedAt = DateTimeOffset.UtcNow,
            ExpiresAt = request.ExpiresAt
        });

        await WriteAuditLogAsync(
            device.OrganizationId,
            request.ApprovedByUserId,
            deviceId,
            null,
            "DEVICE_PERMISSION_GRANTED",
            $"Granted {level} to User {request.UserId}",
            sourceIp,
            cancellationToken);

        await _db.SaveChangesAsync(cancellationToken);
        return OperationResult<bool>.Success(true);
    }

    public async Task<DashboardOverviewDto> GetOverviewAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var devices = await _db.Devices.AsNoTracking().ToListAsync(cancellationToken);

        int online = 0, offline = 0, revoked = 0;
        foreach (var d in devices)
        {
            var (status, isOnline) = ComputeDeviceOnlineState(d, now);
            if (isOnline)
            {
                online++;
            }
            else if (status is "Revoked" or "Disabled")
            {
                revoked++;
            }
            else
            {
                offline++;
            }
        }

        await ExpireStaleSessionsAsync(now, cancellationToken);

        var activeSessions = await _db.Sessions.CountAsync(
            s => s.Status == SessionStatus.Reserved || s.Status == SessionStatus.Active,
            cancellationToken);

        var orgs = await _db.Organizations
            .AsNoTracking()
            .Select(o => new OrganizationDto(o.Id, o.Name, o.Status.ToString()))
            .ToListAsync(cancellationToken);

        var sites = await _db.Sites
            .AsNoTracking()
            .Select(s => new SiteDto(s.Id, s.OrganizationId, s.Name, s.Status.ToString()))
            .ToListAsync(cancellationToken);

        return new DashboardOverviewDto(
            devices.Count,
            online,
            offline,
            revoked,
            activeSessions,
            RemoteSession.MaxConcurrentSessions,
            orgs,
            sites);
    }

    public async Task<OperationResult<StartSessionResponse>> StartSessionAsync(
        StartSessionRequest request,
        string sourceIp,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(request.IdempotencyKey) &&
            IdempotentSessionCache.TryGetValue(request.IdempotencyKey, out var cachedResponse))
        {
            return OperationResult<StartSessionResponse>.Success(cachedResponse);
        }

        await SessionSlotLock.WaitAsync(cancellationToken);
        try
        {
            if (!string.IsNullOrWhiteSpace(request.IdempotencyKey) &&
                IdempotentSessionCache.TryGetValue(request.IdempotencyKey, out cachedResponse))
            {
                return OperationResult<StartSessionResponse>.Success(cachedResponse);
            }

            var now = DateTimeOffset.UtcNow;
            await ExpireStaleSessionsAsync(now, cancellationToken);

            var device = await _db.Devices.FirstOrDefaultAsync(
                d => d.Id == request.DeviceId && d.OrganizationId == request.OrganizationId,
                cancellationToken);

            if (device is null)
            {
                return OperationResult<StartSessionResponse>.Failure("DEVICE_NOT_FOUND", "Target device not found in organization.");
            }

            // RBAC & Device Permission Authorization Check
            var hasPermission = await CheckOperatorDeviceAuthorizationAsync(
                request.OrganizationId,
                request.OperatorUserId,
                device.Id,
                request.AllowInputControl,
                now,
                cancellationToken);

            if (!hasPermission)
            {
                await WriteAuditLogAsync(
                    request.OrganizationId,
                    request.OperatorUserId,
                    device.Id,
                    null,
                    "SESSION_ACCESS_DENIED",
                    "Operator lacks permission for target device",
                    sourceIp,
                    cancellationToken);
                await _db.SaveChangesAsync(cancellationToken);

                return OperationResult<StartSessionResponse>.Failure(
                    "FORBIDDEN_DEVICE_ACCESS",
                    "Operator does not have permission to access this device.");
            }

            var (computedStatus, isOnline) = ComputeDeviceOnlineState(device, now);
            if (!isOnline)
            {
                await WriteAuditLogAsync(
                    request.OrganizationId,
                    request.OperatorUserId,
                    device.Id,
                    null,
                    "SESSION_REQUEST_DENIED",
                    $"Device is {computedStatus}",
                    sourceIp,
                    cancellationToken);
                await _db.SaveChangesAsync(cancellationToken);

                return OperationResult<StartSessionResponse>.Failure(
                    "DEVICE_NOT_ONLINE",
                    $"Cannot start session because device is {computedStatus}.");
            }

            var activeSessions = await _db.Sessions
                .Where(s => s.Status == SessionStatus.Reserved || s.Status == SessionStatus.Active)
                .ToListAsync(cancellationToken);

            if (activeSessions.Count >= RemoteSession.MaxConcurrentSessions)
            {
                await WriteAuditLogAsync(
                    request.OrganizationId,
                    request.OperatorUserId,
                    device.Id,
                    null,
                    "SESSION_LIMIT_EXCEEDED",
                    $"Max concurrent sessions ({RemoteSession.MaxConcurrentSessions}) reached",
                    sourceIp,
                    cancellationToken);
                await _db.SaveChangesAsync(cancellationToken);

                return OperationResult<StartSessionResponse>.Failure(
                    "MAX_SESSIONS_REACHED",
                    $"Maximum concurrent session limit ({RemoteSession.MaxConcurrentSessions}) has been reached.");
            }

            var usedSlots = activeSessions.Select(s => s.SlotIndex).ToHashSet();
            int reservedSlot = 0;
            for (int i = 0; i < RemoteSession.MaxConcurrentSessions; i++)
            {
                if (!usedSlots.Contains(i))
                {
                    reservedSlot = i;
                    break;
                }
            }

            var sessionId = Guid.NewGuid();
            var ticketExpiresAt = now.Add(SessionTicketLifetime);
            var ticket = _ticketService.IssueTicket(
                sessionId,
                device.Id,
                request.OperatorUserId,
                request.AllowInputControl,
                ticketExpiresAt);

            var leaseSeconds = request.LeaseDurationSeconds.HasValue && request.LeaseDurationSeconds.Value > 0
                ? request.LeaseDurationSeconds.Value
                : 300;

            var session = new RemoteSession
            {
                Id = sessionId,
                OrganizationId = request.OrganizationId,
                DeviceId = device.Id,
                OperatorUserId = request.OperatorUserId,
                TransportType = TransportType.DirectLan,
                SlotIndex = reservedSlot,
                Status = SessionStatus.Active,
                StartedAt = now,
                LeaseExpiresAt = now.AddSeconds(leaseSeconds)
            };

            _db.Sessions.Add(session);

            var endpoint = await _db.DeviceEndpoints
                .AsNoTracking()
                .Where(e => e.DeviceId == device.Id && e.EndpointType == EndpointType.DirectLan)
                .FirstOrDefaultAsync(cancellationToken);

            var directAddress = endpoint?.Address ?? "127.0.0.1";
            var directPort = endpoint?.Port ?? DefaultDirectLanPort;

            PendingAgentSessions[device.Id] = new SessionStartInstructionDto(
                sessionId,
                request.OperatorUserId,
                request.OperatorDisplayName,
                ticket,
                ticketExpiresAt,
                directPort,
                request.AllowInputControl);

            await WriteAuditLogAsync(
                request.OrganizationId,
                request.OperatorUserId,
                device.Id,
                sessionId,
                "SESSION_STARTED",
                $"Reserved slot {reservedSlot} via DirectLan",
                sourceIp,
                cancellationToken);

            await _db.SaveChangesAsync(cancellationToken);

            var response = new StartSessionResponse(
                new SessionConnectionInfo(
                    sessionId,
                    device.Id,
                    request.OperatorUserId,
                    ticket,
                    ticketExpiresAt,
                    directAddress,
                    directPort,
                    "127.0.0.1",
                    DefaultRelayPort,
                    request.AllowInputControl),
                reservedSlot);

            if (!string.IsNullOrWhiteSpace(request.IdempotencyKey))
            {
                IdempotentSessionCache[request.IdempotencyKey] = response;
            }

            return OperationResult<StartSessionResponse>.Success(response);
        }
        finally
        {
            SessionSlotLock.Release();
        }
    }

    public async Task<OperationResult<ActiveSessionDto>> RenewSessionLeaseAsync(
        Guid sessionId,
        SessionLeaseRenewalRequest request,
        CancellationToken cancellationToken)
    {
        var session = await _db.Sessions.FirstOrDefaultAsync(
            s => s.Id == sessionId && (s.Status == SessionStatus.Reserved || s.Status == SessionStatus.Active),
            cancellationToken);

        if (session is null)
        {
            return OperationResult<ActiveSessionDto>.Failure("SESSION_NOT_FOUND", "Active session not found.");
        }

        var now = DateTimeOffset.UtcNow;
        if (session.LeaseExpiresAt < now)
        {
            session.Status = SessionStatus.TimedOut;
            session.EndedAt = now;
            session.EndReason = "Lease expired";
            await _db.SaveChangesAsync(cancellationToken);
            return OperationResult<ActiveSessionDto>.Failure("SESSION_EXPIRED", "Session lease has already expired.");
        }

        var extendSec = Math.Clamp(request.ExtendSeconds, 5, 600);
        session.LeaseExpiresAt = now.AddSeconds(extendSec);
        await _db.SaveChangesAsync(cancellationToken);

        var device = await _db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.Id == session.DeviceId, cancellationToken);
        return OperationResult<ActiveSessionDto>.Success(new ActiveSessionDto(
            session.Id,
            session.DeviceId,
            device?.Hostname ?? "Unknown",
            session.OperatorUserId,
            session.SlotIndex,
            session.TransportType.ToString(),
            session.Status.ToString(),
            session.StartedAt,
            session.LeaseExpiresAt));
    }

    public async Task<IReadOnlyList<ActiveSessionDto>> GetActiveSessionsAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        await ExpireStaleSessionsAsync(now, cancellationToken);

        var active = await _db.Sessions
            .AsNoTracking()
            .Where(s => s.Status == SessionStatus.Reserved || s.Status == SessionStatus.Active)
            .OrderBy(s => s.SlotIndex)
            .ToListAsync(cancellationToken);

        var devices = await _db.Devices.AsNoTracking().ToDictionaryAsync(d => d.Id, d => d.Hostname, cancellationToken);

        return active.Select(s => new ActiveSessionDto(
            s.Id,
            s.DeviceId,
            devices.GetValueOrDefault(s.DeviceId, "Unknown"),
            s.OperatorUserId,
            s.SlotIndex,
            s.TransportType.ToString(),
            s.Status.ToString(),
            s.StartedAt,
            s.LeaseExpiresAt)).ToList();
    }

    public async Task<IReadOnlyList<AuditLogDto>> GetAuditLogsAsync(
        int limit,
        CancellationToken cancellationToken)
    {
        var clampedLimit = Math.Clamp(limit, 1, 500);
        var logs = await _db.AuditLogs
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return logs
            .OrderByDescending(l => l.CreatedAt)
            .Take(clampedLimit)
            .Select(l => new AuditLogDto(
                l.Id,
                l.OrganizationId,
                l.ActorUserId,
                l.DeviceId,
                l.SessionId,
                l.Action,
                l.Result,
                l.SourceIp,
                l.CreatedAt))
            .ToList();
    }

    public async Task<OperationResult<bool>> EndSessionAsync(
        Guid sessionId,
        EndSessionRequest request,
        string sourceIp,
        CancellationToken cancellationToken)
    {
        var session = await _db.Sessions.FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken);
        if (session is null)
        {
            return OperationResult<bool>.Failure("SESSION_NOT_FOUND", "Session not found.");
        }

        session.Status = SessionStatus.Ended;
        session.EndedAt = DateTimeOffset.UtcNow;
        session.EndReason = request.Reason;

        await WriteAuditLogAsync(
            session.OrganizationId,
            request.ActorUserId ?? session.OperatorUserId,
            session.DeviceId,
            session.Id,
            "SESSION_ENDED",
            request.Reason,
            sourceIp,
            cancellationToken);

        await _db.SaveChangesAsync(cancellationToken);
        return OperationResult<bool>.Success(true);
    }

    private async Task<bool> CheckOperatorDeviceAuthorizationAsync(
        Guid organizationId,
        Guid operatorUserId,
        Guid deviceId,
        bool requireControlPermission,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var user = await _db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(
                u => u.Id == operatorUserId && u.OrganizationId == organizationId && u.Status == EntityStatus.Active,
                cancellationToken);

        if (user is null)
        {
            return false;
        }

        var roleIds = await _db.UserRoles
            .AsNoTracking()
            .Where(ur => ur.UserId == operatorUserId)
            .Select(ur => ur.RoleId)
            .ToListAsync(cancellationToken);

        var roleNames = await _db.Roles
            .AsNoTracking()
            .Where(r => roleIds.Contains(r.Id))
            .Select(r => r.Name)
            .ToListAsync(cancellationToken);

        // Organization OperatorAdmin has full access to devices within the organization
        if (roleNames.Any(r => string.Equals(r, "OperatorAdmin", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        // Standard operators require an explicit, non-expired DevicePermission
        var perms = await _db.DevicePermissions
            .AsNoTracking()
            .Where(p => p.DeviceId == deviceId &&
                        (p.UserId == operatorUserId || (p.RoleId.HasValue && roleIds.Contains(p.RoleId.Value))))
            .ToListAsync(cancellationToken);

        foreach (var p in perms)
        {
            if (p.ExpiresAt.HasValue && p.ExpiresAt.Value <= now)
            {
                continue;
            }

            if (requireControlPermission && p.Permission == RemotePermissionLevel.ViewAndControl)
            {
                return true;
            }

            if (!requireControlPermission && p.Permission is RemotePermissionLevel.ViewOnly or RemotePermissionLevel.ViewAndControl)
            {
                return true;
            }
        }

        return false;
    }

    private static (string Status, bool IsOnline) ComputeDeviceOnlineState(Device device, DateTimeOffset now)
    {
        if (device.Status == DeviceStatus.Revoked)
        {
            return ("Revoked", false);
        }

        if (device.Status == DeviceStatus.Disabled)
        {
            return ("Disabled", false);
        }

        var age = now - device.LastSeenAt;
        if (age <= HeartbeatOnlineThreshold)
        {
            return ("Online", true);
        }

        return ("Offline", false);
    }

    private async Task ReplaceEndpointsAsync(
        Guid deviceId,
        IReadOnlyList<DeviceEndpointDto> endpoints,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var existing = await _db.DeviceEndpoints
            .Where(e => e.DeviceId == deviceId)
            .ToListAsync(cancellationToken);
        _db.DeviceEndpoints.RemoveRange(existing);

        if (endpoints is null)
        {
            return;
        }

        foreach (var ep in endpoints)
        {
            var parsedType = Enum.TryParse<EndpointType>(ep.EndpointType, true, out var et)
                ? et
                : EndpointType.DirectLan;

            _db.DeviceEndpoints.Add(new DeviceEndpoint
            {
                DeviceId = deviceId,
                EndpointType = parsedType,
                Address = ep.Address,
                Port = ep.Port,
                LastVerifiedAt = now
            });
        }
    }

    private async Task ExpireStaleSessionsAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var activeOrReserved = await _db.Sessions
            .Where(s => s.Status == SessionStatus.Reserved || s.Status == SessionStatus.Active)
            .ToListAsync(cancellationToken);

        var stale = activeOrReserved.Where(s => s.LeaseExpiresAt < now).ToList();
        if (stale.Count > 0)
        {
            foreach (var s in stale)
            {
                s.Status = SessionStatus.TimedOut;
                s.EndedAt = now;
                s.EndReason = "Lease expired";
            }

            await _db.SaveChangesAsync(cancellationToken);
        }
    }

    private Task WriteAuditLogAsync(
        Guid organizationId,
        Guid? actorUserId,
        Guid? deviceId,
        Guid? sessionId,
        string action,
        string result,
        string sourceIp,
        CancellationToken cancellationToken)
    {
        _db.AuditLogs.Add(new AuditLog
        {
            OrganizationId = organizationId,
            ActorUserId = actorUserId,
            DeviceId = deviceId,
            SessionId = sessionId,
            Action = action,
            Result = result,
            SourceIp = sourceIp,
            CreatedAt = DateTimeOffset.UtcNow
        });

        return Task.CompletedTask;
    }
}
