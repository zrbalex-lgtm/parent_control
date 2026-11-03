using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ParentControl.Core;

namespace ParentControl.Service
{
    public static class Program
    {
        public const string ServiceName = "ParentControlGuard";

        public static string Version { get; } =
            typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

        private const string Usage = """
            ParentControl.Guard — screen time guard service

            Usage (run from an elevated PowerShell):
              ParentControl.Guard.exe                 run (as Windows service, or in this console)
              ParentControl.Guard.exe register CODE   register this PC with a one-time code from the admin app
              ParentControl.Guard.exe status          show registration, sync and today's usage
              ParentControl.Guard.exe probe           print what the guard sees on the console session
              ParentControl.Guard.exe clear-tamper    lift the lock after tampering on this PC (also works offline)
              ParentControl.Guard.exe unblock-logon   let children sign in again (after repeated re-logins were blocked)
              ParentControl.Guard.exe harden          apply folder/registry/service protection (installer runs it)
              ParentControl.Guard.exe watchdog        start the service if it was stopped (scheduled task)
              ParentControl.Guard.exe tray            tray app for the signed-in user (started by the service)
            """;

        public static async Task<int> Main(string[] args)
        {
            var command = args.FirstOrDefault()?.ToLowerInvariant();
            if (command is "-h" or "--help" or "help" or "/?")
            {
                Console.WriteLine(Usage);
                return 0;
            }

            // The tray runs as the signed-in (non-admin) user and needs no config or data folder.
            if (command == "tray") return ParentControl.Tray.TrayApp.Run();

            var options = LoadOptions();
            var data = new DataDirectory(options.DataDir);

            if (!DataDirectory.IsElevated())
            {
                Console.Error.WriteLine("Run this from an elevated (Administrator) PowerShell.");
                return 5;
            }

            try
            {
                data.EnsureSecured();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Cannot prepare data folder {data.Root}: {ex.Message}");
                // The service must keep enforcing from memory and the registry backup; tools may stop here.
                if (command is not (null or "run")) return 3;
            }

            return command switch
            {
                null or "run" => await RunAsync(args, options, data),
                "register" => await RegisterAsync(args, options, data),
                "status" => Status(options, data),
                "probe" => Probe(),
                "clear-tamper" => ClearTamper(data),
                "unblock-logon" => UnblockLogon(data),
                "harden" => Harden(data),
                "watchdog" => Watchdog(data),
                _ => Fail($"Unknown command '{command}'.\n\n{Usage}"),
            };
        }

        private static int Fail(string message)
        {
            Console.Error.WriteLine(message);
            return 2;
        }

        /// <summary>appsettings.json is optional: the server address is built in, so deleting the file changes nothing.</summary>
        private static GuardOptions LoadOptions()
        {
            var options = new GuardOptions();
            try
            {
                var config = new ConfigurationBuilder()
                    .SetBasePath(AppContext.BaseDirectory)
                    .AddJsonFile("appsettings.json", optional: true)
                    .Build();
                config.GetSection("Guard").Bind(options);
            }
            catch (Exception ex)
            {
                // A broken file must not stop the service either.
                Console.Error.WriteLine($"Ignoring appsettings.json: {ex.Message}");
                options = new GuardOptions();
            }
            return BuiltInConfig.ApplyTo(options);
        }

        // ---------------- run ----------------
        private static async Task<int> RunAsync(string[] args, GuardOptions options, DataDirectory data)
        {
            var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
            {
                Args = args.Skip(1).ToArray(),
                ContentRootPath = AppContext.BaseDirectory,
            });
            builder.Services.AddWindowsService(o => o.ServiceName = ServiceName);
            builder.Logging.AddProvider(new FileLoggerProvider(data.Logs));

