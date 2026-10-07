using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading.Channels;
using PinSentinel.Core;

namespace PinSentinel.Service;

/// <summary>
/// Streams status lines to UI clients over a named pipe. Publish never blocks:
/// each client has a small drop-oldest queue, so a stalled UI cannot hold up the guard loop.
/// </summary>
public sealed class StatusBroadcaster(ILogger<StatusBroadcaster> logger)
{
    private const int MaxClients = 8;
    private readonly List<Channel<byte[]>> _clients = [];

    public void Start(CancellationToken stop) => _ = Task.Run(() => AcceptLoop(stop), stop);

    public void Publish(StatusMessage message)
    {
        lock (_clients)
        {
            if (_clients.Count == 0) return;
            byte[] line = Encoding.UTF8.GetBytes(message.ToJson() + "\n");
            foreach (var client in _clients) client.Writer.TryWrite(line);
        }
    }

    private async Task AcceptLoop(CancellationToken stop)
    {
        // The service runs as SYSTEM; let logged-on users read the stream, nothing more.
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.Read | PipeAccessRights.Synchronize, AccessControlType.Allow));

        while (!stop.IsCancellationRequested)
        {
            try
            {
                var pipe = NamedPipeServerStreamAcl.Create(StatusMessage.PipeName, PipeDirection.Out,
                    NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous, 0, 16 * 1024, security);
                await pipe.WaitForConnectionAsync(stop);

                bool full;
                lock (_clients) full = _clients.Count >= MaxClients;
                if (full) pipe.Dispose();
                else _ = Task.Run(() => Pump(pipe, stop), stop);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                logger.LogError(ex, "Status pipe error");
                await Task.Delay(5000, stop).ContinueWith(_ => { });
            }
        }
    }

    private async Task Pump(NamedPipeServerStream pipe, CancellationToken stop)
    {
        var queue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(8) { FullMode = BoundedChannelFullMode.DropOldest });
        lock (_clients) _clients.Add(queue);
        try
        {
            await foreach (byte[] line in queue.Reader.ReadAllAsync(stop))
                await pipe.WriteAsync(line, stop);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
        finally
        {
            lock (_clients) _clients.Remove(queue);
            pipe.Dispose();
        }
    }
}
