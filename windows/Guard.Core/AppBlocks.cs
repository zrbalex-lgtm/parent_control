using System.Text.RegularExpressions;

namespace ParentControl.Core;

/// <summary>One rule of an app group, as in app_groups (field exe/product/title, contains or regex).</summary>
public sealed record AppRule(string MatchField, string MatchType, string Pattern);

/// <summary>An app group blocked for a child until a time.</summary>
public sealed record AppBlockInfo(Guid ChildId, string GroupName, DateTimeOffset Until, List<AppRule> Rules);

/// <summary>A blocked group as the tray needs it: what to match and until when.</summary>
public sealed record TrayBlock(string Group, DateTimeOffset Until, List<AppRule> Rules);

/// <summary>What to do with a window of a blocked group.</summary>
public enum BlockAction { None, CloseTab, CloseApp }

public static class AppBlocks
{
    /// <summary>Browsers: a blocked site closes the tab, never the whole browser.</summary>
    private static readonly HashSet<string> Browsers = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome.exe", "msedge.exe", "firefox.exe", "opera.exe", "brave.exe", "vivaldi.exe", "browser.exe", "arc.exe",
    };

    /// <summary>Never closed even if a rule matches: closing them would break Windows itself.</summary>
    private static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer.exe", "ShellHost.exe", "SearchHost.exe", "StartMenuExperienceHost.exe", "ShellExperienceHost.exe",
        "LockApp.exe", "LogonUI.exe", "dwm.exe", "csrss.exe", "winlogon.exe", "ParentControl.Guard.exe",
    };

    private static readonly Dictionary<string, Regex?> RegexCache = new();
    private static readonly object CacheLock = new();

    public static bool IsBrowser(string? exe) => exe is not null && Browsers.Contains(exe);

    /// <summary>Blocks of this child still in force, for the tray.</summary>
    public static List<TrayBlock> ActiveFor(IEnumerable<AppBlockInfo> blocks, Guid childId, DateTimeOffset now) =>
        blocks.Where(b => b.ChildId == childId && b.Until > now && b.Rules.Count > 0)
            .GroupBy(b => b.GroupName, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(b => b.Until).First())
            .Select(b => new TrayBlock(b.GroupName, b.Until, b.Rules))
            .ToList();

    /// <summary>The first block whose rules match the window; null if none (or the window must not be touched).</summary>
    public static TrayBlock? Match(IEnumerable<TrayBlock> blocks, string? exe, string? product, string? title, DateTimeOffset now)
    {
        if (exe is not null && Protected.Contains(exe)) return null;
        return blocks.FirstOrDefault(b => b.Until > now && b.Rules.Any(r => Matches(r, exe, product, title)));
    }

    public static BlockAction ActionFor(string? exe) => IsBrowser(exe) ? BlockAction.CloseTab : BlockAction.CloseApp;

    /// <summary>Same semantics as fn_app_group on the server: case-insensitive "contains" or regex.</summary>
    public static bool Matches(AppRule rule, string? exe, string? product, string? title)
    {
        var value = rule.MatchField switch
        {
            "exe" => exe,
            "product" => product,
            _ => title,
        };
        if (string.IsNullOrEmpty(value) || string.IsNullOrEmpty(rule.Pattern)) return false;
        if (rule.MatchType != "regex") return value.Contains(rule.Pattern, StringComparison.OrdinalIgnoreCase);
        return RegexFor(rule.Pattern)?.IsMatch(value) == true;
    }

    /// <summary>Postgres regex to .NET: word boundaries \y, \m, \M become \b; a broken pattern matches nothing.</summary>
    private static Regex? RegexFor(string pattern)
    {
        lock (CacheLock)
        {
            if (RegexCache.TryGetValue(pattern, out var cached)) return cached;
            Regex? re;
            try
            {
                var net = Regex.Replace(pattern, @"(?<!\\)((?:\\\\)*)\\[ymM]", "$1\\b");
                re = new Regex(net, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
            }
            catch (ArgumentException)
            {
                re = null;
            }
            if (RegexCache.Count > 200) RegexCache.Clear();
            RegexCache[pattern] = re;
            return re;
        }
    }
}
