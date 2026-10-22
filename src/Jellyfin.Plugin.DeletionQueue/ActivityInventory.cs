using System.Diagnostics;
using DeletionQueue.Core;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;

namespace Jellyfin.Plugin.DeletionQueue;

// Scan without a user visibility filter: disabled/restricted viewers and related editions still protect files.
public sealed class ActivityInventory(ILibraryManager library, IUserManager users, IUserDataManager data,
    ISessionManager sessions, QueueRuntime runtime, TargetResolver targets, TimeProvider clock)
{
    public bool Scan(VerifiedTarget target)
    {
        var timer = Stopwatch.StartNew();
        var allUsers = users.GetUsers().Take(501).ToArray();
        if (allUsers.Length > 500) return false;
        var tmdb = target.ActivityKey.Split(':').Last();
        var aliases = library.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [target.Kind == MediaKind.Series ? BaseItemKind.Series : BaseItemKind.Movie],
            HasAnyProviderIds = new() { ["Tmdb"] = [tmdb] }, Recursive = true, Limit = 201
        }).ToArray();
        if (aliases.Length is 0 or > 200 || aliases.All(i => i.Id != target.JellyfinItemId)) return false;
        var items = new Dictionary<Guid, BaseItem>();
        foreach (var alias in aliases)
        {
            if (targets.ActivityKey(alias) != target.ActivityKey) return false;
            items[alias.Id] = alias;
            if (target.Kind != MediaKind.Series) continue;
            var children = library.GetItemList(new InternalItemsQuery
            { ParentId = alias.Id, Recursive = true, IncludeItemTypes = [BaseItemKind.Season, BaseItemKind.Episode], Limit = 20001 }).ToArray();
            if (children.Length > 20000) return false;
            foreach (var child in children) items[child.Id] = child;
        }
        if ((long)items.Count * allUsers.Length > 100000) return false;
        var activity = new Dictionary<Guid, DateTimeOffset>(); var now = clock.GetUtcNow();
        foreach (var user in allUsers)
        foreach (var item in items.Values)
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(3)) return false;
            var saved = data.GetUserData(user, item);
            if (saved is null) return false;
            if (saved.LastPlayedDate is { } at)
            {
                var utc = new DateTimeOffset(DateTime.SpecifyKind(at, DateTimeKind.Utc));
                if (utc > now.AddMinutes(1)) return false;
                if (!activity.TryGetValue(user.Id, out var previous) || utc > previous) activity[user.Id] = utc;
            }
        }
        // The event collector protects active playback; a second independent live-session read blocks missed attribution.
        var live = sessions.Sessions.Take(1025).ToArray(); if (live.Length > 1024) return false;
        if (live.Any(s => s.NowPlayingItem is { } playing && items.ContainsKey(playing.Id))) return false;
        var before = runtime.Read();
        if (!activity.Any(pair => pair.Value >= now.AddDays(-before.Policy.ActivityDays) &&
                !before.Activity.Any(a => a.UserId == pair.Key && a.Key == target.ActivityKey && a.At >= pair.Value)) &&
            !before.Activity.Any(a => a.Key == target.ActivityKey && a.Sessions.Count > 0)) return true;
        runtime.Write(Guid.NewGuid(), "inventory", "HistoricalActivityVerified", state =>
        {
            foreach (var (user, at) in activity.Where(a => a.Value >= now.AddDays(-state.Policy.ActivityDays)))
            {
                var old = state.Activity.SingleOrDefault(a => a.UserId == user && a.Key == target.ActivityKey);
                if (old is null || old.At < at) QueueRules.RecordActivity(state, user, target.ActivityKey, null, false, at);
            }
            // A completed live-session scan can clear abandoned session IDs after a restart, never the activity timestamp.
            foreach (var a in state.Activity.Where(a => a.Key == target.ActivityKey && a.Sessions.Count > 0).ToArray())
            { a.Sessions.Clear(); QueueRules.RecordActivity(state, a.UserId, target.ActivityKey, null, false, now); }
            return true;
        });
        return true;
    }
}
