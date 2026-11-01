using System.IO.Pipes;
using System.Text;
using ParentControl.Core;

namespace ParentControl.Tray;

/// <summary>Keeps a connection to the guard service; reconnects forever. Callbacks run on the UI thread.</summary>
internal sealed class PipeClient(SynchronizationContext ui, Action<IpcMessage> onMessage, Action<bool> onConnected) : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly object _writeLock = new();
    private StreamWriter? _writer;

    public bool Connected => _writer is not null;

    public void Start() => _ = Task.Run(() => RunAsync(_cts.Token));

    public void Send(IpcMessage m)
    {
        lock (_writeLock)
        {
            try
            {
                _writer?.WriteLine(Ipc.Serialize(m));
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
                _writer = null;
            }
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", Ipc.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(5000, ct);
                using var reader = new StreamReader(pipe, new UTF8Encoding(false));
                lock (_writeLock)
                {
                    _writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
                }
                Send(new IpcMessage { Type = IpcTypes.Hello, Version = typeof(PipeClient).Assembly.GetName().Version?.ToString(3) });
                ui.Post(_ => onConnected(true), null);

                while (!ct.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(ct);
                    if (line is null) break;
                    if (Ipc.Parse(line) is { } m) ui.Post(_ => onMessage(m), null);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException or OperationCanceledException)
            {
                // service not running yet or restarting
            }
            finally
            {
                lock (_writeLock) _writer = null;
            }

            ui.Post(_ => onConnected(false), null);
            try { await Task.Delay(3000, ct); } catch (OperationCanceledException) { break; }
        }
    }

    public void Dispose() => _cts.Cancel();
}
