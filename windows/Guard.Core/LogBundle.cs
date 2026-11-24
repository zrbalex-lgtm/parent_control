namespace ParentControl.Core;

/// <summary>Builds the text sent when an adult asks for the PC's log.</summary>
public static class LogBundle
{
    /// <summary>Enough for a few busy days; the server keeps at most 2 MB.</summary>
    public const int MaxChars = 1_500_000;

    /// <summary>
    /// Joins files (oldest first) under name headers; drops NUL characters (left by power loss)
    /// and keeps the newest part when the total is too long.
    /// </summary>
    public static string Build(IEnumerable<(string Name, string Text)> files, int maxChars = MaxChars)
    {
        var parts = files.Select(f => $"===== {f.Name} =====\n{f.Text.Replace("\0", string.Empty).TrimEnd()}\n");
        var text = string.Join("\n", parts);
        if (text.Length <= maxChars) return text;
        var tail = text[^maxChars..];
        var cut = tail.IndexOf('\n');
        return "(начало обрезано)\n" + (cut >= 0 ? tail[(cut + 1)..] : tail);
    }

    /// <summary>Which files to send: the newest guard logs and the newest update log.</summary>
    public static IReadOnlyList<string> Pick(IEnumerable<string> fileNames, int guardDays = 3)
    {
        var names = fileNames.Select(n => Path.GetFileName(n)).ToList();
        var guard = names.Where(n => n.StartsWith("guard-", StringComparison.OrdinalIgnoreCase) && n.EndsWith(".log", StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).TakeLast(guardDays);
        var update = names.Where(n => n.StartsWith("update-", StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).TakeLast(1);
        return [.. update, .. guard];
    }
}
