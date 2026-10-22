using DeletionQueue.Core;
using MediaBrowser.Common.Plugins;

namespace Jellyfin.Plugin.DeletionQueue;

// Optional adapter to the installed plugin's public configuration, not its browser globals or XML files.
// Read only on administrator request; never log, serialize or return the credential to the browser.
public sealed class EnhancedConnection(IPluginManager plugins)
{
    public (string Url, string Key)? Discover()
    {
        try
        {
            var candidates = plugins.Plugins.Where(p => p.IsEnabledAndSupported &&
                p.Id == Guid.Parse("f69e946a-4b3c-4e9a-8f0a-8d7c1b2c4d9b")).ToArray();
            if (candidates.Length != 1 || candidates[0].Instance is not IHasPluginConfiguration plugin) return null;
            return ReadConfiguration(plugin.Configuration);
        }
        catch (Exception) { return null; } // An absent/incompatible optional plugin cannot break Jellyfin.
    }

    public static (string Url, string Key)? ReadConfiguration(object config)
    {
        try
        {
            object? Get(string name) => config.GetType().GetProperty(name)?.GetValue(config);
            if (Get("JellyseerrEnabled") is not true || Get("JellyseerrUrls") is not string urls ||
                Get("JellyseerrApiKey") is not string key) return null;
            // Multiple upstreams require an explicit manual choice. Never guess or forward keys to every URL.
            var endpoints = urls.Split(['\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (endpoints.Length != 1 || !Valid(endpoints[0], key)) return null;
            return (new Uri(endpoints[0]).AbsoluteUri.TrimEnd('/'), key);
        }
        catch (Exception) { return null; }
    }

    public static bool Valid(string url, string? key) => url.Length <= 2048 &&
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" &&
        uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0 &&
        key is { Length: > 0 and <= 512 } && key.All(c => c is >= (char)33 and <= (char)126);
}
