using System.Text;
using System.Text.Json;
using DeletionQueue.Core;
using Jellyfin.Plugin.DeletionQueue;
using Microsoft.AspNetCore.Http;

namespace DeletionQueue.Tests;

public sealed class WebIntegrationTests
{
    [Fact] public void AssemblyDoesNotRetainStandaloneUiResources()
    {
        var resources = typeof(Plugin).Assembly.GetManifestResourceNames();
        foreach (var retired in new[] { ".Web.user.html", ".Web.user.js", ".Web.theme.js", ".Web.queue.css" })
            Assert.DoesNotContain(resources, name => name.EndsWith(retired, StringComparison.Ordinal));
        Assert.Contains(resources, name => name.EndsWith(".Web.admin.css", StringComparison.Ordinal));
    }
    private sealed class EnhancedConfig
    {
        public bool JellyseerrEnabled { get; init; } = true;
        public string JellyseerrUrls { get; init; } = "https://seerr.invalid/base";
        public string JellyseerrApiKey { get; init; } = "synthetic-secret";
    }
    [Fact] public void EnhancedConnectionIsUnambiguousValidatedAndNeverGuessed()
    {
        var connection = EnhancedConnection.ReadConfiguration(new EnhancedConfig());
        Assert.NotNull(connection); Assert.Equal("https://seerr.invalid/base", connection.Value.Url);
        Assert.Equal("synthetic-secret", connection.Value.Key);
        Assert.Null(EnhancedConnection.ReadConfiguration(new EnhancedConfig { JellyseerrEnabled = false }));
        Assert.Null(EnhancedConnection.ReadConfiguration(new EnhancedConfig { JellyseerrUrls = "https://a.invalid\nhttps://b.invalid" }));
        Assert.Null(EnhancedConnection.ReadConfiguration(new EnhancedConfig { JellyseerrUrls = "file:///etc/passwd" }));
        Assert.Null(EnhancedConnection.ReadConfiguration(new EnhancedConfig { JellyseerrUrls = "https://user:password@seerr.invalid" }));
        Assert.Null(EnhancedConnection.ReadConfiguration(new EnhancedConfig { JellyseerrApiKey = "x\r\nInjected: yes" }));
        Assert.Null(EnhancedConnection.ReadConfiguration(new object()));
    }
    [Fact] public void AutomaticRecipientsIncludeAllRecentOrPlayingViewersButNotOtherAccounts()
    {
        var state = QueueRulesTests.State(); var p = QueueRulesTests.Propose(state); var now = QueueRulesTests.Now;
        var recent = Guid.NewGuid(); var old = Guid.NewGuid(); var playing = Guid.NewGuid(); var unrelated = Guid.NewGuid();
        state.Activity.AddRange([
            new Activity { UserId = recent, Key = p.Target.ActivityKey, At = now.AddDays(-14) },
            new Activity { UserId = old, Key = p.Target.ActivityKey, At = now.AddDays(-15) },
            new Activity { UserId = playing, Key = p.Target.ActivityKey, At = now.AddDays(-15), Sessions = ["session"] },
            new Activity { UserId = unrelated, Key = "other", At = now }
        ]);
        var before = JsonSerializer.Serialize(state.Activity); var consent = JsonSerializer.Serialize(p.Consents);
        NoticeRules.Publish(state, [p], now); NoticeRules.Publish(state, [p], now);
        Assert.Equal(new[] { p.OwnerId, recent, playing }.Order(), state.Notices.Select(n => n.UserId).Order());
        Assert.Equal(before, JsonSerializer.Serialize(state.Activity)); Assert.Equal(consent, JsonSerializer.Serialize(p.Consents));
        Assert.DoesNotContain(recent.ToString(), JsonSerializer.Serialize(state.Notices.Select(n => n.Text)));
    }

    [Fact] public void UserResetRetainsConsentHistoryAndAccumulatedDeadlineFloors()
    {
        var s = QueueRulesTests.State(); var p = QueueRulesTests.Propose(s); var user = p.OwnerId;
        QueueRules.UpdatePreferences(s, user, 0, 240, false);
        QueueRules.Subscription(s, p.Id, user, 1, true);
        s.Policy.Accounts[user] = ProposalAuthority.AnyAccessibleItem;
        p.EligibleSince = QueueRulesTests.Now; p.NotBefore = QueueRulesTests.Now.AddHours(240);
        var deadline = p.NotBefore; var consent = JsonSerializer.Serialize(p.Consents);
        QueueRules.ResetUser(s, user, s.Policy.Revision, 2);
        Assert.False(s.Policy.Accounts.ContainsKey(user)); Assert.Equal(240, p.DelayFloorHours);
        Assert.Equal(deadline, p.NotBefore); Assert.Equal(consent, JsonSerializer.Serialize(p.Consents));
        Assert.Equal(user, p.OwnerId); Assert.Single(s.Proposals);
        Assert.Equal(48, s.Preferences[user].DelayHours); Assert.True(s.Preferences[user].Notifications);
        Assert.Empty(s.Preferences[user].Subscriptions); Assert.Equal(3, s.Preferences[user].Revision);
    }

