using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeletionQueue.Core;
using DeletionQueue.Infrastructure;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Jellyfin.Data.Enums;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.DeletionQueue;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TargetSelection(Guid? ItemId, int? RequestId, bool Is4K) : IValidatableObject
{
    public IEnumerable<ValidationResult> Validate(ValidationContext context)
    {
        if ((ItemId is null) == (RequestId is null) || ItemId == Guid.Empty || RequestId <= 0)
            yield return new ValidationResult("Select exactly one valid item or request identifier.");
    }
}

public sealed class TargetResolver(ILibraryManager library, IUserManager users, QueueRuntime runtime, IServerApplicationHost host,
    ISeerrClientFactory seerrClients)
{
    public string? ActivityKey(Guid itemId) => library.GetItemById(itemId) is { } item ? ActivityKey(item) : null;
    public bool Accessible(Guid userId, Guid itemId)
    {
        var user = users.GetUserById(userId);
        return user is not null && library.GetItemById<BaseItem>(itemId, user) is Movie or Series;
    }

    public string? ActivityKey(BaseItem item)
    {
        if (item is Episode episode) item = library.GetItemById(episode.SeriesId) ?? item;
        else if (item is Season season) item = library.GetItemById(season.SeriesId) ?? item;
        if (item is not (Movie or Series)) return null;
        if (!item.ProviderIds.TryGetValue("Tmdb", out var id) || id.Length > 16 || !int.TryParse(id, out var number) || number <= 0)
            throw new QueueException("ActivityTargetUnverified");
        // All editions and library aliases of this title share a protective activity key.
        return $"{(item is Series ? "tv" : "movie")}:{number}";
    }

    public async Task<VerifiedTarget> ResolveAsync(Account caller, TargetSelection selection, CancellationToken ct)
    {
        if (selection.ItemId == Guid.Empty || selection.RequestId <= 0) throw new QueueException("InvalidTarget");
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(15)); ct = budget.Token;
        if ((selection.ItemId is null) == (selection.RequestId is null)) throw new QueueException("SelectExactlyOneTarget");
        var state = runtime.Read();
        if (QueueRules.Authority(state, caller.Id) == ProposalAuthority.Denied) throw new QueueException("ProposalNotPermitted");
        if (selection.ItemId is { } selectedItem && !Accessible(caller.Id, selectedItem)) throw new QueueException("TargetUnavailable");
        var config = state.Seerr;
        if (string.IsNullOrWhiteSpace(config.BaseUrl) || string.IsNullOrWhiteSpace(config.ApiKey) ||
            string.IsNullOrWhiteSpace(config.JellyfinServerId)) throw new QueueException("SeerrNotConfigured");
        using var seerr = seerrClients.Create(config);
        var serverSettings = await seerr.JellyfinSettingsAsync(ct);
        if (!string.Equals(config.JellyfinServerId, host.SystemId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Text(serverSettings, "serverId"), host.SystemId, StringComparison.OrdinalIgnoreCase))
            throw new QueueException("SeerrServerAssociationUnverified");
        BaseItem? item;
        if (selection.ItemId is { } itemId)
        {
            var user = users.GetUserById(caller.Id) ?? throw new QueueException("TargetUnavailable");
            item = library.GetItemById<BaseItem>(itemId, user);
        }
        else
        {
            var request = await seerr.RequestAsync(selection.RequestId!.Value, ct);
            if (!request.TryGetProperty("media", out var media) || Bool(request, "is4k") != selection.Is4K)
                throw new QueueException("TargetUnavailable");
            var type = Text(media, "mediaType");
            var id = Number(media, "tmdbId");
            if (type is not ("movie" or "tv") || id <= 0) throw new QueueException("TargetUnavailable");
            var user = users.GetUserById(caller.Id) ?? throw new QueueException("TargetUnavailable");
            item = library.GetItemList(new InternalItemsQuery(user)
            {
                IncludeItemTypes = [type == "movie" ? BaseItemKind.Movie : BaseItemKind.Series],
                HasAnyProviderIds = new() { ["Tmdb"] = [id.ToString(CultureInfo.InvariantCulture)] },
                Recursive = true, Limit = 1
            }).FirstOrDefault();
        }
        if (item is not (Movie or Series) || !Accessible(caller.Id, item.Id)) throw new QueueException("TargetUnavailable");
        var activityKey = ActivityKey(item) ?? throw new QueueException("TargetUnavailable");
        var tmdb = int.Parse(activityKey.Split(':')[1], CultureInfo.InvariantCulture);
        var details = await seerr.MediaAsync(item is Series, tmdb, ct);
        if (Number(details, "id") != tmdb || !details.TryGetProperty("mediaInfo", out var info) || info.ValueKind != JsonValueKind.Object ||
            Number(info, "tmdbId") != tmdb || Text(info, "mediaType") != (item is Series ? "tv" : "movie"))
            throw new QueueException("TargetUnavailable");
        var mediaId = Number(info, "id");
        var serviceProperty = selection.Is4K ? "serviceId4k" : "serviceId";
        var externalProperty = selection.Is4K ? "externalServiceId4k" : "externalServiceId";
        if (mediaId <= 0 || !info.TryGetProperty(serviceProperty, out var service) || service.ValueKind != JsonValueKind.Number ||
            service.GetInt32() < 0 || Number(info, externalProperty) <= 0) throw new QueueException("SeerrTargetNotManaged");
        // Provider identity alone does NOT establish visibility of the requested quality edition.
        // In particular, access to a 4K alias must not authorize a hidden standard-library target.
        var nativeProperty = selection.Is4K ? "jellyfinMediaId4k" : "jellyfinMediaId";
        if (!Guid.TryParse(Text(info, nativeProperty), out var nativeId) || nativeId == Guid.Empty)
            throw new QueueException("TargetEditionUnverified");
        var currentUser = users.GetUserById(caller.Id) ?? throw new QueueException("TargetUnavailable");
        var mappedItem = library.GetItemById<BaseItem>(nativeId, currentUser);
        if (mappedItem is not (Movie or Series) || mappedItem.GetType() != item.GetType() || ActivityKey(mappedItem) != activityKey ||
            (selection.ItemId is { } selectedId && selectedId != nativeId))
            throw new QueueException("TargetEditionUnverified");
        item = mappedItem;
        // Do not include service ID in the lock key: remapping must not create a second active proposal.
        // There is one configured Seerr association. Changing its address must not evade active scope locks.
        var key = $"seerr|{activityKey}|{selection.Is4K}";
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{config.BaseUrl}|{key}|{mediaId}|{nativeId:D}|{service.GetInt32()}|{Number(info, externalProperty)}|{config.Revision}")));
        var own = await SeerrOwnership.OwnsAsync(info, caller.Id, selection.Is4K, item is Series, seerr.UserAsync, ct);
        // Seerr is explicitly trusted for Arr scope/files; the legacy FilesPinned field remains false and is not a gate.
        var title = item.Name ?? "Media";
        if (title.Length > 512) title = title[..512];
        return new VerifiedTarget(key, activityKey, fingerprint, item.Id, title,
            item is Series ? MediaKind.Series : MediaKind.Movie, mediaId, selection.Is4K, true, own, false, config.Revision,
            $"{(item is Series ? "sonarr" : "radarr")}|{service.GetInt32()}|{Number(info, externalProperty)}");
    }

    private static int Number(JsonElement item, string key) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) ? n : 0;
    private static bool Bool(JsonElement item, string key) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.True;
    private static string Text(JsonElement item, string key) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
}
