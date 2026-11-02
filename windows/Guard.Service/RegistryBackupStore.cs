using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using ParentControl.Core;

namespace ParentControl.Service;

/// <summary>
/// Backup in HKLM\SOFTWARE\ParentControl (SYSTEM and Administrators only).
/// The device secret is additionally encrypted with DPAPI (machine scope).
/// Each part lives in its own value and is written only when it changed.
/// </summary>
public sealed class RegistryBackupStore : IBackupStore
{
    public const string KeyPath = @"SOFTWARE\ParentControl";

    private readonly Dictionary<string, string> _written = [];

    private sealed record CredBlob(Guid DeviceId, string Secret);

    public GuardBackup? Load()
    {
        using var key = Registry.LocalMachine.OpenSubKey(KeyPath);
        if (key is null) return null;

        DeviceCredentials? creds = null;
        if (key.GetValue("Device") is byte[] protectedBlob)
        {
            try
            {
                var json = Encoding.UTF8.GetString(Dpapi.Unprotect(protectedBlob));
                if (JsonSerializer.Deserialize<CredBlob>(json, Json.Options) is { } c) creds = new DeviceCredentials(c.DeviceId, c.Secret);
            }
            catch (Exception ex) when (ex is JsonException or System.ComponentModel.Win32Exception)
            {
                creds = null;
            }
        }

        return new GuardBackup
        {
            Credentials = creds,
            DeviceId = key.GetValue("DeviceId") is string id && Guid.TryParse(id, out var g) ? g : creds?.DeviceId,
            LastSync = Read<SyncResponse>(key, "LastSync"),
            LastSyncAt = key.GetValue("LastSyncAt") is string at && DateTimeOffset.TryParse(at, out var t) ? t : null,
            Usage = Read<Dictionary<DateOnly, Dictionary<string, List<long[]>>>>(key, "Usage") ?? [],
            TamperLocked = key.GetValue("TamperLocked") is int locked && locked != 0,
        };
    }

    public void Save(GuardBackup b)
    {
        using var key = OpenForWrite();

        var credJson = b.Credentials is { } c ? JsonSerializer.Serialize(new CredBlob(c.DeviceId, c.Secret), Json.Options) : null;
        if (Changed("Device", credJson))
        {
            if (credJson is null) key.DeleteValue("Device", throwOnMissingValue: false);
            else key.SetValue("Device", Dpapi.Protect(Encoding.UTF8.GetBytes(credJson)), RegistryValueKind.Binary);
        }

        WriteString(key, "DeviceId", b.DeviceId?.ToString());
        WriteString(key, "LastSync", b.LastSync is null ? null : JsonSerializer.Serialize(b.LastSync, Json.Options));
        WriteString(key, "LastSyncAt", b.LastSyncAt?.ToString("O"));
        WriteString(key, "Usage", JsonSerializer.Serialize(b.Usage, Json.Options));
        if (Changed("TamperLocked", b.TamperLocked ? "1" : "0"))
        {
            key.SetValue("TamperLocked", b.TamperLocked ? 1 : 0, RegistryValueKind.DWord);
        }
    }

    /// <summary>Removes the whole backup (used by uninstall with -RemoveData).</summary>
    public static void Delete() => Registry.LocalMachine.DeleteSubKeyTree(KeyPath, throwOnMissingSubKey: false);

    private bool Changed(string name, string? value)
    {
        var v = value ?? "\0null";
        if (_written.TryGetValue(name, out var last) && last == v) return false;
        _written[name] = v;
        return true;
    }

    private void WriteString(RegistryKey key, string name, string? value)
    {
        if (!Changed(name, value)) return;
        if (value is null) key.DeleteValue(name, throwOnMissingValue: false);
        else key.SetValue(name, value, RegistryValueKind.String);
    }

    private static T? Read<T>(RegistryKey key, string name) where T : class
    {
        if (key.GetValue(name) is not string json || json.Length == 0) return null;
        try
        {
            return JsonSerializer.Deserialize<T>(json, Json.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static RegistryKey OpenForWrite()
    {
        var key = Registry.LocalMachine.OpenSubKey(KeyPath, writable: true);
        if (key is not null) return key;
        key = Registry.LocalMachine.CreateSubKey(KeyPath, writable: true);
        Secure(key);
        return key;
    }

    /// <summary>SYSTEM and Administrators only; no inheritance from SOFTWARE (where Users can read).</summary>
    public static void Secure(RegistryKey key)
    {
        var sec = new RegistrySecurity();
        sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sidType in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
        {
            sec.AddAccessRule(new RegistryAccessRule(new SecurityIdentifier(sidType, null), RegistryRights.FullControl,
                InheritanceFlags.ContainerInherit, PropagationFlags.None, AccessControlType.Allow));
        }
        key.SetAccessControl(sec);
    }
}

/// <summary>Minimal DPAPI wrapper (machine scope), without the ProtectedData package.</summary>
internal static class Dpapi
{
    private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;
    private const int CRYPTPROTECT_LOCAL_MACHINE = 0x4;

    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    public static byte[] Protect(byte[] data) => Run(data, protect: true);
    public static byte[] Unprotect(byte[] data) => Run(data, protect: false);

    private static byte[] Run(byte[] data, bool protect)
    {
        var input = new DATA_BLOB { cbData = data.Length, pbData = Marshal.AllocHGlobal(Math.Max(1, data.Length)) };
        var output = new DATA_BLOB();
        try
        {
            Marshal.Copy(data, 0, input.pbData, data.Length);
            var ok = protect
                ? CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN | CRYPTPROTECT_LOCAL_MACHINE, ref output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref output);
            if (!ok) throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
            var result = new byte[output.cbData];
            Marshal.Copy(output.pbData, result, 0, output.cbData);
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(input.pbData);
            if (output.pbData != IntPtr.Zero) LocalFree(output.pbData);
        }
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DATA_BLOB pDataIn, string? szDataDescr, IntPtr pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(ref DATA_BLOB pDataIn, IntPtr ppszDataDescr, IntPtr pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DATA_BLOB pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);
}
