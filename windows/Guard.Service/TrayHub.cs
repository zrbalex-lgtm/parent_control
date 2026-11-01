using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ParentControl.Core;

namespace ParentControl.Service;

/// <summary>
/// Named pipe server for tray apps. Each connection is bound to the Windows
/// session of the client process; the service talks to "the tray in session N".
/// </summary>
public sealed class TrayHub(TimeProvider time, ILogger<TrayHub> log) : BackgroundService
{
    private readonly ConcurrentDictionary<uint, Connection> _bySession = new();

    /// <summary>Messages from trays (request_time, ack_notice), drained by the worker.</summary>
    public ConcurrentQueue<(uint Session, IpcMessage Message)> Inbox { get; } = new();

    public bool IsConnected(uint session) => _bySession.ContainsKey(session);

    public bool Send(uint session, IpcMessage message) =>
        _bySession.TryGetValue(session, out var c) && c.TrySend(Ipc.Serialize(message));

    /// <summary>Latest foreground-window report from the tray in this session, if fresh.</summary>
    public IpcMessage? FreshActivity(uint session, TimeSpan maxAge) =>
        _bySession.TryGetValue(session, out var c) && c.LastActivity is { } a && time.GetUtcNow() - c.LastActivityAt <= maxAge
            ? a
            : null;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = NamedPipeServerStreamAcl.Create(
                    Ipc.PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, CreateSecurity());
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Cannot create tray pipe");
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
                continue;
            }

            try
            {
                await pipe.WaitForConnectionAsync(ct);
            }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync();
                break;
            }

            if (!GetNamedPipeClientSessionId(pipe.SafePipeHandle.DangerousGetHandle(), out var session))
            {
                log.LogWarning("Tray connected but its session is unknown; dropping");
                await pipe.DisposeAsync();
                continue;
            }
            _ = Task.Run(() => HandleAsync(new Connection(pipe, session), ct), ct);
        }
    }

    private async Task HandleAsync(Connection c, CancellationToken ct)
    {
        if (_bySession.TryGetValue(c.Session, out var old)) old.Dispose(); // one tray per session
        _bySession[c.Session] = c;
        log.LogInformation("Tray connected in session {Session}", c.Session);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await c.Reader.ReadLineAsync(ct);
                if (line is null) break;
                if (line.Length > 8192) continue;
                var m = Ipc.Parse(line);
                if (m is null) continue;
                switch (m.Type)
                {
                    case IpcTypes.Activity:
                        c.LastActivity = m;
                        c.LastActivityAt = time.GetUtcNow();
                        break;
                    case IpcTypes.RequestTime:
                    case IpcTypes.AckNotice:
                        Inbox.Enqueue((c.Session, m));
                        break;
                    case IpcTypes.Hello:
                        log.LogInformation("Tray {Version} hello from session {Session}", m.Version, c.Session);
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // client went away
        }
        finally
        {
            _bySession.TryRemove(new KeyValuePair<uint, Connection>(c.Session, c));
            c.Dispose();
            log.LogInformation("Tray disconnected from session {Session}", c.Session);
        }
    }

    private static PipeSecurity CreateSecurity()
    {
        var sec = new PipeSecurity();
        sec.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        sec.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        // Trays run as the signed-in (non-admin) user.
        sec.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return sec;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientSessionId(IntPtr pipe, out uint clientSessionId);

    private sealed class Connection : IDisposable
    {
        private readonly NamedPipeServerStream _pipe;
        private readonly StreamWriter _writer;
        private readonly object _writeLock = new();

        public Connection(NamedPipeServerStream pipe, uint session)
        {
            _pipe = pipe;
            Session = session;
            Reader = new StreamReader(pipe, new UTF8Encoding(false));
            _writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        }

        public uint Session { get; }
        public StreamReader Reader { get; }
        public IpcMessage? LastActivity { get; set; }
        public DateTimeOffset LastActivityAt { get; set; }

        public bool TrySend(string line)
        {
            lock (_writeLock)
            {
                try
                {
                    _writer.WriteLine(line);
                    return true;
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
                {
                    return false;
                }
            }
        }

        public void Dispose()
        {
            try { _pipe.Dispose(); } catch (IOException) { }
        }
    }
}
