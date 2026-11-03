using System.Text.Json.Serialization;

namespace ParentControl.Core;

/// <summary>manifest.json published with every GitHub Release by CI.</summary>
public sealed record UpdateManifest(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("asset")] string Asset,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("size")] long Size);

public sealed record UpdatePolicy([property: JsonPropertyName("auto_update")] bool AutoUpdate);

/// <summary>Pure rules for when to install a new version (kept here so they are unit tested).</summary>
public static class UpdatePlanner
{
    public static readonly TimeSpan RetryFailedAfter = TimeSpan.FromHours(24);
    public static readonly TimeSpan MaxWaitForIdle = TimeSpan.FromHours(12);

    public static Version? Parse(string? v) =>
        Version.TryParse(v?.Split('+', '-')[0], out var parsed) ? Normalize(parsed) : null;

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    /// <summary>Newer than what runs, and not the version that failed less than a day ago.</summary>
    public static bool IsWanted(string current, UpdateManifest manifest, string? failedVersion, DateTimeOffset? failedAt, DateTimeOffset now)
    {
        var cur = Parse(current);
        var next = Parse(manifest.Version);
        if (cur is null || next is null || next <= cur) return false;
        if (string.IsNullOrWhiteSpace(manifest.Asset) || manifest.Sha256.Length != 64 || manifest.Size <= 0) return false;
        if (failedVersion is not null && Parse(failedVersion) == next && failedAt is { } at && now - at < RetryFailedAfter) return false;
        return true;
    }

    /// <summary>Install when no child is at the PC; after 12 hours of waiting install anyway (a restart takes seconds).</summary>
    public static bool CanInstallNow(bool childActive, DateTimeOffset readySince, DateTimeOffset now) =>
        !childActive || now - readySince >= MaxWaitForIdle;
}