            builder.Services.AddSingleton(options);
            builder.Services.AddSingleton(data);
            builder.Services.AddSingleton(TimeProvider.System);
            builder.Services.AddSingleton(new HttpClient { Timeout = TimeSpan.FromSeconds(20) });
            builder.Services.AddSingleton<IGuardApi, SupabaseApi>();
            builder.Services.AddSingleton(_ => new HeartbeatQueue(data.Queue));
            builder.Services.AddSingleton(_ => new StateStore(data.State));
            builder.Services.AddSingleton(_ => new CredentialStore(data.Credentials));
            builder.Services.AddSingleton<IBackupStore, RegistryBackupStore>();
            builder.Services.AddSingleton(_ => new TamperQueue(data.Tamper));
            builder.Services.AddSingleton<IAdminCheck, LocalAdmins>();
            builder.Services.AddSingleton<ISessionProbe, WindowsSessionProbe>();
            builder.Services.AddSingleton(sp => new GuardEngine(
                sp.GetRequiredService<IGuardApi>(),
                sp.GetRequiredService<HeartbeatQueue>(),
                sp.GetRequiredService<StateStore>(),
                sp.GetRequiredService<CredentialStore>(),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<ILogger<GuardEngine>>(),
                Version,
                sp.GetRequiredService<IBackupStore>(),
                sp.GetRequiredService<TamperQueue>()));
            builder.Services.AddSingleton(sp => new Policy(
                sp.GetRequiredService<GuardEngine>(), sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<IAdminCheck>()));
            builder.Services.AddSingleton<TrayHub>();
            builder.Services.AddHostedService(sp => sp.GetRequiredService<TrayHub>());
            builder.Services.AddSingleton<TrayLauncher>();
            builder.Services.AddSingleton<WindowsSessionControl>();
            builder.Services.AddSingleton<ISessionControl>(sp => sp.GetRequiredService<WindowsSessionControl>());
            builder.Services.AddSingleton<Enforcer>();
            builder.Services.AddSingleton<Updater>();
            builder.Services.AddSingleton<LogonBlocker>();
            builder.Services.AddHostedService<GuardWorker>();

            await builder.Build().RunAsync();
            return 0;
        }

