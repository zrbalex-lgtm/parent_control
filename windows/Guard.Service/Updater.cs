using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using ParentControl.Core;

namespace ParentControl.Service;

/// <summary>
/// Self-update from GitHub Releases of the public repository (no credentials needed):
/// hourly manifest check, download, SHA-256 check, then update.ps1 (backup, install, rollback on failure)
/// once no child is at the PC. The admin app can switch updates off per PC.
/// </summary>
public sealed class Updater
{
    public const string Repo = "zrbalex-lgtm/parent_control";
    public const string ManifestUrl = $"https://github.com/{Repo}/releases/latest/download/manifest.json";
    private static readonly TimeSpan CheckEvery = TimeSpan.FromHours(1);
    private static readonly TimeSpan FirstCheckAfter = TimeSpan.FromMinutes(2);

    private readonly GuardEngine _engine;
    private readonly DataDirectory _data;
    private readonly TimeProvider _time;
    private readonly ILogger<Updater> _log;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(10) };
    private readonly bool _enabled;

    private Task? _work;
    private DateTimeOffset _nextCheck;
    private string? _readyDir;
    private string? _readyVersion;
    private DateTimeOffset _readySince;

    public Updater(GuardEngine engine, DataDirectory data, TimeProvider time, ILogger<Updater> log)
    {
        _engine = engine;
        _data = data;
        _time = time;
        _log = log;
        // Only the installed service updates itself (not a console run from a build folder).
        _enabled = WindowsServiceHelpers.IsWindowsService();
        _nextCheck = time.GetUtcNow() + FirstCheckAfter;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"ParentControlGuard/{Program.Version}");
        engine.CheckUpdateOutcome(Program.Version);
    }

    public string UpdatesDir => Path.Combine(_data.Root, "updates");

    /// <summary>Called from the worker loop; never blocks it (network work runs in the background).</summary>
    public void Tick(bool childActive)
    {
        if (!_enabled || _work is { IsCompleted: false }) return;
        var now = _time.GetUtcNow();

        if (_readyDir is not null)
        {
            if (UpdatePlanner.CanInstallNow(childActive, _readySince, now)) Install();
            return;
        }
        if (now < _nextCheck) return;
        _nextCheck = now + CheckEvery;
        _work = Task.Run(CheckAndDownloadAsync);
    }

    private async Task CheckAndDownloadAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(15));
            var manifest = await _http.GetFromJsonSafeAsync<UpdateManifest>(ManifestUrl, cts.Token);
            if (manifest is null) return;
            var s = _engine.State;
            if (!UpdatePlanner.IsWanted(Program.Version, manifest, s.FailedUpdateVersion, s.FailedUpdateAt, _time.GetUtcNow())) return;
            if (!await _engine.UpdateAllowedAsync(cts.Token))
            {
                _log.LogInformation("Version {Version} is available, but updates are off for this PC", manifest.Version);
                return;
            }

            _log.LogInformation("Downloading version {Version}", manifest.Version);
            Directory.CreateDirectory(UpdatesDir);
            CleanOldPackages();
            var zip = Path.Combine(UpdatesDir, $"{manifest.Version}.zip");
            var url = $"https://github.com/{Repo}/releases/download/v{manifest.Version}/{manifest.Asset}";
            await using (var src = await _http.GetStreamAsync(url, cts.Token))
            await using (var dst = File.Create(zip))
            {
                await src.CopyToAsync(dst, cts.Token);
            }

            var info = new FileInfo(zip);
            string hash;
            await using (var fs = File.OpenRead(zip))
            {
                hash = Convert.ToHexString(await SHA256.HashDataAsync(fs, cts.Token));
            }
            if (info.Length != manifest.Size || !hash.Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                _log.LogError("Downloaded package does not match the manifest (size {Size}, sha {Hash}); discarded", info.Length, hash);
                File.Delete(zip);
                return;
            }

            var dir = Path.Combine(UpdatesDir, manifest.Version);
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            ZipFile.ExtractToDirectory(zip, dir);
            File.Delete(zip);

            // The package must contain what it claims to be.
            var exe = Path.Combine(dir, "ParentControl.Guard.exe");
            var exeVersion = File.Exists(exe) ? FileVersionInfo.GetVersionInfo(exe).FileVersion : null;
            if (UpdatePlanner.Parse(exeVersion) != UpdatePlanner.Parse(manifest.Version) || !File.Exists(Path.Combine(dir, "update.ps1")))
            {
                _log.LogError("Package {Version} is incomplete or has version {ExeVersion}; discarded", manifest.Version, exeVersion);
                Directory.Delete(dir, recursive: true);
                return;
            }

            _readyDir = dir;
            _readyVersion = manifest.Version;
            _readySince = _time.GetUtcNow();
            _log.LogInformation("Version {Version} is ready; installs when no child is at the PC", manifest.Version);
        }
        catch (Exception ex)
        {
            _log.LogWarning("Update check failed: {Message}", ex.Message);
        }
    }

    /// <summary>Starts update.ps1 in its own process; it stops this service, so nothing runs after this.</summary>
    private void Install()
    {
        var dir = _readyDir!;
        var version = _readyVersion!;
        _readyDir = null;
        try
        {
            _engine.NoteUpdateAttempt(version);
            var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            var psi = new ProcessStartInfo(powershell) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in new[]
                     {
                         "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(dir, "update.ps1"),
                         "-InstallDir", AppContext.BaseDirectory.TrimEnd('\\'),
                         "-Log", Path.Combine(_data.Logs, $"update-{version}.log"),
                     })
            {
                psi.ArgumentList.Add(a);
            }
            Process.Start(psi);
            _log.LogInformation("Installing version {Version}; the service restarts in a moment", version);
        }
        catch (Exception ex)
        {
            _log.LogError("Cannot start the update: {Message}", ex.Message);
            _engine.CheckUpdateOutcome(Program.Version); // records the failure
        }
    }

    private void CleanOldPackages()
    {
        foreach (var d in Directory.EnumerateDirectories(UpdatesDir).Where(d => Path.GetFileName(d) != "backup"))
        {
            try { Directory.Delete(d, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        foreach (var f in Directory.EnumerateFiles(UpdatesDir, "*.zip"))
        {
            try { File.Delete(f); } catch (IOException) { }
        }
    }
}

internal static class HttpJson
{
    public static async Task<T?> GetFromJsonSafeAsync<T>(this HttpClient http, string url, CancellationToken ct)
    {
        using var res = await http.GetAsync(url, ct);
        if (!res.IsSuccessStatusCode) return default;
        return JsonSerializer.Deserialize<T>(await res.Content.ReadAsStringAsync(ct));
    }
}
