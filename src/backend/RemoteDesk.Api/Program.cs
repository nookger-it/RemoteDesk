using RemoteDesk.Application.Abstractions;
using RemoteDesk.Contracts.Sessions;
using RemoteDesk.Infrastructure;
using RemoteDesk.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRemoteDeskInfrastructure();
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

var app = builder.Build();
app.UseCors();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<RemoteDeskDbContext>();
    await db.EnsureSeededAsync();
}

app.MapGet("/health", () => Results.Ok(new { status = "Healthy", timestamp = DateTimeOffset.UtcNow }));

app.MapPost("/api/auth/login", async (
    OperatorLoginRequest request,
    HttpContext http,
    IRemoteDeskManagementService service,
    CancellationToken ct) =>
{
    var sourceIp = http.Connection.RemoteIpAddress?.ToString() ?? "127.0.0.1";
    var result = await service.AuthenticateOperatorAsync(request, sourceIp, ct);
    if (!result.IsSuccess)
    {
        return Results.Json(
            new { error = result.ErrorCode, message = result.ErrorMessage },
            statusCode: StatusCodes.Status401Unauthorized);
    }

    return Results.Ok(result.Value);
});

app.MapGet("/api/overview", async (
    IRemoteDeskManagementService service,
    CancellationToken ct) =>
{
    var overview = await service.GetOverviewAsync(ct);
    return Results.Ok(overview);
});

app.MapGet("/api/devices", async (
    Guid? organizationId,
    Guid? siteId,
    string? status,
    string? search,
    IRemoteDeskManagementService service,
    CancellationToken ct) =>
{
    var devices = await service.GetDevicesAsync(organizationId, siteId, status, search, ct);
    return Results.Ok(devices);
});

app.MapGet("/api/devices/{id:guid}", async (
    Guid id,
    IRemoteDeskManagementService service,
    CancellationToken ct) =>
{
    var device = await service.GetDeviceByIdAsync(id, ct);
    return device is not null ? Results.Ok(device) : Results.NotFound();
});

app.MapPost("/api/devices/enroll", async (
    DeviceEnrollmentRequest request,
    HttpContext http,
    IRemoteDeskManagementService service,
    CancellationToken ct) =>
{
    var sourceIp = http.Connection.RemoteIpAddress?.ToString() ?? "127.0.0.1";
    var result = await service.EnrollDeviceAsync(request, sourceIp, ct);
    if (!result.IsSuccess)
    {
        return result.ErrorCode == "DEVICE_REVOKED"
            ? Results.Json(new { error = result.ErrorCode, message = result.ErrorMessage }, statusCode: StatusCodes.Status403Forbidden)
            : Results.BadRequest(new { error = result.ErrorCode, message = result.ErrorMessage });
    }

    return Results.Ok(result.Value);
});

app.MapPost("/api/devices/{id:guid}/heartbeat", async (
    Guid id,
    DeviceHeartbeatRequest request,
    HttpContext http,
    IRemoteDeskManagementService service,
    CancellationToken ct) =>
{
    var sourceIp = http.Connection.RemoteIpAddress?.ToString() ?? "127.0.0.1";
    var result = await service.ProcessHeartbeatAsync(id, request, sourceIp, ct);
    if (!result.IsSuccess)
    {
        if (result.ErrorCode == "DEVICE_REVOKED")
        {
            return Results.Json(new { error = result.ErrorCode, message = result.ErrorMessage }, statusCode: StatusCodes.Status403Forbidden);
        }

        return Results.NotFound(new { error = result.ErrorCode, message = result.ErrorMessage });
    }

    return Results.Ok(result.Value);
});

app.MapPost("/api/devices/{id:guid}/revoke", async (
    Guid id,
    RevokeDeviceRequest request,
    HttpContext http,
    IRemoteDeskManagementService service,
    CancellationToken ct) =>
{
    var sourceIp = http.Connection.RemoteIpAddress?.ToString() ?? "127.0.0.1";
    var result = await service.RevokeOrDisableDeviceAsync(id, permanentRevoke: true, request, sourceIp, ct);
    return result.IsSuccess
        ? Results.Ok(result.Value)
        : Results.NotFound(new { error = result.ErrorCode, message = result.ErrorMessage });
});

app.MapPost("/api/devices/{id:guid}/disable", async (
    Guid id,
    RevokeDeviceRequest request,
    HttpContext http,
    IRemoteDeskManagementService service,
    CancellationToken ct) =>
{
    var sourceIp = http.Connection.RemoteIpAddress?.ToString() ?? "127.0.0.1";
    var result = await service.RevokeOrDisableDeviceAsync(id, permanentRevoke: false, request, sourceIp, ct);
    return result.IsSuccess
        ? Results.Ok(result.Value)
        : Results.NotFound(new { error = result.ErrorCode, message = result.ErrorMessage });
});

