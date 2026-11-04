namespace RemoteDesk.Transport.Abstractions;

/// <summary>
/// Runtime stream quality presets that the Operator can switch during an active session.
/// </summary>
public enum StreamQualityProfile
{
    /// <summary>Native screen resolution, 95% JPEG quality, 45 FPS (ultra-sharp text &amp; high speed).</summary>
    Ultra = 0,

    /// <summary>Native screen resolution, 88% JPEG quality, 35 FPS (sharpest text, low bandwidth).</summary>
    Sharp = 1,

    /// <summary>1600x900, 82% JPEG quality, 45 FPS.</summary>
    Balanced = 2,

    /// <summary>1280x720, 75% JPEG quality, 60 FPS (maximum frame rate &amp; lowest latency).</summary>
    Fast = 3
}

/// <summary>
/// Concrete capture/encode settings for a <see cref="StreamQualityProfile"/>.
/// Width/Height of 0 means "use the native screen resolution".
/// </summary>
public sealed record StreamQualitySettings(int Width, int Height, long JpegQuality, int TargetFps)
{
    public static StreamQualitySettings For(StreamQualityProfile profile) => profile switch
    {
        StreamQualityProfile.Ultra => new StreamQualitySettings(0, 0, 95L, 45),
        StreamQualityProfile.Sharp => new StreamQualitySettings(0, 0, 88L, 35),
        StreamQualityProfile.Fast => new StreamQualitySettings(1280, 720, 75L, 60),
        _ => new StreamQualitySettings(1600, 900, 82L, 45)
    };

    public static StreamQualityProfile Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "ultra" or "max" => StreamQualityProfile.Ultra,
        "sharp" or "high" or "hq" => StreamQualityProfile.Sharp,
        "fast" or "low" => StreamQualityProfile.Fast,
        _ => StreamQualityProfile.Ultra
    };
}

/// <summary>
/// Optional capability implemented by transports that can ask the Agent to change
/// stream resolution / JPEG quality / FPS during an active session.
/// </summary>
public interface IStreamQualityControl
{
    Task SetQualityProfileAsync(StreamQualityProfile profile, CancellationToken cancellationToken);
}

/// <summary>
/// Optional capability implemented by transports that support encrypted chunked file transfer
/// from the Operator Client to the Agent PC, and bidirectional Unicode clipboard synchronization.
/// </summary>
public interface IFileTransferTransport
{
    event Action<RemoteDesk.Contracts.Sessions.FileTransferAckPacket>? FileTransferAckReceived;

    event Action<RemoteDesk.Contracts.Sessions.ClipboardSyncPacket>? ClipboardSyncReceived;

    Task SendFileChunkAsync(
        RemoteDesk.Contracts.Sessions.FileTransferPacket chunk,
        CancellationToken cancellationToken);

    Task SendClipboardSyncAsync(
        RemoteDesk.Contracts.Sessions.ClipboardSyncPacket clipboard,
        CancellationToken cancellationToken);
}

