namespace DeletionQueue.Core;

public static class NoticeRules
{
    public static void Publish(QueueState state, Proposal proposal) => Publish(state, [proposal]);

    public static void Publish(QueueState state, IEnumerable<Proposal> proposals)
    {
        var changed = proposals.Select(p => p.Id).ToHashSet();
        if (changed.Count == 0) return;
        var unread = state.Notices.Where(n => n.ReadAt is null).Select(n => (n.UserId, n.ProposalId)).ToHashSet();
        foreach (var pref in state.Preferences.Where(p => p.Value.Notifications))
        foreach (var id in pref.Value.Subscriptions.Where(changed.Contains))
        {
            // Coalesce unread notices per caller/proposal; never expose votes, identities or activity in notices.
            if (!unread.Add((pref.Key, id))) continue;
            if (state.Notices.Count >= 10000) { state.NoticeDeliveryIncomplete = true; return; }
            state.Notices.Add(new Notice(Guid.NewGuid(), pref.Key, id,
                "A subscribed deletion proposal changed. Review your private queue status."));
        }
    }

    public static void MarkRead(QueueState state, Guid noticeId, Guid caller, DateTimeOffset now)
    {
        var notice = state.Notices.SingleOrDefault(n => n.Id == noticeId && n.UserId == caller)
            ?? throw new QueueException("NotFound");
        if (notice.ReadAt is null) state.Notices[state.Notices.IndexOf(notice)] = notice with { ReadAt = now };
    }
}
