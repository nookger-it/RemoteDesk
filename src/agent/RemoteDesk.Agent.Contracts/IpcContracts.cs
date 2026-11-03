using System.IO.Pipes;
using System.Text.Json;

namespace RemoteDesk.Agent.Contracts;

/// <summary>
/// IPC message contracts exchanged strictly between Agent.Service (Session 0)
/// and Agent.Interactive (User Session Worker).
/// </summary>
public sealed record StartInteractiveSessionCommand(
    Guid SessionId,
    Guid DeviceId,
    Guid OperatorUserId,
    string OperatorDisplayName,
    string SessionTicket,
    DateTimeOffset TicketExpiresAt,
    int DirectPort,
    bool AllowInputControl,
    int TargetWidth = 1280,
    int TargetHeight = 720,
    int TargetFps = 12);

public sealed record StopInteractiveSessionCommand(
    Guid SessionId,
    string Reason);

public sealed record AgentIpcEnvelope(
    string CommandType,
    StartInteractiveSessionCommand? StartCommand = null,
    StopInteractiveSessionCommand? StopCommand = null);

public static class AgentNamedPipeIpc
{
    public const string DefaultPipeName = "RemoteDesk.Agent.Ipc.V1";

    public static async Task SendAsync(
        AgentIpcEnvelope envelope,
        string pipeName = DefaultPipeName,
        CancellationToken cancellationToken = default)
    {
        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.Asynchronous);
        await client.ConnectAsync(1500, cancellationToken);
        await JsonSerializer.SerializeAsync(client, envelope, cancellationToken: cancellationToken);
        await client.FlushAsync(cancellationToken);
    }

    public static async Task ListenAsync(
        Func<AgentIpcEnvelope, CancellationToken, Task> onMessageReceived,
        string pipeName = DefaultPipeName,
        CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    pipeName,
                    PipeDirection.In,
                    maxNumberOfServerInstances: 4,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(cancellationToken);
                var envelope = await JsonSerializer.DeserializeAsync<AgentIpcEnvelope>(server, cancellationToken: cancellationToken);
                if (envelope is not null)
                {
                    await onMessageReceived(envelope, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // Ignore transient pipe reset or malformed frame so IPC listener stays alive
            }
        }
    }
}
