using System.Diagnostics;
using System.Threading.Channels;
using DeletionQueue.Core;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DeletionQueue;

public sealed class ActivityWorker(QueueRuntime runtime, TargetResolver resolver, LiveIdentity identity,
    ISessionManager sessions, IUserDataManager userData, TimeProvider clock, ILogger<ActivityWorker> logger, ActivityFence fence) : BackgroundService
{
    private sealed record Captured(Guid Item, Guid[] Users, string? Session, bool Stopped, DateTimeOffset At);
    private readonly Channel<Captured> events = Channel.CreateBounded<Captured>(new BoundedChannelOptions(256)
    { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly Dictionary<string, (long Position, DateTimeOffset At)> progress = [];
    private readonly object progressLock = new();
    private DateTimeOffset lastUtc;
    private long lastTimestamp;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // No journal replay or writes on Jellyfin's startup/event dispatch thread.
        await Task.Yield();
        try
        {
            if (!runtime.Available) return;
            sessions.PlaybackStart += Started;
            sessions.PlaybackProgress += Playback;
            sessions.PlaybackStopped += Stopped;
            userData.UserDataSaved += Interaction;
            runtime.Write(Guid.NewGuid(), "collector", "CollectionStarted", state =>
            {
                QueueRules.BreakCoverage(state, "RestartWarmup", clock.GetUtcNow());
                foreach (var p in state.Proposals.Where(p => p.Status == ProposalStatus.Claimed)) QueueRules.Uncertain(state, p.Id);
                return true;
            });
            runtime.MarkCollectorStarted();
            lastUtc = clock.GetUtcNow(); lastTimestamp = Stopwatch.GetTimestamp();
            var nextTick = Stopwatch.GetTimestamp();
            while (!stoppingToken.IsCancellationRequested)
            {
                // Bounded batch prevents playback traffic starving coverage checks or shutdown.
                for (var i = 0; i < 64 && events.Reader.TryRead(out var captured); i++)
                {
                    var key = resolver.ActivityKey(captured.Item);
                    if (key is null) { runtime.MarkCollectorFailed(); continue; }
                    runtime.Write(Guid.NewGuid(), "jellyfin-activity", "ActivityCaptured", state =>
                    {
                        foreach (var user in captured.Users) QueueRules.RecordActivity(state, user, key, captured.Session, captured.Stopped, captured.At);
                        return true;
                    });
                    fence.Captured();
                }
                if (Stopwatch.GetTimestamp() >= nextTick)
                { Tick(); nextTick = Stopwatch.GetTimestamp() + (30 * Stopwatch.Frequency); }
                await Task.Delay(100, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception)
        {
            runtime.MarkCollectorFailed();
            // Hosted-service exceptions must not reach HostOptions.BackgroundServiceExceptionBehavior.StopHost.
            try { logger.LogError("Deletion queue collector stopped safely; coverage is incomplete. Jellyfin remains running."); } catch (Exception) { }
        }
        finally
        {
            runtime.MarkCollectorFailed();
            try { sessions.PlaybackStart -= Started; } catch (Exception) { }
            try { sessions.PlaybackProgress -= Playback; } catch (Exception) { }
            try { sessions.PlaybackStopped -= Stopped; } catch (Exception) { }
            try { userData.UserDataSaved -= Interaction; } catch (Exception) { }
            events.Writer.TryComplete();
        }
    }

    private void Tick()
    {
        var now = clock.GetUtcNow();
        var elapsed = Stopwatch.GetElapsedTime(lastTimestamp);
        var clockHealthy = Math.Abs(((now - lastUtc) - elapsed).TotalSeconds) < 10;
        lastUtc = now; lastTimestamp = Stopwatch.GetTimestamp();
        runtime.Write(Guid.NewGuid(), "collector", "CoverageHeartbeatAndEvaluation", state =>
        {
            if (!clockHealthy) { state.ClockHealthy = false; QueueRules.BreakCoverage(state, "ClockDiscontinuity", now); }
            if (!runtime.CollectorHealthy) QueueRules.BreakCoverage(state, "CollectorFailure", now);
            state.CoverageHealthy = runtime.CollectorHealthy && state.ClockHealthy;
            state.LastHeartbeat = now;
            if (state.CoverageHealthy) state.CoverageProblem = "ContinuousObservationWarmup";
            foreach (var p in state.Proposals.Where(p => !p.Terminal && p.Claim is null))
            {
                var previous = (p.Status, p.Reason, p.NotBefore, p.EligibleSince, p.DelayFloorHours);
                var owner = identity.Account(p.OwnerId);
                var authorized = owner.Enabled && resolver.Accessible(owner.Id, p.Target.JellyfinItemId) &&
                    QueueRules.Authority(state, owner.Id) != ProposalAuthority.Denied &&
                    (QueueRules.Authority(state, owner.Id) == ProposalAuthority.AnyAccessibleItem || p.Target.OwnRequestVerified);
                // The execution worker supplies fresh target/inventory evidence. Do not erase its eligibility epoch here.
                if (!authorized || !state.CoverageHealthy)
                    QueueRules.Evaluate(state, p, new EvaluationEvidence(authorized, false, false, true, true), now);
                if (previous != (p.Status, p.Reason, p.NotBefore, p.EligibleSince, p.DelayFloorHours)) p.Revision++;
            }
            return true;
        });
    }

    private void Started(object? sender, PlaybackProgressEventArgs e) => Record(e, false, true);
    private void Playback(object? sender, PlaybackProgressEventArgs e) => Record(e, false, false);
    private void Stopped(object? sender, PlaybackStopEventArgs e) => Record(e, true, true);
    private void Record(PlaybackProgressEventArgs e, bool stopped, bool force)
    {
        try
        {
            if (e.Item is not (Movie or Series or Episode or Season)) return;
            var participants = e.Users.Take(33).ToArray();
            if (participants.Length is 0 or > 32 || participants.Any(u => u.Id == Guid.Empty) ||
                e.PlaySessionId is not { Length: > 0 and <= 128 }) { runtime.MarkCollectorFailed(); return; }
            var users = participants.Select(u => u.Id).Distinct().ToArray();
            var session = e.PlaySessionId; var now = clock.GetUtcNow(); var position = e.PlaybackPositionTicks ?? 0;
            lock (progressLock)
            {
                if (!force && progress.TryGetValue(session, out var previous) && now - previous.At < TimeSpan.FromMinutes(1) &&
                    Math.Abs((decimal)position - previous.Position) < TimeSpan.FromSeconds(30).Ticks) return;
                if (stopped) progress.Remove(session);
                else { if (progress.Count >= 1024 && !progress.ContainsKey(session)) { runtime.MarkCollectorFailed(); return; } progress[session] = (position, now); }
            }
            fence.Capturing();
            if (!events.Writer.TryWrite(new Captured(e.Item.Id, users, session, stopped, now))) { fence.Captured(); runtime.MarkCollectorFailed(); }
        }
        catch (Exception) { runtime.MarkCollectorFailed(); }
    }
    private void Interaction(object? sender, UserDataSaveEventArgs e)
    {
        try
        {
            if (e.SaveReason is not (UserDataSaveReason.TogglePlayed or UserDataSaveReason.UpdateUserRating or UserDataSaveReason.UpdateUserData) ||
                e.Item is not (Movie or Series or Episode or Season)) return;
            fence.Capturing();
            if (!events.Writer.TryWrite(new Captured(e.Item.Id, [e.UserId], null, false, clock.GetUtcNow()))) { fence.Captured(); runtime.MarkCollectorFailed(); }
        }
        catch (Exception) { runtime.MarkCollectorFailed(); }
    }
}