app.MapPost("/api/devices/{id:guid}/enable", async (
    Guid id,
    HttpContext http,
    IRemoteDeskManagementService service,
    CancellationToken ct) =>
{
    var sourceIp = http.Connection.RemoteIpAddress?.ToString() ?? "127.0.0.1";
    var result = await service.EnableDeviceAsync(id, sourceIp, ct);
    return result.IsSuccess
        ? Results.Ok(result.Value)
        : Results.NotFound(new { error = result.ErrorCode, message = result.ErrorMessage });
});

app.MapPost("/api/devices/manual", async (
    AddManualDeviceRequest request,
    HttpContext http,
    IRemoteDeskManagementService service,
    CancellationToken ct) =>
{
    var sourceIp = http.Connection.RemoteIpAddress?.ToString() ?? "127.0.0.1";
    var result = await service.AddManualDeviceAsync(request, sourceIp, ct);
    return result.IsSuccess
        ? Results.Ok(result.Value)
        : Results.BadRequest(new { error = result.ErrorCode, message = result.ErrorMessage });
});

app.MapPut("/api/devices/{id:guid}/rename", async (
    Guid id,
    RenameDeviceRequest request,
    HttpContext http,
    IRemoteDeskManagementService service,
    CancellationToken ct) =>
{
    var sourceIp = http.Connection.RemoteIpAddress?.ToString() ?? "127.0.0.1";
    var result = await service.RenameDeviceAsync(id, request, sourceIp, ct);
    return result.IsSuccess
        ? Results.Ok(result.Value)
        : Results.BadRequest(new { error = result.ErrorCode, message = result.ErrorMessage });
});

app.MapDelete("/api/devices/{id:guid}", async (
    Guid id,
    HttpContext http,
    IRemoteDeskManagementService service,
    CancellationToken ct) =>
{
    var sourceIp = http.Connection.RemoteIpAddress?.ToString() ?? "127.0.0.1";
    var result = await service.DeleteDeviceAsync(id, sourceIp, ct);
    return result.IsSuccess
        ? Results.Ok(new { deleted = true })
        : Results.NotFound(new { error = result.ErrorCode, message = result.ErrorMessage });
});

app.MapPost("/api/devices/{id:guid}/permissions", async (
    Guid id,
    GrantDevicePermissionRequest request,
    HttpContext http,
    IRemoteDeskManagementService service,
    CancellationToken ct) =>
{
    var sourceIp = http.Connection.RemoteIpAddress?.ToString() ?? "127.0.0.1";
    var result = await service.GrantDevicePermissionAsync(id, request, sourceIp, ct);
    return result.IsSuccess
        ? Results.Ok(new { granted = true })
        : Results.NotFound(new { error = result.ErrorCode, message = result.ErrorMessage });
});

app.MapGet("/api/sessions/active", async (
    IRemoteDeskManagementService service,
    CancellationToken ct) =>
{
    var active = await service.GetActiveSessionsAsync(ct);
    return Results.Ok(active);
});

app.MapPost("/api/sessions/request", async (
    StartSessionRequest request,
    HttpContext http,
    IRemoteDeskManagementService service,
    CancellationToken ct) =>
{
    var sourceIp = http.Connection.RemoteIpAddress?.ToString() ?? "127.0.0.1";
    var result = await service.StartSessionAsync(request, sourceIp, ct);
    if (!result.IsSuccess)
    {
        var statusCode = result.ErrorCode switch
        {
            "MAX_SESSIONS_REACHED" => StatusCodes.Status429TooManyRequests,
            "FORBIDDEN_DEVICE_ACCESS" => StatusCodes.Status403Forbidden,
            "DEVICE_NOT_ONLINE" => StatusCodes.Status409Conflict,
            "DEVICE_NOT_FOUND" => StatusCodes.Status404NotFound,
            _ => StatusCodes.Status400BadRequest
        };

        return Results.Json(new { error = result.ErrorCode, message = result.ErrorMessage }, statusCode: statusCode);
    }

    return Results.Ok(result.Value);
});

app.MapPost("/api/sessions/{id:guid}/lease", async (
    Guid id,
    SessionLeaseRenewalRequest request,
    IRemoteDeskManagementService service,
    CancellationToken ct) =>
{
    var result = await service.RenewSessionLeaseAsync(id, request, ct);
    return result.IsSuccess
        ? Results.Ok(result.Value)
        : Results.NotFound(new { error = result.ErrorCode, message = result.ErrorMessage });
});

app.MapPost("/api/sessions/{id:guid}/end", async (
    Guid id,
    EndSessionRequest request,
    HttpContext http,
    IRemoteDeskManagementService service,
    CancellationToken ct) =>
{
    var sourceIp = http.Connection.RemoteIpAddress?.ToString() ?? "127.0.0.1";
    var result = await service.EndSessionAsync(id, request, sourceIp, ct);
    return result.IsSuccess
        ? Results.Ok(new { ended = true })
        : Results.NotFound(new { error = result.ErrorCode, message = result.ErrorMessage });
});

app.MapGet("/api/audit-logs", async (
    int? limit,
    IRemoteDeskManagementService service,
    CancellationToken ct) =>
{
    var logs = await service.GetAuditLogsAsync(limit ?? 100, ct);
    return Results.Ok(logs);
});

app.Run();

public partial class Program;
