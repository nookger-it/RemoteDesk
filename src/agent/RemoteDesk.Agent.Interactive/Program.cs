using RemoteDesk.Agent.Contracts;
using RemoteDesk.Agent.Interactive;
using RemoteDesk.Application.Security;
using RemoteDesk.Transport.DirectLan;
using RemoteDesk.Transport.LocalRelay;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

var captureWidth = int.TryParse(Environment.GetEnvironmentVariable("REMOTEDESK_CAPTURE_WIDTH"), out var cw) ? cw : 1280;
var captureHeight = int.TryParse(Environment.GetEnvironmentVariable("REMOTEDESK_CAPTURE_HEIGHT"), out var ch) ? ch : 720;
var targetFps = int.TryParse(Environment.GetEnvironmentVariable("REMOTEDESK_TARGET_FPS"), out var fps) ? Math.Clamp(fps, 5, 30) : 15;
var directPort = int.TryParse(Environment.GetEnvironmentVariable("REMOTEDESK_DIRECT_PORT"), out var dp) ? dp : 52000;
var relayHost = Environment.GetEnvironmentVariable("REMOTEDESK_RELAY_HOST") ?? "127.0.0.1";
var relayPort = int.TryParse(Environment.GetEnvironmentVariable("REMOTEDESK_RELAY_PORT"), out var rp) ? rp : 52100;
var captureAllMonitors = string.Equals(Environment.GetEnvironmentVariable("REMOTEDESK_ALL_MONITORS"), "true", StringComparison.OrdinalIgnoreCase);

using var captureEngine = new ScreenCaptureEngine(captureWidth, captureHeight, captureAllMonitors: captureAllMonitors, jpegQuality: 85L);
var inputEngine = new InputInjectorEngine(executeNativeSendInput: true);
var bannerManager = new AgentSessionBannerManager();
var ticketService = new SessionTicketService();
LocalRelayAgentSessionBridge? activeRelayBridge = null;
Guid currentStreamingSessionId = Guid.Empty;

bannerManager.StateChanged += state =>
{
    Console.WriteLine(state.IsVisible
        ? $"================================================================\n{state.BannerText}\n================================================================"
        : $"[Agent Banner] {state.BannerText}");
};

// Start Direct LAN Encrypted Stream Host immediately on port 52000 so incoming Operator connections
// with a valid HMAC-signed SessionTicket connect with zero heartbeat delay!
await using var directHost = new DirectLanStreamHost(
    port: directPort,
    deviceId: Guid.Empty,
    ticketValidator: (Guid sessionId, Guid deviceId, string ticket, out bool allowInput, out string? errorMessage) =>
    {
        var valid = ticketService.TryValidateTicket(ticket, sessionId, deviceId, out var claims, out errorMessage);
        allowInput = claims?.AllowInputControl ?? false;
        if (valid)
        {
            currentStreamingSessionId = sessionId;
        }
        return valid;
    },
    frameProvider: (seq, token) => captureEngine.CaptureFrameAsync(currentStreamingSessionId, seq, token),
    targetFps: targetFps,
    inputHandler: (inputPacket, allowInput, token) => inputEngine.ProcessInputAsync(inputPacket, allowInput, token));

directHost.SessionEndedByPeerOrLocal += (_, reason) =>
{
    bannerManager.HideBanner(reason);
};

directHost.Start();
Console.WriteLine($"[Agent.Interactive] Started in User Session ({Environment.UserName}@{Environment.MachineName}). Resolution={captureWidth}x{captureHeight}@{targetFps}fps. Listening on Direct LAN TLS port {directHost.BoundPort}...");

await AgentNamedPipeIpc.ListenAsync(async (envelope, ct) =>
{
    if (envelope.CommandType == "START_SESSION" && envelope.StartCommand is { } startCmd)
    {
        currentStreamingSessionId = startCmd.SessionId;

        if (activeRelayBridge is not null)
        {
            await activeRelayBridge.DisposeAsync();
        }

        activeRelayBridge = new LocalRelayAgentSessionBridge(
            relayAddress: relayHost,
            relayPort: relayPort,
            sessionId: startCmd.SessionId,
            deviceId: startCmd.DeviceId,
            sessionTicket: startCmd.SessionTicket,
            frameProvider: (seq, token) => captureEngine.CaptureFrameAsync(startCmd.SessionId, seq, token),
            targetFps: startCmd.TargetFps,
            inputHandler: (inputPacket, allowInput, token) => inputEngine.ProcessInputAsync(inputPacket, allowInput, token));
        activeRelayBridge.Start();

        bannerManager.ShowBannerForSession(startCmd, directHost);
    }
    else if (envelope.CommandType == "STOP_SESSION")
    {
        await bannerManager.DisconnectByLocalUserAsync(envelope.StopCommand?.Reason ?? "Stopped via IPC");
        if (activeRelayBridge is not null)
        {
            await activeRelayBridge.DisposeAsync();
            activeRelayBridge = null;
        }
    }
}, cancellationToken: cts.Token);
