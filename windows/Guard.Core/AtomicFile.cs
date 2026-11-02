using System.Text;

namespace ParentControl.Core;

internal static class AtomicFile
{
    /// <summary>Writes via a temp file and rename so a crash never leaves a half-written file.</summary>
    public static void WriteAllText(string path, string text)
    {
        var tmp = path + ".tmp";
        var dir = Path.GetDirectoryName(path);
        // The data folder may have been deleted while the service runs.
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var w = new StreamWriter(fs, new UTF8Encoding(false)))
        {
            w.Write(text);
            w.Flush();
            fs.Flush(flushToDisk: true);
        }
        File.Move(tmp, path, overwrite: true);
    }

    public static string? TryReadAllText(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
