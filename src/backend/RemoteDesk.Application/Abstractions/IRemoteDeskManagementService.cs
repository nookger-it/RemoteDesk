using RemoteDesk.Contracts.Sessions;

namespace RemoteDesk.Application.Abstractions;

public sealed record OperationResult<T>(
    bool IsSuccess,
    T? Value,
    string? ErrorCode = null,
    string? ErrorMessage = null)
{
    public static OperationResult<T> Success(T value) => new(true, value);
    public static OperationResult<T> Failure(string errorCode, string errorMessage) => new(false, default, errorCode, errorMessage);
}

public interface IRemoteDeskManagementService
{
    Task<OperationResult<OperatorLoginResponse>> AuthenticateOperatorAsync(
        OperatorLoginRequest request,
        string sourceIp,
        CancellationToken cancellationToken);

    Task<OperationResult<DeviceEnrollmentResponse>> EnrollDeviceAsync(
        DeviceEnrollmentRequest request,
        string sourceIp,
        CancellationToken cancellationToken);

    Task<OperationResult<DeviceHeartbeatResponse>> ProcessHeartbeatAsync(
        Guid deviceId,
        DeviceHeartbeatRequest request,
        string sourceIp,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<DeviceSummaryDto>> GetDevicesAsync(
        Guid? organizationId,
        Guid? siteId,
        string? statusFilter,
        string? searchQuery,
        CancellationToken cancellationToken);

    Task<DeviceSummaryDto?> GetDeviceByIdAsync(
        Guid deviceId,
        CancellationToken cancellationToken);

    Task<OperationResult<DeviceSummaryDto>> RevokeOrDisableDeviceAsync(
        Guid deviceId,
        bool permanentRevoke,
        RevokeDeviceRequest request,
        string sourceIp,
        CancellationToken cancellationToken);

    Task<OperationResult<DeviceSummaryDto>> EnableDeviceAsync(
        Guid deviceId,
        string sourceIp,
        CancellationToken cancellationToken);

    Task<OperationResult<bool>> GrantDevicePermissionAsync(
        Guid deviceId,
        GrantDevicePermissionRequest request,
        string sourceIp,
        CancellationToken cancellationToken);

    Task<DashboardOverviewDto> GetOverviewAsync(
        CancellationToken cancellationToken);

    Task<OperationResult<StartSessionResponse>> StartSessionAsync(
        StartSessionRequest request,
        string sourceIp,
        CancellationToken cancellationToken);

    Task<OperationResult<ActiveSessionDto>> RenewSessionLeaseAsync(
        Guid sessionId,
        SessionLeaseRenewalRequest request,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ActiveSessionDto>> GetActiveSessionsAsync(
        CancellationToken cancellationToken);

    Task<IReadOnlyList<AuditLogDto>> GetAuditLogsAsync(
        int limit,
        CancellationToken cancellationToken);

    Task<OperationResult<bool>> EndSessionAsync(
        Guid sessionId,
        EndSessionRequest request,
        string sourceIp,
        CancellationToken cancellationToken);
}
