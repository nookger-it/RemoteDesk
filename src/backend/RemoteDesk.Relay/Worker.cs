using RemoteDesk.Application.Security;
using RemoteDesk.Transport.LocalRelay;

namespace RemoteDesk.Relay;

public sealed class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly IConfiguration _config;

    public Worker(ILogger<Worker> logger, IConfiguration config)
    {
        _logger = logger;
        _config = config;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var port = _config.GetValue("RemoteDesk:RelayPort", 52100);
        var secret = _config["RemoteDesk:TicketSigningKey"];
        var ticketService = new SessionTicketService(secret);

        await using var relayServer = new LocalRelayServer(
            port: port,
            ticketValidator: (Guid sessionId, Guid deviceId, string ticket, out bool allowInput, out string? error) =>
            {
                var valid = ticketService.TryValidateTicket(ticket, sessionId, deviceId, out var claims, out error);
                allowInput = claims?.AllowInputControl ?? false;
                return valid;
            });

        relayServer.Start();
        _logger.LogInformation("RemoteDesk Local Relay Service started on TCP/TLS port {Port}", relayServer.BoundPort);

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("RemoteDesk Local Relay Service stopping...");
        }
    }
}