    [Fact] public void StaleUserResetCannotMutateAnySetting()
    {
        var s = QueueRulesTests.State(); var user = QueueRulesTests.Propose(s).OwnerId;
        var before = JsonSerializer.Serialize(s);
        Assert.Throws<QueueException>(() => QueueRules.ResetUser(s, user, 99, 0));
        Assert.Equal(before, JsonSerializer.Serialize(s));
        Assert.Throws<QueueException>(() => QueueRules.ResetUser(s, user, 0, 99));
        Assert.Equal(before, JsonSerializer.Serialize(s));
    }

    [Theory]
    [InlineData("/web")] [InlineData("/web/")] [InlineData("/web/index.html")]
    public async Task InjectorHandlesBasePathsWithoutChangingNativeOrEnhancedContent(string path)
    {
        var ctx = Context(path); ctx.Request.PathBase = "/jellyfin";
        ctx.Request.Headers.AcceptEncoding = "gzip"; ctx.Request.Headers.IfNoneMatch = "old";
        await WebInjection.InvokeAsync(ctx, async c =>
        {
            Assert.Equal(0, c.Request.Headers.AcceptEncoding.Count); Assert.Equal(0, c.Request.Headers.IfNoneMatch.Count);
            c.Response.Headers.ETag = "old";
            await c.Response.Body.WriteAsync(Encoding.UTF8.GetBytes("<html><body>Native<script src=\"enhanced.js\"></script></body></html>"));
        });
        var html = Text(ctx);
        Assert.Contains("Native<script src=\"enhanced.js\"></script>", html);
        Assert.Contains("/jellyfin/DeletionQueue/assets/integrated.js", html);
        Assert.Equal(1, html.Split("id=\"dq-web-client\"").Length - 1);
        Assert.Equal(0, ctx.Response.Headers.ETag.Count); Assert.Equal("no-store", ctx.Response.Headers.CacheControl);
        Assert.Equal("gzip", ctx.Request.Headers.AcceptEncoding); Assert.Equal("old", ctx.Request.Headers.IfNoneMatch);
        Assert.Equal(Encoding.UTF8.GetByteCount(html), ctx.Response.ContentLength);
    }

    [Theory]
    [InlineData("/web/main.js", "GET", 200, "text/html", "<body>native</body>")]
    [InlineData("/web/", "HEAD", 200, "text/html", "")]
    [InlineData("/web/", "GET", 500, "text/html", "<body>error</body>")]
    [InlineData("/web/", "GET", 200, "application/json", "{}")]
    [InlineData("/web/", "GET", 200, "text/html", "missing closing body")]
    [InlineData("/web/", "GET", 200, "text/html", "<body><script id=\"dq-web-client\"></script></body>")]
    public async Task IncompatibleOrAlreadyInjectedResponsesPassThrough(string path, string method, int status, string type, string original)
    {
        var ctx = Context(path); ctx.Request.Method = method;
        await WebInjection.InvokeAsync(ctx, async c => { c.Response.StatusCode = status; c.Response.ContentType = type; await c.Response.WriteAsync(original); });
        Assert.Equal(original, Text(ctx));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task OversizedAndStreamingResponsesPassThroughExactly(bool flush)
    {
        var ctx = Context("/web/"); var original = "<body>" + new string('x', 2 * 1024 * 1024 + 1) + "</body>";
        await WebInjection.InvokeAsync(ctx, async c =>
        {
            await c.Response.WriteAsync(original[..10]); if (flush) await c.Response.Body.FlushAsync();
            await c.Response.WriteAsync(original[10..]);
        });
        Assert.Equal(original, Text(ctx));
    }

    [Fact] public async Task HostErrorsPropagateAndOriginalStreamsAndRequestHeadersAreRestored()
    {
        var ctx = Context("/web/"); var body = ctx.Response.Body; ctx.Request.Headers.Range = "bytes=0-10";
        await Assert.ThrowsAsync<InvalidOperationException>(() => WebInjection.InvokeAsync(ctx, _ => throw new InvalidOperationException("Host failure")));
        Assert.Same(body, ctx.Response.Body); Assert.Equal("bytes=0-10", ctx.Request.Headers.Range);
    }

    private static DefaultHttpContext Context(string path)
    {
        var ctx = new DefaultHttpContext(); ctx.Request.Method = "GET"; ctx.Request.Path = path;
        ctx.Response.ContentType = "text/html; charset=utf-8"; ctx.Response.Body = new MemoryStream(); return ctx;
    }
    private static string Text(HttpContext ctx) => Encoding.UTF8.GetString(((MemoryStream)ctx.Response.Body).ToArray());
}
