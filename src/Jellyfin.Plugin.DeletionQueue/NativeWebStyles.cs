using DeletionQueue.Infrastructure;
using MediaBrowser.Common.Configuration;

namespace Jellyfin.Plugin.DeletionQueue;

public sealed class NativeWebStyles(IApplicationPaths paths)
{
    // One bounded read-only scan on first use. A missing/incompatible Web client disables only this UI.
    private readonly Lazy<IReadOnlyList<string>> files = new(() => InstalledStylesheets.Find(paths.WebPath));
    public IReadOnlyList<string> Files => files.Value;
}