        // ---------------- register ----------------
        private static async Task<int> RegisterAsync(string[] args, GuardOptions options, DataDirectory data)
        {
            var code = args.Skip(1).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(code))
            {
                Console.Write("Registration code: ");
                code = Console.ReadLine();
            }
            if (string.IsNullOrWhiteSpace(code)) return Fail("No code given.");

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            var api = new SupabaseApi(http, options);
            try
            {
                var result = await api.RegisterAsync(code.Trim(), Environment.MachineName, Version, CancellationToken.None);
                new CredentialStore(data.Credentials).Save(new DeviceCredentials(result.DeviceId, result.DeviceSecret));

                // A new registration starts from a clean server view.
                var stateStore = new StateStore(data.State);
                var state = stateStore.Load();
                state.Unauthorized = false;
                state.LastSyncError = null;
                stateStore.Save(state);

                Console.WriteLine($"Registered. Device id: {result.DeviceId}");
                return 0;
            }
            catch (DeviceUnauthorizedException)
            {
                return Fail("The code is wrong or expired. Create a new one in the admin app.");
            }
            catch (ApiException ex)
            {
                return Fail($"Could not reach the server: {ex.Message}");
            }
        }

        // ---------------- status ----------------
        private static int Status(GuardOptions options, DataDirectory data)
        {
            var creds = new CredentialStore(data.Credentials).Load();
            using var http = new HttpClient();
            var engine = new GuardEngine(new SupabaseApi(http, options), new HeartbeatQueue(data.Queue),
                new StateStore(data.State), new CredentialStore(data.Credentials), TimeProvider.System,
                NullLogger<GuardEngine>.Instance, Version, SafeBackup(), readOnly: true);
            var s = engine.State;
            var today = engine.LocalToday;

            Console.WriteLine($"Version:      {Version}");
            Console.WriteLine($"Data folder:  {data.Root}");
            Console.WriteLine($"Device:       {(creds is null ? engine.HasCredentials ? "device.json missing (backup present)" : "NOT REGISTERED" : creds.DeviceId.ToString())}");
            Console.WriteLine($"Tamper lock:  {(s.TamperLocked ? $"ON since {s.TamperLockedAt?.ToLocalTime():yyyy-MM-dd HH:mm} (clear: ParentControl.Guard.exe clear-tamper)" : "off")}");
            Console.WriteLine($"Queue:        {engine.QueueLength} minute(s) waiting to upload");
            Console.WriteLine($"Last sync:    {(s.LastSyncAt is { } at ? at.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : "never")}");
            if (s.LastSyncError is not null) Console.WriteLine($"Last error:   {s.LastSyncError}");
            if (s.Unauthorized) Console.WriteLine("              Server rejects this device — register again with a new code.");
            if (s.LastSync is { } sync)
            {
                Console.WriteLine($"Enabled:      {(sync.DeviceEnabled ? "yes" : "NO (disabled in admin app)")}");
                Console.WriteLine("Accounts:");
                foreach (var a in sync.Accounts)
                {
                    var child = sync.Children.FirstOrDefault(c => c.ChildId == a.ChildId)?.Name;
                    Console.WriteLine($"  {a.User,-20} {(child ?? "(not limited)")}");
                }
                Console.WriteLine($"Today ({today:yyyy-MM-dd}):");
                foreach (var c in sync.Children)
                {
                    var d = c.Days.FirstOrDefault(x => x.LocalDate == today);
                    var limit = d?.LimitMinutes is { } l ? $"{l + d.BonusMinutes} min" : "no limit";
                    Console.WriteLine($"  {c.Name,-20} spent {engine.SpentMinutes(c.ChildId, today)} min, limit {limit}, window {d?.WindowFrom}-{d?.WindowTo}");
                }
                foreach (var l in sync.Locks)
                {
                    var child = sync.Children.FirstOrDefault(c => c.ChildId == l.ChildId)?.Name;
                    Console.WriteLine($"  Lock #{l.Id} for {child}: from {l.LockFrom.ToLocalTime():HH:mm} until {(l.LockUntil is { } u ? u.ToLocalTime().ToString("HH:mm") : "cancelled")}");
                }
            }

            var sample = new WindowsSessionProbe(NullLogger<WindowsSessionProbe>.Instance).Sample();
            var decision = new Policy(engine, TimeProvider.System, new LocalAdmins(NullLogger<LocalAdmins>.Instance)).Evaluate(sample.Sid);
            Console.WriteLine($"Console now:  {sample.User ?? "-"} ({sample.State})");
            Console.WriteLine(!decision.Restricted
                ? "Decision:     not limited"
                : decision.Lock is { } reason
                    ? $"Decision:     LOCK ({reason})"
                    : $"Decision:     allowed, {(decision.RemainingMinutes is { } left ? $"{left} min left" : "no end today")}");
            return 0;
        }

        private static IBackupStore SafeBackup()
        {
            try
            {
                return new MemoryBackupStore { Value = new RegistryBackupStore().Load() };
            }
            catch (Exception)
            {
                return NullBackupStore.Instance;
            }
        }

        // ---------------- clear-tamper ----------------
        private static int ClearTamper(DataDirectory data)
        {
            File.WriteAllText(data.ClearTamperFlag, Environment.UserName);
            Console.WriteLine("Requested. The service lifts the tamper lock within a few seconds and tells the admin app.");
            return 0;
        }

        // ---------------- unblock-logon ----------------
        private static int UnblockLogon(DataDirectory data)
        {
            File.WriteAllText(data.UnblockLogonFlag, Environment.UserName);
            Console.WriteLine("Requested. The service restores sign-in for the children within a few seconds.");
            return 0;
        }

        // ---------------- harden ----------------
        /// <summary>Silent apply (no tamper events): this is the trusted baseline after install or update.</summary>
        private static int Harden(DataDirectory data)
        {
            var fixes = new Hardening(data, new ConsoleLogger()).Apply();
            Hardening.SetBaseline(Version);
            Console.WriteLine(fixes.Count == 0 ? "Protection already in place." : $"Applied {fixes.Count} protection setting(s).");
            return 0;
        }

        // ---------------- watchdog ----------------
        /// <summary>Started by the scheduled task as SYSTEM: brings the service back and leaves a note for it.</summary>
        private static int Watchdog(DataDirectory data)
        {
            using var sc = new System.ServiceProcess.ServiceController(ServiceName);
            System.ServiceProcess.ServiceControllerStatus status;
            try
            {
                status = sc.Status;
            }
            catch (InvalidOperationException)
            {
                return 0; // not installed
            }
            if (status is System.ServiceProcess.ServiceControllerStatus.Running
                or System.ServiceProcess.ServiceControllerStatus.StartPending
                or System.ServiceProcess.ServiceControllerStatus.ContinuePending)
            {
                return 0;
            }

            if (status == System.ServiceProcess.ServiceControllerStatus.Paused) sc.Continue();
            else sc.Start();

            var e = new TamperEvent
            {
                ClientId = Guid.NewGuid(),
                OccurredAt = DateTimeOffset.UtcNow,
                Type = TamperTypes.WatchdogFixed,
                Details = new() { ["what"] = "service", ["was"] = status.ToString() },
            };
            File.AppendAllText(data.Inbox, System.Text.Json.JsonSerializer.Serialize(e, Json.Options) + "\n");
            Console.WriteLine($"Service was {status}; started.");
            return 0;
        }

        // ---------------- probe ----------------
        private static int Probe()
        {
            var probe = new WindowsSessionProbe(NullLogger<WindowsSessionProbe>.Instance);
            var sample = probe.Sample();
            Console.WriteLine($"Console session id: {probe.ConsoleSessionId}");
            Console.WriteLine($"State: {sample.State}");
            Console.WriteLine($"User:  {sample.User ?? "-"}");
            Console.WriteLine($"SID:   {sample.Sid ?? "-"}");
            return 0;
        }
    }
}
