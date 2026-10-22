namespace DeletionQueue.Core;

public static class NoticeRules
{
    public static void Publish(QueueState state, Proposal proposal) => Publish(state, [proposal], DateTimeOffset.UtcNow);

    public static void Publish(QueueState state, IEnumerable<Proposal> proposals) => Publish(state, proposals, DateTimeOffset.UtcNow);

    public static void Publish(QueueState state, IEnumerable<Proposal> proposals, DateTimeOffset now)
    {
        var changed = proposals.ToDictionary(p => p.Id);
        if (changed.Count == 0) return;
        var unread = state.Notices.Where(n => n.ReadAt is null).Select(n => (n.UserId, n.ProposalId)).ToHashSet();
        foreach (var (id, proposal) in changed)
        foreach (var user in state.Activity.Where(a => a.Key == proposal.Target.ActivityKey &&
                     (a.At >= now.AddDays(-state.Policy.ActivityDays) || a.Sessions.Count > 0)).Select(a => a.UserId)
                     .Append(proposal.OwnerId).Concat(state.Preferences.Where(p => p.Value.Subscriptions.Contains(id)).Select(p => p.Key)).Distinct())
        {
            if (state.Preferences.GetValueOrDefault(user)?.Notifications == false) continue;
            // Coalesce unread notices per caller/proposal; never expose votes, identities or activity in notices.
            if (!unread.Add((user, id))) continue;
            if (state.Notices.Count >= 10000) { state.NoticeDeliveryIncomplete = true; return; }
            state.Notices.Add(new Notice(Guid.NewGuid(), user, id,
                "A deletion proposal relevant to you changed. Review your private status."));
        }
    }

    public static void MarkRead(QueueState state, Guid noticeId, Guid caller, DateTimeOffset now)
    {
        var notice = state.Notices.SingleOrDefault(n => n.Id == noticeId && n.UserId == caller)
            ?? throw new QueueException("NotFound");
        if (notice.ReadAt is null) state.Notices[state.Notices.IndexOf(notice)] = notice with { ReadAt = now };
    }
}
