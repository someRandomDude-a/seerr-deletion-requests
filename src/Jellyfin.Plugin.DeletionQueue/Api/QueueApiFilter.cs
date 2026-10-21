using System.Collections.Concurrent;
using DeletionQueue.Core;
using DeletionQueue.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Http;
using System.Text.Json;

namespace Jellyfin.Plugin.DeletionQueue.Api;

public sealed class QueueApiFilter : IAsyncResourceFilter, IAsyncActionFilter
{
    private static readonly ConcurrentDictionary<string, (long Minute, int Count)> Budgets = new();
    private static readonly SemaphoreSlim ConcurrentOperations = new(4, 4);

    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        var request = context.HttpContext.Request;
        context.HttpContext.Response.Headers.CacheControl = "private, no-store";
        context.HttpContext.Response.Headers.XContentTypeOptions = "nosniff";
        var entered = false;
        var invokedNext = false;
        async Task Fail(IActionResult result)
        {
            if (!invokedNext) context.Result = result;
            else if (!context.HttpContext.Response.HasStarted) await result.ExecuteResultAsync(context);
            else context.HttpContext.Abort(); // Never append an error to a partially serialized response.
        }
        try
        {
            if (!WithinBudget(context.HttpContext.User.FindFirst("Jellyfin-UserId")?.Value)) { context.Result = new StatusCodeResult(429); return; }
            entered = await ConcurrentOperations.WaitAsync(0, context.HttpContext.RequestAborted);
            if (!entered) { context.Result = new StatusCodeResult(429); return; }
            if (request.Method is not ("GET" or "HEAD"))
            {
                LiveIdentity.RequireSameOrigin(request);
                request.EnableBuffering(bufferThreshold: 32768, bufferLimit: 32769);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.HttpContext.RequestAborted);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                using var json = await JsonDocument.ParseAsync(request.Body, new JsonDocumentOptions { MaxDepth = 16 }, timeout.Token);
                if (!UniqueProperties(json.RootElement)) { context.Result = new BadRequestObjectResult(new { code = "AmbiguousJson" }); return; }
                request.Body.Position = 0;
            }
            invokedNext = true;
            var executed = await next();
            if (executed.Exception is not null && !executed.ExceptionHandled)
            { executed.ExceptionHandled = true; await Fail(Failure(executed.Exception)); }
        }
        catch (JsonException) { await Fail(new BadRequestObjectResult(new { code = "InvalidJson" })); }
        catch (BadHttpRequestException e) { await Fail(new StatusCodeResult(e.StatusCode)); }
        catch (IOException) { await Fail(new StatusCodeResult(413)); }
        catch (OperationCanceledException) { await Fail(new StatusCodeResult(408)); }
        catch (Exception e) { await Fail(Failure(e)); }
        finally { if (entered) ConcurrentOperations.Release(); }
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        try
        {
            var executed = await next();
            if (executed.Exception is not null && !executed.ExceptionHandled)
            { executed.Result = Failure(executed.Exception); executed.ExceptionHandled = true; }
        }
        catch (Exception e) { context.Result = Failure(e); }
    }

    private static bool UniqueProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject()) if (!names.Add(property.Name) || !UniqueProperties(property.Value)) return false;
        }
        if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) if (!UniqueProperties(item)) return false;
        return true;
    }

    private static bool WithinBudget(string? user)
    {
        if (user is null) return true;
        var minute = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60;
        if (Budgets.Count >= 4096)
        {
            foreach (var old in Budgets.Where(b => b.Value.Minute < minute - 1)) Budgets.TryRemove(old.Key, out _);
            if (!Budgets.ContainsKey(user) && Budgets.Count >= 4096) return false;
        }
        var budget = Budgets.AddOrUpdate(user, (minute, 1), (_, old) => old.Minute == minute ? (minute, old.Count + 1) : (minute, 1));
        return budget.Count <= 120;
    }

    private static ObjectResult Failure(Exception e)
    {
        var code = e switch
        {
            QueueException qe => qe.Code,
            IntegrityException => "IntegrityUnavailable",
            HttpRequestException or TaskCanceledException => "UpstreamUnavailable",
            _ => "OperationUnavailable"
        };
        var status = code switch
        {
            "NotFound" or "TargetUnavailable" => 404,
            "UserSessionRequired" => 401,
            "AccessDenied" or "OwnerOnly" or "ProposalNotPermitted" or "OriginRejected" => 403,
            "IntegrityUnavailable" or "UpstreamUnavailable" or "OperationUnavailable" => 503,
            _ => 409
        };
        return new ObjectResult(new { code }) { StatusCode = status };
    }
}
