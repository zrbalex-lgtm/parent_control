using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using ParentControl.Core;

namespace ParentControl.Service;

/// <summary>A deviation found (and already repaired) by <see cref="Hardening"/>.</summary>
public sealed record HardeningFix(string Type, Dictionary<string, string?> Details);

/// <summary>
/// Keeps the installation locked down: folder and registry ACLs, the service's own permissions,
/// Safe Mode start and the watchdog task. Every check repairs what it finds and returns what it repaired.
/// The first run after install (no baseline yet) repairs silently, so an upgrade never looks like tampering.
/// </summary>
public sealed class Hardening(DataDirectory data, ILogger log)
{
    public const string WatchdogTask = "ParentControl Watchdog";
    public const string BaselineValue = "HardenedVersion";

    // Service DACL: SYSTEM and Administrators manage it; interactive and service users may only query it.
    public const string ServiceDacl =
        "D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWLOCRRC;;;IU)(A;;CCLCSWLOCRRC;;;SU)";

    private static readonly SecurityIdentifier System = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Admins = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);
    private static readonly SecurityIdentifier TrustedInstaller =
        new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");
    private static readonly SecurityIdentifier CreatorOwner = new(WellKnownSidType.CreatorOwnerSid, null);

    private static readonly HashSet<SecurityIdentifier> Trusted = [System, Admins, TrustedInstaller];

    public string InstallDir { get; } = AppContext.BaseDirectory.TrimEnd('\\');
    public string Exe { get; } = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "ParentControl.Guard.exe");

    /// <summary>True once an installer or the service applied the hardening for this version.</summary>
    public static bool HasBaseline()
    {
        using var key = Registry.LocalMachine.OpenSubKey(RegistryBackupStore.KeyPath);
        return key?.GetValue(BaselineValue) is string;
    }

    public static void SetBaseline(string version)
    {
        using var key = Registry.LocalMachine.CreateSubKey(RegistryBackupStore.KeyPath, writable: true);
        key.SetValue(BaselineValue, version, RegistryValueKind.String);
    }

    /// <summary>Runs every check; each failing check is logged and skipped, never thrown.</summary>
    public List<HardeningFix> Apply()
    {
        var fixes = new List<HardeningFix>();
        void Run(string name, Func<HardeningFix?> check)
        {
            try
            {
                if (check() is { } fix)
                {
                    fixes.Add(fix);
                    log.LogWarning("Hardening fixed {Check}: {Details}", name, string.Join(", ", fix.Details.Select(kv => $"{kv.Key}={kv.Value}")));
                }
            }
            catch (Exception ex)
            {
                log.LogError("Hardening check {Check} failed: {Message}", name, ex.Message);
            }
        }

        Run("install folder", () => FixFolder(InstallDir, usersRead: true, "install"));
        Run("data folder", () => FixFolder(data.Root, usersRead: false, "data"));
        Run("registry", FixRegistry);
        Run("service dacl", FixServiceDacl);
        Run("service config", FixServiceConfig);
        Run("safe boot", FixSafeBoot);
        Run("watchdog", FixWatchdog);
        return fixes;
    }

    // ---------------- folders ----------------

    private HardeningFix? FixFolder(string path, bool usersRead, string what)
    {
        if (!Directory.Exists(path)) return null;
        var problems = new List<string>();

        var dir = new DirectoryInfo(path);
        var sec = dir.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
        if (!sec.AreAccessRulesProtected) problems.Add("inherits");
        if (HasUntrustedWrite(sec.GetAccessRules(true, true, typeof(SecurityIdentifier)), FileWriteMask)) problems.Add("writable");
        if (!IsTrustedOwner(sec.GetOwner(typeof(SecurityIdentifier)))) problems.Add("owner");

        if (problems.Count > 0) dir.SetAccessControl(FolderSecurity(usersRead));
        if (!IsTrustedOwner(sec.GetOwner(typeof(SecurityIdentifier)))) SetOwner(dir);

        // Files and subfolders: no explicit rules that let others write, and trusted owners.
        foreach (var entry in dir.EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
        {
            try
            {
                FixEntry(entry, problems);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                // Temp files come and go while the service writes.
            }
        }

        return problems.Count == 0 ? null : new HardeningFix(TamperTypes.AclFixed, new()
        {
            ["what"] = what,
            ["path"] = path,
            ["problems"] = string.Join(",", problems.Take(10)),
        });
    }

    private static void FixEntry(FileSystemInfo entry, List<string> problems)
    {
        FileSystemSecurity es = entry is DirectoryInfo d
            ? d.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner)
            : ((FileInfo)entry).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
        var bad = es.AreAccessRulesProtected
                  || HasUntrustedWrite(es.GetAccessRules(true, false, typeof(SecurityIdentifier)), FileWriteMask);
        if (bad)
        {
            // Drop explicit rules and inherit again from the locked-down root.
            if (entry is DirectoryInfo di)
            {
                var reset = new DirectorySecurity();
                reset.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
                di.SetAccessControl(reset);
            }
            else
            {
                var reset = new FileSecurity();
                reset.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
                ((FileInfo)entry).SetAccessControl(reset);
            }
            problems.Add($"entry:{entry.Name}");
        }
        if (!IsTrustedOwner(es.GetOwner(typeof(SecurityIdentifier))))
        {
            SetOwner(entry);
            problems.Add($"owner:{entry.Name}");
        }
    }

    private static DirectorySecurity FolderSecurity(bool usersRead)
    {
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        var sec = new DirectorySecurity();
        sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        sec.AddAccessRule(new FileSystemAccessRule(System, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(Admins, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        if (usersRead)
        {
            // The tray runs as the signed-in user and must be able to start the exe.
            sec.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
        }
        return sec;
    }

    private static void SetOwner(FileSystemInfo entry)
    {
        if (entry is DirectoryInfo d)
        {
            var s = new DirectorySecurity();
            s.SetOwner(Admins);
            d.SetAccessControl(s);
        }
        else if (entry is FileInfo f)
        {
            var s = new FileSecurity();
            s.SetOwner(Admins);
            f.SetAccessControl(s);
        }
    }

    // Rights that let someone change, replace or delete files (incl. generic write/all bits on inherit-only ACEs).
    private const int FileWriteMask =
        (int)(FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteExtendedAttributes
              | FileSystemRights.WriteAttributes | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles
              | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership)
        | GenericWrite | GenericAll;

    private const int RegistryWriteMask =
        (int)(RegistryRights.SetValue | RegistryRights.CreateSubKey | RegistryRights.Delete
              | RegistryRights.ChangePermissions | RegistryRights.TakeOwnership | RegistryRights.CreateLink)
        | GenericWrite | GenericAll;

    private const int GenericAll = 0x10000000;
    private const int GenericWrite = 0x40000000;

    internal static bool HasUntrustedWrite(AuthorizationRuleCollection rules, int writeMask)
    {
        foreach (AccessRule rule in rules)
        {
            if (rule.AccessControlType != AccessControlType.Allow) continue;
            if (rule.IdentityReference is not SecurityIdentifier sid || Trusted.Contains(sid)) continue;
            // CREATOR OWNER only matters if someone untrusted can create objects, which is checked on its own.
            if (sid == CreatorOwner) continue;
            var mask = rule switch
            {
                FileSystemAccessRule f => (int)f.FileSystemRights,
                RegistryAccessRule r => (int)r.RegistryRights,
                _ => 0,
            };
            if ((mask & writeMask) != 0) return true;
        }
        return false;
    }

    private static bool IsTrustedOwner(IdentityReference? owner) =>
        owner is SecurityIdentifier sid && Trusted.Contains(sid);

    // ---------------- registry ----------------

    private HardeningFix? FixRegistry()
    {
        using var key = Registry.LocalMachine.CreateSubKey(RegistryBackupStore.KeyPath, writable: true);
        var sec = key.GetAccessControl(AccessControlSections.Access);
        var readable = false;
        foreach (RegistryAccessRule rule in sec.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType == AccessControlType.Allow && rule.IdentityReference is SecurityIdentifier sid && !Trusted.Contains(sid))
            {
                readable = true;
            }
        }
        if (sec.AreAccessRulesProtected && !readable) return null;

        RegistryBackupStore.Secure(key);
        return new HardeningFix(TamperTypes.AclFixed, new() { ["what"] = "registry", ["path"] = @"HKLM\" + RegistryBackupStore.KeyPath });
    }

    // ---------------- service ----------------

    private HardeningFix? FixServiceDacl()
    {
        var (code, output) = Sc("sdshow", Program.ServiceName);
        if (code != 0) return null; // not installed as a service (console run)
        var current = output.Trim();
        var dacl = current.Split("S:", 2)[0].Trim();
        if (ServiceDaclIsSafe(dacl)) return null;

        var (setCode, setOut) = Sc("sdset", Program.ServiceName, ServiceDacl);
        if (setCode != 0) throw new InvalidOperationException($"sc sdset failed ({setCode}): {setOut.Trim()}");
        return new HardeningFix(TamperTypes.ServiceDaclFixed, new() { ["was"] = dacl.Length > 300 ? dacl[..300] : dacl });
    }

    /// <summary>No one but SYSTEM, Administrators or TrustedInstaller may start, stop, reconfigure or delete the service.</summary>
    internal static bool ServiceDaclIsSafe(string dacl)
    {
        if (!dacl.StartsWith("D:", StringComparison.Ordinal)) return false;
        string[] dangerous = ["RP", "WP", "DT", "SD", "WD", "WO", "DC", "GA", "GW"];
        foreach (var ace in dacl[2..].Split(['(', ')'], StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = ace.Split(';');
            if (parts.Length < 6 || parts[0] != "A") continue;
            var trustee = parts[5];
            if (trustee is "SY" or "BA" or "S-1-5-18" or "S-1-5-32-544" || trustee == TrustedInstaller.Value) continue;
            var rights = parts[2];
            for (var i = 0; i + 1 < rights.Length; i += 2)
            {
                if (dangerous.Contains(rights.Substring(i, 2))) return false;
            }
        }
        return true;
    }

    /// <summary>Auto start and restart on failure.</summary>
    private HardeningFix? FixServiceConfig()
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{Program.ServiceName}");
        if (key is null) return null;
        var problems = new List<string>();
        if (key.GetValue("Start") is not int start || start != 2)
        {
            Sc("config", Program.ServiceName, "start=", "auto");
            problems.Add("start");
        }
        if (key.GetValue("FailureActions") is not byte[])
        {
            Sc("failure", Program.ServiceName, "reset=", "86400", "actions=", "restart/5000/restart/5000/restart/30000");
            Sc("failureflag", Program.ServiceName, "1");
            problems.Add("recovery");
        }
        return problems.Count == 0 ? null
            : new HardeningFix(TamperTypes.ServiceDaclFixed, new() { ["what"] = "config", ["problems"] = string.Join(",", problems) });
    }

    // ---------------- safe mode ----------------

    /// <summary>Registers the service for Safe Mode (minimal and with networking), so it enforces there too.</summary>
    private HardeningFix? FixSafeBoot()
    {
        var fixedModes = new List<string>();
        foreach (var mode in new[] { "Minimal", "Network" })
        {
            var path = $@"SYSTEM\CurrentControlSet\Control\SafeBoot\{mode}\{Program.ServiceName}";
            using var existing = Registry.LocalMachine.OpenSubKey(path);
            if (existing?.GetValue("") as string == "Service") continue;
            using var key = Registry.LocalMachine.CreateSubKey(path, writable: true);
            key.SetValue("", "Service", RegistryValueKind.String);
            fixedModes.Add(mode);
        }
        return fixedModes.Count == 0 ? null
            : new HardeningFix(TamperTypes.SafeBootFixed, new() { ["modes"] = string.Join(",", fixedModes) });
    }

    // ---------------- watchdog task ----------------

    /// <summary>Scheduled task (SYSTEM, every 5 minutes) that starts the service if someone stopped it.</summary>
    private HardeningFix? FixWatchdog()
    {
        if (Sc("query", Program.ServiceName).Code != 0) return null; // no service, nothing to watch
        var (code, output) = Run("schtasks.exe", "/Query", "/TN", WatchdogTask, "/XML");
        var expected = $"\"{Exe}\" watchdog";
        if (code == 0
            && output.Contains("<UserId>S-1-5-18</UserId>", StringComparison.OrdinalIgnoreCase)
            && output.Contains(Exe, StringComparison.OrdinalIgnoreCase)
            && !output.Contains("<Enabled>false</Enabled>", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var (createCode, createOut) = Run("schtasks.exe", "/Create", "/F", "/TN", WatchdogTask, "/RU", "SYSTEM",
            "/SC", "MINUTE", "/MO", "5", "/RL", "HIGHEST", "/TR", expected);
        if (createCode != 0) throw new InvalidOperationException($"schtasks /Create failed ({createCode}): {createOut.Trim()}");
        return new HardeningFix(TamperTypes.WatchdogFixed, new() { ["what"] = "task", ["was"] = code == 0 ? "changed" : "missing" });
    }

    // ---------------- helpers ----------------

    internal static (int Code, string Output) Sc(params string[] args) => Run("sc.exe", args);

    internal static (int Code, string Output) Run(string file, params string[] args)
    {
        var psi = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"cannot start {file}");
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(30_000))
        {
            try { p.Kill(); } catch (InvalidOperationException) { }
            return (-1, "timeout");
        }
        return (p.ExitCode, stdout.Result + stderr.Result);
    }
}
