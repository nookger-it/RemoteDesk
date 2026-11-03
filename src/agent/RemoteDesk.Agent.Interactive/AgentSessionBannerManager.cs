using RemoteDesk.Agent.Contracts;
using RemoteDesk.Transport.DirectLan;

namespace RemoteDesk.Agent.Interactive;

public sealed record ActiveSessionBannerState(
    bool IsVisible,
    Guid SessionId,
    string OperatorDisplayName,
    bool AllowInputControl,
    DateTimeOffset StartedAtUtc,
    string BannerText);

/// <summary>
/// Manages the mandatory Agent Remote Session Status Banner (No Hidden Mode allowed)
/// and provides the local user with an immediate Disconnect / Stop Remote Access action.
/// </summary>
public sealed class AgentSessionBannerManager
{
    private DirectLanStreamHost? _currentHost;

    public ActiveSessionBannerState CurrentState { get; private set; } = new(
        IsVisible: false,
        SessionId: Guid.Empty,
        OperatorDisplayName: string.Empty,
        AllowInputControl: false,
        StartedAtUtc: DateTimeOffset.MinValue,
        BannerText: "No active remote session");

    public event Action<ActiveSessionBannerState>? StateChanged;

    public void ShowBannerForSession(
        StartInteractiveSessionCommand command,
        DirectLanStreamHost host)
    {
        _currentHost = host;
        var modeLabel = command.AllowInputControl ? "View + Mouse/Keyboard Control" : "View Only";
        var text = $"[REMOTE SESSION ACTIVE] Controlled by: {command.OperatorDisplayName} ({modeLabel}) — Press Disconnect to terminate immediately.";

        CurrentState = new ActiveSessionBannerState(
            IsVisible: true,
            SessionId: command.SessionId,
            OperatorDisplayName: command.OperatorDisplayName,
            AllowInputControl: command.AllowInputControl,
            StartedAtUtc: DateTimeOffset.UtcNow,
            BannerText: text);

        StateChanged?.Invoke(CurrentState);
    }

    public async Task DisconnectByLocalUserAsync(string reason = "Local user pressed Disconnect on Agent PC")
    {
        var host = _currentHost;
        _currentHost = null;

        if (host is not null)
        {
            await host.DisconnectActiveSessionByLocalUserAsync(reason);
        }

        HideBanner(reason);
    }

    public void HideBanner(string reason = "Session ended")
    {
        _currentHost = null;
        CurrentState = new ActiveSessionBannerState(
            IsVisible: false,
            SessionId: Guid.Empty,
            OperatorDisplayName: string.Empty,
            AllowInputControl: false,
            StartedAtUtc: DateTimeOffset.MinValue,
            BannerText: $"Idle ({reason})");

        StateChanged?.Invoke(CurrentState);
    }
}

