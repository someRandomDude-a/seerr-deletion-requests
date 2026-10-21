using DeletionQueue.Core;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.DeletionQueue.Api;

[ApiController]
[Authorize]
[Route("DeletionQueue")]
[TypeFilter(typeof(QueueApiFilter))]
[RequestSizeLimit(32768)]
public sealed class QueueController(QueueRuntime runtime, LiveIdentity identity, TargetResolver targets,
    IUserManager users, IServerApplicationHost host, TimeProvider clock, NativeWebStyles webStyles) : ControllerBase
{
    [AllowAnonymous, HttpGet("ui")]
    public IActionResult Ui() => Resource("user.html", "text/html; charset=utf-8");

    [AllowAnonymous, HttpGet("assets/native-styles")]
    public IActionResult NativeStyles() => Ok(webStyles.Files);

    [AllowAnonymous, HttpGet("assets/{name}")]
    public IActionResult Asset(string name) => name switch
    {
        "user.js" => Resource("user.js", "application/javascript"),
        "theme.js" => Resource("theme.js", "application/javascript"),
        "queue.css" => Resource("queue.css", "text/css"),
        _ => NotFound()
    };

    [HttpGet("proposals")]
    public IActionResult List([FromQuery] int skip = 0, [FromQuery] int take = 50)
    {
        var caller = identity.Caller(User);
        if (skip < 0 || take is < 1 or > 100) throw new QueueException("InvalidPagination");
        var state = runtime.Read();
        return Ok(state.Proposals.Where(p => targets.Accessible(caller.Id, p.Target.JellyfinItemId))
            .OrderByDescending(p => p.CreatedAt).Skip(skip).Take(take)
            .Select(p => QueueRules.Project(state, p, caller.Id, clock.GetUtcNow())).ToArray());
    }

    [HttpGet("proposals/{id:guid}")]
    public IActionResult Detail(Guid id)
    {
        var caller = identity.Caller(User);
        var state = runtime.Read();
        var p = Visible(state, id, caller.Id);
        return Ok(QueueRules.Project(state, p, caller.Id, clock.GetUtcNow()));
    }

    [HttpPost("targets/preview")]
    public async Task<IActionResult> Preview(TargetSelection selection, CancellationToken ct)
    {
        var caller = identity.Caller(User);
        var target = await targets.ResolveAsync(caller, selection, ct);
        var state = runtime.Read();
        QueueRules.RequireProposalAuthority(state, identity.Caller(User), target, targets.Accessible(caller.Id, target.JellyfinItemId));
        return Ok(new { target.Title, target.Fingerprint, Snapshot = QueueRules.Snapshot(state, target, caller.Id),
            target.Kind, target.Is4K, ExecutionAvailable = false });
    }

    [HttpPost("proposals")]
    public async Task<IActionResult> Propose(ProposeBody body, CancellationToken ct)
    {
        var caller = identity.Caller(User);
        if (body.CommandId == Guid.Empty) throw new QueueException("CommandIdRequired");
        var target = await targets.ResolveAsync(caller, body.Selection, ct);
        if (body.Fingerprint != target.Fingerprint) throw new QueueException("TargetChanged");
        var id = runtime.Write(body.CommandId, caller.Id.ToString(), "ProposalCreatedOrJoined", state =>
        {
            if (state.Seerr.Revision != target.ConfigurationRevision) throw new QueueException("RevisionChanged");
            var proposal = QueueRules.Propose(state, identity.Caller(User), target,
                targets.Accessible(caller.Id, target.JellyfinItemId), body.Snapshot, clock.GetUtcNow());
            return proposal.Id;
        });
        return Detail(id);
    }

    [HttpPost("proposals/{id:guid}/consent")]
    public IActionResult Consent(Guid id, ConsentBody body)
    {
        if (!Enum.IsDefined(body.Answer)) throw new QueueException("InvalidAnswer");
        var caller = identity.Caller(User);
        runtime.Write(body.CommandId, caller.Id.ToString(), "ConsentChanged", state =>
        {
            var p = Visible(state, id, caller.Id);
            QueueRules.Consent(state, id, identity.Caller(User), body.Revision, body.Snapshot,
                body.Answer, targets.Accessible(caller.Id, p.Target.JellyfinItemId), clock.GetUtcNow());
            return true;
        });
        return Detail(id);
    }

    [HttpPost("proposals/{id:guid}/undo")]
    public IActionResult Undo(Guid id, RevisionBody body)
    {
        var caller = identity.Caller(User);
        runtime.Write(body.CommandId, caller.Id.ToString(), "OwnerUndo", state =>
        { _ = Visible(state, id, caller.Id); _ = identity.Caller(User); QueueRules.Undo(state, id, caller.Id, body.Revision); return true; });
        return NoContent();
    }

    [HttpGet("me/preferences")]
    public IActionResult Preferences()
    {
        var caller = identity.Caller(User);
        return Ok(runtime.Read().Preferences.GetValueOrDefault(caller.Id) ?? new Preferences());
    }

    [HttpPatch("me/preferences")]
    public IActionResult Preferences(PreferencesBody body)
    {
        var caller = identity.Caller(User);
        runtime.Write(body.CommandId, caller.Id.ToString(), "PreferencesChanged", state =>
        { _ = identity.Caller(User); QueueRules.UpdatePreferences(state, caller.Id, body.Revision, body.DelayHours, body.Notifications); return true; });
        return Preferences();
    }

    [HttpPost("proposals/{id:guid}/subscription"), HttpDelete("proposals/{id:guid}/subscription")]
    public IActionResult Subscribe(Guid id, RevisionBody body)
    {
        var caller = identity.Caller(User);
        runtime.Write(body.CommandId, caller.Id.ToString(), "SubscriptionChanged", state =>
        {
            _ = Visible(state, id, caller.Id);
            _ = identity.Caller(User);
            QueueRules.Subscription(state, id, caller.Id, body.Revision, Request.Method == "POST");
            return true;
        });
        return NoContent();
    }

    [HttpGet("me/notices")]
    public IActionResult Notices()
    {
        var caller = identity.Caller(User);
        var state = runtime.Read();
        if (state.NoticeDeliveryIncomplete) Response.Headers["X-DeletionQueue-Notice-Delivery"] = "incomplete";
        var accessible = state.Proposals.Where(p => targets.Accessible(caller.Id, p.Target.JellyfinItemId)).Select(p => p.Id).ToHashSet();
        return Ok(state.Notices.Where(n => n.UserId == caller.Id && accessible.Contains(n.ProposalId))
            .OrderBy(n => n.ReadAt.HasValue).Take(100).Select(n => new { n.Id, n.ProposalId, n.Text, IsRead = n.ReadAt.HasValue }).ToArray());
    }

    [HttpPost("me/notices/{id:guid}/read")]
    public IActionResult ReadNotice(Guid id, NoticeReadBody body)
    {
        var caller = identity.Caller(User);
        runtime.Write(body.CommandId, caller.Id.ToString(), "NoticeMarkedRead", state =>
        {
            _ = identity.Caller(User);
            var notice = state.Notices.SingleOrDefault(n => n.Id == id && n.UserId == caller.Id) ?? throw new QueueException("NotFound");
            _ = Visible(state, notice.ProposalId, caller.Id);
            NoticeRules.MarkRead(state, id, caller.Id, clock.GetUtcNow());
            return true;
        });
        return NoContent();
    }

    [HttpGet("admin/status")]
    public IActionResult AdminStatus()
    {
        _ = identity.Caller(User, administrator: true);
        if (!runtime.Available) return Ok(new { StorageAvailable = false, ExecutionAvailable = false, Mode = QueueRuntime.ReleaseMode });
        var state = runtime.Read();
        return Ok(new
        {
            StorageAvailable = true, ExecutionAvailable = false, Mode = QueueRuntime.ReleaseMode,
            state.Policy, state.CoverageSince, state.LastHeartbeat, state.CoverageHealthy, state.ClockHealthy,
            state.CoverageProblem, CollectorHealthy = runtime.CollectorHealthy, LocalIntegrityVerified = true, IndependentRollbackProtection = false,
            ContinuousWindowCollected = QueueRules.CoverageComplete(state, clock.GetUtcNow()),
            FullActivityScanVerified = false, PinnedDeletionContractAvailable = false,
            state.NoticeDeliveryIncomplete,
            Seerr = new { state.Seerr.BaseUrl, state.Seerr.Revision, KeyConfigured = state.Seerr.ApiKey.Length > 0, JellyfinServerId = host.SystemId },
            Accounts = users.GetUsers().Take(500).Select(u => new { u.Id, u.Username, EffectiveAuthority = QueueRules.Authority(state, u.Id), Account = identity.Account(u.Id) }).ToArray(),
            Proposals = state.Proposals.TakeLast(200).ToArray(), ActivityEvidence = state.Activity.TakeLast(500).ToArray(),
            MonitoringLimits = new { Accounts = 500, Proposals = 200, Activity = 500 },
            Journal = runtime.History(),
            HistoryAuthentication = "All events since first initialization are authenticated; no imported legacy history."
        });
    }

    [HttpPatch("admin/policy")]
    public IActionResult Policy(PolicyBody body)
    {
        var caller = identity.Caller(User, administrator: true);
        runtime.Write(body.CommandId, caller.Id.ToString(), "AdministratorPolicyChanged", state =>
        { _ = identity.Caller(User, administrator: true); QueueRules.UpdatePolicy(state, body.Policy, body.Revision); return true; });
        return NoContent();
    }

    [HttpPatch("admin/seerr")]
    public IActionResult Integration(IntegrationBody body)
    {
        var caller = identity.Caller(User, administrator: true);
        if (!Uri.TryCreate(body.BaseUrl, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https") ||
            url.UserInfo.Length > 0 || url.Query.Length > 0 || url.Fragment.Length > 0 || body.BaseUrl.Length > 2048 || body.ApiKey?.Length > 512)
            throw new QueueException("InvalidSeerrSettings");
        if (body.ApiKey?.Any(c => c < 33 || c > 126) == true) throw new QueueException("InvalidSeerrSettings");
        runtime.Write(body.CommandId, caller.Id.ToString(), "SeerrConfigurationChanged", state =>
        {
            _ = identity.Caller(User, administrator: true);
            if (body.Revision != state.Seerr.Revision) throw new QueueException("RevisionChanged");
            state.Seerr.BaseUrl = url.AbsoluteUri.TrimEnd('/');
            if (body.ApiKey is not null) state.Seerr.ApiKey = body.ApiKey;
            state.Seerr.JellyfinServerId = host.SystemId;
            state.Seerr.Revision++;
            QueueRules.BreakCoverage(state, "IntegrationChanged", clock.GetUtcNow());
            return true;
        });
        return NoContent();
    }

    private Proposal Visible(QueueState state, Guid id, Guid caller)
    {
        var p = QueueRules.Find(state, id);
        if (!targets.Accessible(caller, p.Target.JellyfinItemId)) throw new QueueException("NotFound");
        return p;
    }

    private IActionResult Resource(string name, string contentType)
    {
        var stream = typeof(Plugin).Assembly.GetManifestResourceStream(typeof(Plugin).Namespace + ".Web." + name);
        return stream is null ? NotFound() : File(stream, contentType);
    }
}
