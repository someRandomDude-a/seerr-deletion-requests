using System.Net;
using System.Reflection;
using System.Text.Json;
using DeletionQueue.Core;
using DeletionQueue.Infrastructure;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.DeletionQueue;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Users;
using Microsoft.Extensions.Logging.Abstractions;

namespace DeletionQueue.Tests;

public sealed class ProductionGatewayTests : IDisposable
{
    public class Proxy : DispatchProxy
    {
        public Func<MethodInfo, object?[], object?> Call = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Call(method!, args ?? []);
    }
    private static T Fake<T>(Func<MethodInfo, object?[], object?> call) where T : class
    { var fake = DispatchProxy.Create<T, Proxy>(); ((Proxy)(object)fake).Call = call; return fake; }
    private sealed class Clock : TimeProvider { public DateTimeOffset Now = QueueRulesTests.Now; public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage message, CancellationToken ct) => Task.FromResult(send(message)); }
    private sealed class Factory(Func<HttpRequestMessage, HttpResponseMessage> send) : ISeerrClientFactory
    { public SeerrClient Create(SeerrSettings settings) => new(new Uri(settings.BaseUrl), settings.ApiKey, new Handler(send)); }

    private readonly string directory = Path.Combine(Path.GetTempPath(), "dq-production-gateway-" + Guid.NewGuid().ToString("N"));
    private readonly Clock clock = new();
    private readonly User owner = new("owner", "auth", "reset") { Id = Guid.NewGuid() };
    private readonly User restricted = new("restricted", "auth", "reset") { Id = Guid.NewGuid() };
    private readonly Movie movie = new() { Id = Guid.NewGuid(), Name = "Fixture movie", ProviderIds = new() { ["Tmdb"] = "1" } };
    private readonly QueueRuntime runtime;
    private readonly TargetResolver targets;
    private readonly SeerrExecutionGateway gateway;
    private readonly ActivityFence fence = new();
    private DateTime? historical;
    private bool timeout;
    private bool deleted;
    private bool administrator;
    private int deletes;

    public ProductionGatewayTests()
    {
        var paths = Fake<IApplicationPaths>((m, _) => m.Name == "get_DataPath" ? directory : throw new NotSupportedException(m.Name));
        runtime = new QueueRuntime(paths, NullLogger<QueueRuntime>.Instance, clock);
        Assert.False(runtime.CollectorHealthy); // A persisted healthy heartbeat cannot bypass restart warm-up.
        runtime.MarkCollectorStarted();
        var users = Fake<IUserManager>((m, args) => m.Name switch
        {
            "GetUsers" => new[] { owner, restricted },
            "GetUserById" => (Guid)args[0]! == owner.Id ? owner : restricted,
            "GetUserDto" => new UserDto { Id = ((User)args[0]!).Id, Policy = new UserPolicy { IsDisabled = ((User)args[0]!).Id == restricted.Id,
                IsAdministrator = ((User)args[0]!).Id == owner.Id && administrator } },
            _ => throw new NotSupportedException(m.Name)
        });
        var library = Fake<ILibraryManager>((m, _) => m.Name switch
        { "GetItemById" => movie, "GetItemList" => new BaseItem[] { movie }, _ => throw new NotSupportedException(m.Name) });
        var data = Fake<IUserDataManager>((m, args) =>
        {
            if (m.Name != "GetUserData") throw new NotSupportedException(m.Name);
            var value = Activator.CreateInstance(m.ReturnType)!;
            if (((User)args[0]!).Id == restricted.Id) m.ReturnType.GetProperty("LastPlayedDate")!.SetValue(value, historical);
            return value;
        });
        var sessions = Fake<ISessionManager>((m, _) => m.Name == "get_Sessions" ? Array.Empty<SessionInfo>() : throw new NotSupportedException(m.Name));
        var host = Fake<IServerApplicationHost>((m, _) => m.Name == "get_SystemId" ? "fixture-server" : throw new NotSupportedException(m.Name));
        var clients = new Factory(Send);
        targets = new TargetResolver(library, users, runtime, host, clients);
        var identity = new LiveIdentity(users);
        var inventory = new ActivityInventory(library, users, data, sessions, runtime, targets, clock);
        gateway = new SeerrExecutionGateway(runtime, targets, identity, inventory, fence, clock, clients);
        runtime.Write(Guid.NewGuid(), "fixture", "Configure", state =>
        {
            state.Seerr = new() { BaseUrl = "https://seerr.invalid", ApiKey = "synthetic-key", JellyfinServerId = "fixture-server" };
            state.Policy.AutomaticDeletion = true; state.Policy.SeerrManagedDeletionAcknowledged = true;
            state.CoverageHealthy = true; state.CoverageSince = clock.Now.AddDays(-30); state.LastHeartbeat = clock.Now;
            return true;
        });
    }
    private HttpResponseMessage Send(HttpRequestMessage request)
    {
        if (request.Method == HttpMethod.Delete)
        {
            Assert.Equal("/api/v1/media/42/file?is4k=false", request.RequestUri!.PathAndQuery);
            var claimed = Assert.Single(runtime.Read().Proposals);
            Assert.Equal(ProposalStatus.Claimed, claimed.Status); Assert.NotNull(claimed.Claim);
            Assert.Contains(runtime.History(), e => e.Kind == "ExecutionClaimed");
            deletes++; deleted = true;
            if (timeout) throw new TaskCanceledException("Uncertain synthetic response");
            return new(HttpStatusCode.NoContent);
        }
        Assert.Equal(HttpMethod.Get, request.Method);
        var json = request.RequestUri!.AbsolutePath switch
        {
            "/api/v1/settings/jellyfin" => """{"serverId":"fixture-server"}""",
            "/api/v1/user/1" => $$$"""{"jellyfinUserId":"{{{owner.Id}}}"}""",
            "/api/v1/movie/1" => $$$"""{"id":1,"mediaInfo":{"id":42,"tmdbId":1,"mediaType":"movie","serviceId":0,"externalServiceId":123,"jellyfinMediaId":"{{{movie.Id}}}","requests":[{"is4k":false,"status":2,"requestedBy":{"id":1}}]}}""",
            "/api/v1/media/42" => $$$"""{"id":42,"tmdbId":1,"status":{{{(deleted ? 7 : 5)}}}}""",
            _ => throw new NotSupportedException(request.RequestUri.AbsolutePath)
        };
        return new(HttpStatusCode.OK) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
    }
    private async Task<Guid> Seed()
    {
        var target = await targets.ResolveAsync(new Account(owner.Id, true), new TargetSelection(movie.Id, null, false), default);
        return runtime.Write(Guid.NewGuid(), "fixture", "Propose", state =>
        {
            var p = QueueRules.Propose(state, new Account(owner.Id, true), target, true, QueueRules.Snapshot(state, target, owner.Id), clock.Now);
            QueueRules.Evaluate(state, p, QueueRulesTests.Good, clock.Now); return p.Id;
        });
    }
    private void Due()
    {
        clock.Now = clock.Now.AddHours(48);
        runtime.Write(Guid.NewGuid(), "fixture", "Heartbeat", s => { s.LastHeartbeat = clock.Now; return true; });
    }
    [Fact] public async Task RealGatewayPersistsClaimAndSeparateOutcomeBeforeCompletion()
    {
        var id = await Seed(); Due();
        await new ExecutionCoordinator(runtime, gateway, clock).ExecuteAsync(id, default);
        Assert.Equal(1, deletes); Assert.Equal(ProposalStatus.Completed, QueueRules.Find(runtime.Read(), id).Status);
        Assert.NotNull(QueueRules.Find(runtime.Read(), id).FilesDeletedAt);
    }
    [Fact] public async Task RealGatewayDoesNotResendAfterTimeoutAndReconcilesOnlyByReading()
    {
        var id = await Seed(); Due(); timeout = true;
        var coordinator = new ExecutionCoordinator(runtime, gateway, clock);
        await coordinator.ExecuteAsync(id, default);
        Assert.Equal(ProposalStatus.Uncertain, QueueRules.Find(runtime.Read(), id).Status);
        await Assert.ThrowsAsync<QueueException>(() => coordinator.ExecuteAsync(id, default));
        await coordinator.ReconcileAsync(id, default);
        Assert.Equal(1, deletes); Assert.Equal(ProposalStatus.FilesDeleted, QueueRules.Find(runtime.Read(), id).Status);
    }
    [Fact] public async Task RestrictedViewerHistoricalActivityBlocksRealGatewayWithoutRecordingConsent()
    {
        var id = await Seed(); Due(); historical = clock.Now.UtcDateTime.AddHours(-1);
        await Assert.ThrowsAsync<QueueException>(() => new ExecutionCoordinator(runtime, gateway, clock).ExecuteAsync(id, default));
        var state = runtime.Read(); Assert.Contains(state.Activity, a => a.UserId == restricted.Id);
        Assert.False(QueueRules.Find(state, id).Consents.ContainsKey(restricted.Id)); Assert.Equal(0, deletes);
    }
    [Fact] public async Task PendingNativeActivityBlocksDispatch()
    {
        var id = await Seed(); Due(); fence.Capturing();
        await Assert.ThrowsAsync<QueueException>(() => new ExecutionCoordinator(runtime, gateway, clock).ExecuteAsync(id, default));
        Assert.Equal(0, deletes);
    }
    [Theory] [InlineData(true)] [InlineData(false)]
    public async Task OptionalApprovalUsesTheApproversLiveRole(bool stillAdministrator)
    {
        runtime.Write(Guid.NewGuid(), "fixture", "RequireApproval", s => { s.Policy.RequireAdministratorApproval = true; return true; });
        var id = await Seed(); administrator = true;
        runtime.Write(Guid.NewGuid(), "fixture", "Approve", s =>
        {
            var p = QueueRules.Find(s, id);
            QueueRules.AdministratorDecision(s, id, new(owner.Id, true, true), p.Revision, QueueRules.AdministratorSnapshot(s, p), true, true, clock.Now);
            QueueRules.Evaluate(s, p, QueueRulesTests.Good with { AdministratorAuthorized = true }, clock.Now); return true;
        });
        Due(); administrator = stillAdministrator;
        var coordinator = new ExecutionCoordinator(runtime, gateway, clock);
        if (stillAdministrator) { await coordinator.ExecuteAsync(id, default); Assert.Equal(1, deletes); }
        else { await Assert.ThrowsAsync<QueueException>(() => coordinator.ExecuteAsync(id, default)); Assert.Equal(0, deletes); }
    }
    public void Dispose() { runtime.Dispose(); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
