using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using RemoteDesk.Agent.Contracts;
using RemoteDesk.Contracts.Sessions;

namespace RemoteDesk.Agent.Service;

public sealed record PersistedDeviceIdentity(
    Guid DeviceId,
    Guid OrganizationId,
    Guid SiteId,
    string PublicKey);

public sealed class Worker : BackgroundService
{
    private static readonly Guid DefaultPilotOrgId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid DefaultPilotSiteId = Guid.Parse("22222222-2222-2222-2222-222222222221");

    private readonly ILogger<Worker> _logger;
    private readonly IConfiguration _config;
    private readonly HttpClient _httpClient;
    private readonly string _identityFilePath;

    public Worker(ILogger<Worker> logger, IConfiguration config)
    {
        _logger = logger;
        _config = config;

        var apiBaseUrl = Environment.GetEnvironmentVariable("REMOTEDESK_API_URL")
            ?? _config["RemoteDesk:ApiBaseUrl"]
            ?? "http://localhost:5100";
        _httpClient = new HttpClient { BaseAddress = new Uri(apiBaseUrl) };
        _identityFilePath = Path.Combine(AppContext.BaseDirectory, "device-identity.json");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var identity = await LoadOrCreateIdentityAsync(stoppingToken);
        var envPort = Environment.GetEnvironmentVariable("REMOTEDESK_DIRECT_PORT");
        var directPort = int.TryParse(envPort, out var parsedPort)
            ? parsedPort
            : _config.GetValue("RemoteDesk:DirectLanPort", 52000);
        var heartbeatSeconds = 10;

        _logger.LogInformation(
            "RemoteDesk Agent.Service starting in Session 0. Host={Host}, DeviceId={DeviceId}",
            Environment.MachineName,
            identity.DeviceId);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var endpoints = DiscoverLanEndpoints(directPort);
                var enrollReq = new DeviceEnrollmentRequest(
                    identity.OrganizationId,
                    identity.SiteId,
                    identity.DeviceId,
                    Environment.MachineName,
                    "1.0.0-v1",
                    identity.PublicKey,
                    endpoints);

                var enrollResp = await _httpClient.PostAsJsonAsync("/api/devices/enroll", enrollReq, stoppingToken);
                if (enrollResp.IsSuccessStatusCode)
                {
                    var enrolled = await enrollResp.Content.ReadFromJsonAsync<DeviceEnrollmentResponse>(cancellationToken: stoppingToken);
                    if (enrolled is not null)
                    {
                        identity = identity with { DeviceId = enrolled.DeviceId };
                        await SaveIdentityAsync(identity, stoppingToken);
                        heartbeatSeconds = Math.Max(5, enrolled.HeartbeatIntervalSeconds);
                        _logger.LogInformation("Device enrolled successfully with DeviceId={DeviceId}", identity.DeviceId);
                        break;
                    }
                }
                else
                {
                    _logger.LogWarning("Enrollment returned HTTP {StatusCode}. Retrying...", (int)enrollResp.StatusCode);
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Waiting for Local Management API to become reachable...");
            }

            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var endpoints = DiscoverLanEndpoints(directPort);
                var hbReq = new DeviceHeartbeatRequest(identity.DeviceId, "1.0.0-v1", endpoints);
                var hbResp = await _httpClient.PostAsJsonAsync(
                    $"/api/devices/{identity.DeviceId}/heartbeat",
                    hbReq,
                    stoppingToken);

                if (hbResp.StatusCode == HttpStatusCode.Forbidden)
                {
                    _logger.LogError("Device {DeviceId} has been REVOKED or DISABLED by administrator. Stopping heartbeat.", identity.DeviceId);
                    break;
                }

                if (hbResp.IsSuccessStatusCode)
                {
                    var hbData = await hbResp.Content.ReadFromJsonAsync<DeviceHeartbeatResponse>(cancellationToken: stoppingToken);
                    if (hbData?.PendingSession is { } pending)
                    {
                        _logger.LogInformation(
                            "Received session instruction SessionId={SessionId} for Operator={Operator}. Forwarding to Agent.Interactive via IPC...",
                            pending.SessionId,
                            pending.OperatorDisplayName);

                        try
                        {
                            await AgentNamedPipeIpc.SendAsync(
                                new AgentIpcEnvelope(
                                    "START_SESSION",
                                    new StartInteractiveSessionCommand(
                                        pending.SessionId,
                                        identity.DeviceId,
                                        pending.OperatorUserId,
                                        pending.OperatorDisplayName,
                                        pending.SessionTicket,
                                        pending.TicketExpiresAt,
                                        pending.DirectPort,
                                        pending.AllowInputControl)),
                                cancellationToken: stoppingToken);
                        }
                        catch (Exception ipcEx)
                        {
                            _logger.LogWarning(ipcEx, "Could not forward session start to Agent.Interactive via Named Pipe.");
                        }
                    }
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Heartbeat request failed.");
            }

            await Task.Delay(TimeSpan.FromSeconds(heartbeatSeconds), stoppingToken);
        }
    }

    private async Task<PersistedDeviceIdentity> LoadOrCreateIdentityAsync(CancellationToken ct)
    {
        if (File.Exists(_identityFilePath))
        {
            var json = await File.ReadAllTextAsync(_identityFilePath, ct);
            var loaded = JsonSerializer.Deserialize<PersistedDeviceIdentity>(json);
            if (loaded is not null && loaded.DeviceId != Guid.Empty)
            {
                return loaded;
            }
        }

        using var rsa = RSA.Create(2048);
        var pubKeyB64 = Convert.ToBase64String(rsa.ExportRSAPublicKey());
        var created = new PersistedDeviceIdentity(
            Guid.NewGuid(),
            DefaultPilotOrgId,
            DefaultPilotSiteId,
            pubKeyB64);

        await SaveIdentityAsync(created, ct);
        return created;
    }

    private async Task SaveIdentityAsync(PersistedDeviceIdentity identity, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(identity, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(_identityFilePath, json, ct);
    }

    private static IReadOnlyList<DeviceEndpointDto> DiscoverLanEndpoints(int port)
    {
        var list = new List<DeviceEndpointDto>();
        try
        {
            var host = Dns.GetHostEntry(Dns.GetHostName());
            foreach (var ip in host.AddressList)
            {
                if (ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
                {
                    list.Add(new DeviceEndpointDto("DirectLan", ip.ToString(), port));
                }
            }
        }
        catch
        {
            // Fallback to loopback if network interface enumeration fails
        }

        if (list.Count == 0)
        {
            list.Add(new DeviceEndpointDto("DirectLan", "127.0.0.1", port));
        }

        return list;
    }
}
