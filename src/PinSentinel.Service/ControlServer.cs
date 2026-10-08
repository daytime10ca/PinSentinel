using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using PinSentinel.Core;

namespace PinSentinel.Service;

/// <summary>
/// Accepts one-line commands from the tray app: arm, disarm, test, throttle-test.
/// Limited to users logged on at the machine; none of the commands can cause a shutdown by itself,
/// and the throttle test undoes itself after 20 seconds.
/// </summary>
public sealed class ControlServer(GuardState state, WindowsGuardActions actions, ILogger<ControlServer> logger)
{
    public void Start(CancellationToken stop) => _ = Task.Run(() => Run(stop), stop);

    private async Task Run(CancellationToken stop)
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.InteractiveSid, null),
            PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, AccessControlType.Allow));

        while (!stop.IsCancellationRequested)
        {
            try
            {
                await using var pipe = NamedPipeServerStreamAcl.Create(StatusMessage.ControlPipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 1024, 1024, security);
                await pipe.WaitForConnectionAsync(stop);

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
                timeout.CancelAfter(3000);
                using var reader = new StreamReader(pipe, leaveOpen: true);
                await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
                string? command = await reader.ReadLineAsync(timeout.Token);
                await writer.WriteLineAsync(Handle(command?.Trim()));
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            catch (Exception ex)
            {
                logger.LogError(ex, "Control pipe error");
                await Task.Delay(5000, stop).ContinueWith(_ => { });
            }
        }
    }

    private string Handle(string? command)
    {
        switch (command)
        {
            case "arm": state.SetDryRun(false); return "ok";
            case "disarm": state.SetDryRun(true); return "ok";
            case "test": actions.ShowTestAlert(); return "ok";
            case "throttle-test": return actions.StartThrottleTest() ? "ok" : "error: a throttle is already active";
            default: return "error: unknown command";
        }
    }
}
