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
            """;

        public static async Task<int> Main(string[] args)
        {
            var command = args.FirstOrDefault()?.ToLowerInvariant();
            if (command is "-h" or "--help" or "help" or "/?")
            {
                Console.WriteLine(Usage);
                return 0;
            }

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
                return 3;
            }

            return command switch
            {
                null or "run" => await RunAsync(args, options, data),
                "register" => await RegisterAsync(args, options, data),
                "status" => Status(options, data),
                "probe" => Probe(),
                _ => Fail($"Unknown command '{command}'.\n\n{Usage}"),
            };
        }

        private static int Fail(string message)
        {
            Console.Error.WriteLine(message);
            return 2;
        }

        private static GuardOptions LoadOptions()
        {
            var config = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: false)
                .Build();
            var options = new GuardOptions();
            config.GetSection("Guard").Bind(options);
            return options;
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
            builder.Services.AddSingleton<ISessionProbe, WindowsSessionProbe>();
            builder.Services.AddSingleton(sp => new GuardEngine(
                sp.GetRequiredService<IGuardApi>(),
                sp.GetRequiredService<HeartbeatQueue>(),
                sp.GetRequiredService<StateStore>(),
                sp.GetRequiredService<CredentialStore>(),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<ILogger<GuardEngine>>(),
                Version));
            builder.Services.AddSingleton(sp => new Policy(sp.GetRequiredService<GuardEngine>(), sp.GetRequiredService<TimeProvider>()));
            builder.Services.AddSingleton<ISessionControl, WindowsSessionControl>();
            builder.Services.AddSingleton<Enforcer>();
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
                NullLogger<GuardEngine>.Instance, Version);
            var s = engine.State;
            var today = engine.LocalToday;

            Console.WriteLine($"Version:      {Version}");
            Console.WriteLine($"Data folder:  {data.Root}");
            Console.WriteLine($"Device:       {(creds is null ? "NOT REGISTERED" : creds.DeviceId.ToString())}");
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
            var decision = new Policy(engine, TimeProvider.System).Evaluate(sample.Sid);
            Console.WriteLine($"Console now:  {sample.User ?? "-"} ({sample.State})");
            Console.WriteLine(!decision.Restricted
                ? "Decision:     not limited"
                : decision.Lock is { } reason
                    ? $"Decision:     LOCK ({reason})"
                    : $"Decision:     allowed, {(decision.RemainingMinutes is { } left ? $"{left} min left" : "no end today")}");
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
