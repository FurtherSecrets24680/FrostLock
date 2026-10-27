using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace FrostLock.Services;

/// <summary>
/// Sends command-line requests to the running FrostLock instance over a named pipe.
/// </summary>
public sealed class InstanceCommand
{
    public string Command { get; set; } = "show";
    public string? Mode { get; set; }
    public int? DurationSeconds { get; set; }
    public int? ArmSeconds { get; set; }
    public bool? Sound { get; set; }
}

public sealed class InstanceReply
{
    public bool Ok { get; set; }
    public string Message { get; set; } = string.Empty;

    public static InstanceReply Fail(string message) => new() { Ok = false, Message = message };

    public static InstanceReply Done(string message) => new() { Ok = true, Message = message };
}

/// <summary>One-request, one-reply channel between FrostLock processes.</summary>
public static class InstanceChannel
{
    public const string PipeName = "FrostLock.Ipc.v1";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>Sends a command to the running instance, or reports why it could not.</summary>
    public static InstanceReply Send(InstanceCommand command, TimeSpan timeout)
    {
        try
        {
            using var client = new NamedPipeClientStream(
                ".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

            client.Connect(Math.Max(200, (int)timeout.TotalMilliseconds));

            using var writer = new StreamWriter(client, new UTF8Encoding(false), 4096, leaveOpen: true)
            {
                AutoFlush = true
            };
            using var reader = new StreamReader(client, Encoding.UTF8, false, 4096, leaveOpen: true);

            writer.WriteLine(JsonSerializer.Serialize(command, Json));

            var line = reader.ReadLine();
            if (line is null)
                return InstanceReply.Fail("The running FrostLock did not answer.");

            return JsonSerializer.Deserialize<InstanceReply>(line, Json)
                   ?? InstanceReply.Fail("The running FrostLock sent an empty reply.");
        }
        catch (TimeoutException)
        {
            return InstanceReply.Fail("Timed out waiting for the running FrostLock.");
        }
        catch (Exception ex)
        {
            return InstanceReply.Fail($"Could not reach the running FrostLock: {ex.Message}");
        }
    }

    /// <summary>
    /// Listens in the background. The handler runs on the server thread and is expected to
    /// marshal to the UI thread itself.
    /// </summary>
    public static Task StartAsync(Func<InstanceCommand, InstanceReply> handler, CancellationToken token)
        => Task.Run(() => AcceptLoopAsync(handler, token), CancellationToken.None);

    private static async Task AcceptLoopAsync(Func<InstanceCommand, InstanceReply> handler,
        CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;

            try
            {
                server = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(token).ConfigureAwait(false);

                using var reader = new StreamReader(server, Encoding.UTF8, false, 4096, leaveOpen: true);
                using var writer = new StreamWriter(server, new UTF8Encoding(false), 4096, leaveOpen: true)
                {
                    AutoFlush = true
                };

                var line = await reader.ReadLineAsync(token).ConfigureAwait(false);

                InstanceCommand? command = null;
                if (line is not null)
                {
                    try { command = JsonSerializer.Deserialize<InstanceCommand>(line, Json); }
                    catch (JsonException) { /* reported below */ }
                }

                var reply = command is null
                    ? InstanceReply.Fail("The command could not be read.")
                    : handler(command);

                await writer.WriteLineAsync(JsonSerializer.Serialize(reply, Json)).ConfigureAwait(false);
                await writer.FlushAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Another instance may temporarily own the pipe during an elevated relaunch.
                try { await Task.Delay(250, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
            finally
            {
                try { server?.Dispose(); } catch { /* ignore */ }
            }
        }
    }
}
