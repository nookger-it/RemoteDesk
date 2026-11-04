using RemoteDesk.Agent.Contracts;
using RemoteDesk.Agent.Interactive;
using RemoteDesk.Application.Security;
using RemoteDesk.Transport.Abstractions;
using RemoteDesk.Transport.DirectLan;
using RemoteDesk.Transport.LocalRelay;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

// Quality profile: ultra (native 95%, 45 FPS, default) | sharp | balanced | fast (60 FPS). Operator can switch it live during a session.
var initialProfile = StreamQualitySettings.Parse(Environment.GetEnvironmentVariable("REMOTEDESK_QUALITY_PROFILE") ?? "ultra");
var initialSettings = StreamQualitySettings.For(initialProfile);

// Optional explicit overrides (0 = native resolution)
var captureWidth = int.TryParse(Environment.GetEnvironmentVariable("REMOTEDESK_CAPTURE_WIDTH"), out var cw) ? cw : initialSettings.Width;
var captureHeight = int.TryParse(Environment.GetEnvironmentVariable("REMOTEDESK_CAPTURE_HEIGHT"), out var ch) ? ch : initialSettings.Height;
var targetFps = int.TryParse(Environment.GetEnvironmentVariable("REMOTEDESK_TARGET_FPS"), out var fps) ? Math.Clamp(fps, 5, 60) : initialSettings.TargetFps;
var jpegQuality = long.TryParse(Environment.GetEnvironmentVariable("REMOTEDESK_JPEG_QUALITY"), out var jq) ? jq : initialSettings.JpegQuality;
var directPort = int.TryParse(Environment.GetEnvironmentVariable("REMOTEDESK_DIRECT_PORT"), out var dp) ? dp : 52000;
var relayHost = Environment.GetEnvironmentVariable("REMOTEDESK_RELAY_HOST") ?? "127.0.0.1";
var relayPort = int.TryParse(Environment.GetEnvironmentVariable("REMOTEDESK_RELAY_PORT"), out var rp) ? rp : 52100;
var captureAllMonitors = string.Equals(Environment.GetEnvironmentVariable("REMOTEDESK_ALL_MONITORS"), "true", StringComparison.OrdinalIgnoreCase);

using var captureEngine = new ScreenCaptureEngine(
    captureWidth,
    captureHeight,
    captureAllMonitors: captureAllMonitors,
    jpegQuality: jpegQuality,
    skipUnchangedFrames: true);
var inputEngine = new InputInjectorEngine(executeNativeSendInput: true);
var bannerManager = new AgentSessionBannerManager();
var ticketService = new SessionTicketService();
LocalRelayAgentSessionBridge? activeRelayBridge = null;
DirectLanStreamHost? directHostRef = null;
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
            captureEngine.RequestKeyframe();
        }
        return valid;
    },
    frameProvider: (seq, token) => captureEngine.CaptureFrameAsync(currentStreamingSessionId, seq, token),
    targetFps: targetFps,
    inputHandler: (inputPacket, allowInput, token) => inputEngine.ProcessInputAsync(inputPacket, allowInput, token),
    qualityHandler: profile =>
    {
        var s = StreamQualitySettings.For(profile);
        captureEngine.ApplySettings(s.Width, s.Height, s.JpegQuality);
        if (directHostRef is not null)
        {
            directHostRef.TargetFps = s.TargetFps;
        }

        var res = s.Width == 0 ? "native" : $"{s.Width}x{s.Height}";
        Console.WriteLine($"[Agent.Interactive] Quality -> {profile}: {res}, JPEG {s.JpegQuality}%, {s.TargetFps} FPS");
    },
    clipboardHandler: (clipSync, allowInput) =>
    {
        if (!allowInput || !OperatingSystem.IsWindows())
        {
            return;
        }

        if (Win32ClipboardHelper.TrySetText(clipSync.Text) && clipSync.TriggerPaste)
        {
            inputEngine.InjectCtrlV();
        }
    },
    remoteClipboardProvider: () => OperatingSystem.IsWindows() ? Win32ClipboardHelper.TryGetText() : null,
    fileReceivedHandler: savedPath =>
    {
        if (OperatingSystem.IsWindows())
        {
            _ = Win32ClipboardHelper.TrySetFileDropList(savedPath);
        }
    });
directHostRef = directHost;

directHost.SessionEndedByPeerOrLocal += (_, reason) =>
{
    bannerManager.HideBanner(reason);
};

directHost.Start();
var resLabel = captureWidth == 0 ? "native" : $"{captureWidth}x{captureHeight}";
Console.WriteLine($"[Agent.Interactive] Started in User Session ({Environment.UserName}@{Environment.MachineName}). Profile={initialProfile}, Resolution={resLabel}, JPEG={jpegQuality}%, {targetFps}fps. Listening on Direct LAN TLS port {directHost.BoundPort}...");

// Relay frames are only produced when no Operator is attached via Direct LAN
// (avoids capturing/encoding every frame twice, which halved the FPS before).
async Task<RemoteDesk.Contracts.Sessions.ScreenFramePacket> RelayFrameProvider(Guid sessionId, long seq, CancellationToken token)
{
    if (directHost.HasActiveSession)
    {
        await Task.Delay(250, token);
        return new RemoteDesk.Contracts.Sessions.ScreenFramePacket(sessionId, seq, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), 0, 0, "jpeg", []);
    }

    return await captureEngine.CaptureFrameAsync(sessionId, seq, token);
}

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
            frameProvider: (seq, token) => RelayFrameProvider(startCmd.SessionId, seq, token),
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
