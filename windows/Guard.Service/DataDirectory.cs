using System.Security.AccessControl;
using System.Security.Principal;

namespace ParentControl.Service;

/// <summary>%ProgramData%\ParentControl, readable and writable only by SYSTEM and Administrators.</summary>
public sealed class DataDirectory
{
    public DataDirectory(string? overridePath)
    {
        Root = string.IsNullOrWhiteSpace(overridePath)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ParentControl")
            : Environment.ExpandEnvironmentVariables(overridePath);
    }

    public string Root { get; }
    public string Logs => Path.Combine(Root, "logs");
    public string Credentials => Path.Combine(Root, "device.json");
    public string State => Path.Combine(Root, "state.json");
    public string Queue => Path.Combine(Root, "queue.jsonl");
    public string Tamper => Path.Combine(Root, "tamper.jsonl");
    /// <summary>Dropped by the clear-tamper command; picked up by the running service.</summary>
    public string ClearTamperFlag => Path.Combine(Root, "clear-tamper.flag");

    public static bool IsElevated()
    {
        using var id = WindowsIdentity.GetCurrent();
        return id.IsSystem || new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>Creates the folder and replaces its ACL (no inheritance from ProgramData, which lets Users read).</summary>
    public void EnsureSecured()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Logs);

        var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        var sec = new DirectorySecurity();
        sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sidType in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
        {
            sec.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(sidType, null), FileSystemRights.FullControl,
                inherit, PropagationFlags.None, AccessControlType.Allow));
        }
        new DirectoryInfo(Root).SetAccessControl(sec);
    }
}
