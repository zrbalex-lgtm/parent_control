using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Extensions.Logging;
using ParentControl.Core;

namespace ParentControl.Service;

/// <summary>
/// Members of the local Administrators group, read from the account database
/// (not from a session token, which a standard user could not elevate anyway).
/// Cached for a minute.
/// </summary>
public sealed class LocalAdmins(ILogger<LocalAdmins> log) : IAdminCheck
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(1);
    private HashSet<string>? _cache;
    private DateTime _cachedAt;
    private readonly object _lock = new();

    public bool IsAdmin(string sid)
    {
        lock (_lock)
        {
            if (_cache is null || DateTime.UtcNow - _cachedAt > CacheFor)
            {
                _cache = ReadMembers();
                _cachedAt = DateTime.UtcNow;
            }
            return _cache.Contains(sid);
        }
    }

    /// <summary>SIDs that are direct members of BUILTIN\Administrators.</summary>
    public HashSet<string> ReadMembers()
    {
        // The group name is localized ("Администраторы"), so resolve it from its well-known SID.
        var groupName = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)
            .Translate(typeof(NTAccount)).Value.Split('\\').Last();

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IntPtr resume = IntPtr.Zero;
        int status;
        do
        {
            status = NetLocalGroupGetMembers(null, groupName, 0, out var buffer, -1, out var read, out _, ref resume);
            try
            {
                if (status is not (NERR_Success or ERROR_MORE_DATA))
                {
                    throw new System.ComponentModel.Win32Exception(status, "NetLocalGroupGetMembers failed");
                }
                var size = Marshal.SizeOf<LOCALGROUP_MEMBERS_INFO_0>();
                for (var i = 0; i < read; i++)
                {
                    var info = Marshal.PtrToStructure<LOCALGROUP_MEMBERS_INFO_0>(buffer + i * size);
                    if (info.lgrmi0_sid != IntPtr.Zero) result.Add(new SecurityIdentifier(info.lgrmi0_sid).Value);
                }
            }
            finally
            {
                if (buffer != IntPtr.Zero) NetApiBufferFree(buffer);
            }
        } while (status == ERROR_MORE_DATA);

        log.LogDebug("Local administrators: {Count}", result.Count);
        return result;
    }

    private const int NERR_Success = 0;
    private const int ERROR_MORE_DATA = 234;

    [StructLayout(LayoutKind.Sequential)]
    private struct LOCALGROUP_MEMBERS_INFO_0
    {
        public IntPtr lgrmi0_sid;
    }

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetLocalGroupGetMembers(string? serverName, string localGroupName, int level,
        out IntPtr bufptr, int prefMaxLen, out int entriesRead, out int totalEntries, ref IntPtr resumeHandle);

    [DllImport("netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buffer);
}
