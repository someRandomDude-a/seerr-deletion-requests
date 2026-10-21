namespace DeletionQueue.Infrastructure;

/// <summary>Read-only discovery of the installed Web client's lazy control CSS, never its executable bundles.</summary>
public static class InstalledStylesheets
{
    public static IReadOnlyList<string> Find(string directory)
    {
        var required = new[] { ".emby-select", ".formDialogHeader", ".countIndicator", ".listItemBody" };
        var best = new Dictionary<string, (string Name, long Size)>();
        var files = Directory.EnumerateFiles(directory, "*.css", SearchOption.TopDirectoryOnly).Take(257).ToArray();
        if (files.Length > 256) throw new InvalidDataException("Installed stylesheet discovery limit exceeded.");
        long read = 0;
        foreach (var file in files.Order(StringComparer.Ordinal))
        {
            var info = new FileInfo(file);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || info.Length > 2 * 1024 * 1024 ||
                info.Name.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-' or '_'))) continue;
            read += info.Length;
            if (read > 16 * 1024 * 1024) throw new InvalidDataException("Installed stylesheet discovery limit exceeded.");
            var css = File.ReadAllText(file);
            foreach (var selector in required)
                if (css.Contains(selector, StringComparison.Ordinal) && (!best.TryGetValue(selector, out var current) || info.Length < current.Size))
                    best[selector] = (info.Name, info.Length);
        }
        if (required.Any(s => !best.ContainsKey(s))) throw new InvalidDataException("Installed native control styles unavailable.");
        return best.Values.Select(v => v.Name).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }
}
