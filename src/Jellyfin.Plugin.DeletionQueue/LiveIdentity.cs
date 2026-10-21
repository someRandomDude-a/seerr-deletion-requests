using System.Security.Claims;
using DeletionQueue.Core;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.DeletionQueue;

public sealed class LiveIdentity(IUserManager users)
{
    public Account Caller(ClaimsPrincipal principal, bool administrator = false)
    {
        // Jellyfin's authenticated claims, NOT headers, query IDs or a supplied administrator identity.
        if (principal.Identity?.IsAuthenticated != true ||
            !bool.TryParse(principal.FindFirstValue("Jellyfin-IsApiKey"), out var apiKey) || apiKey ||
            !Guid.TryParse(principal.FindFirstValue("Jellyfin-UserId"), out var id) || id == Guid.Empty)
            throw new QueueException("UserSessionRequired");
        var account = Account(id);
        if (!account.Enabled || (administrator && !account.Administrator)) throw new QueueException("AccessDenied");
        return account;
    }

    public Account Account(Guid id)
    {
        var user = users.GetUserById(id);
        if (user is null) return new Account(id, false);
        var dto = users.GetUserDto(user);
        return new Account(id, dto.Policy?.IsDisabled == false, dto.Policy?.IsAdministrator == true);
    }

    public static void RequireSameOrigin(HttpRequest request)
    {
        if (!request.Headers.TryGetValue("Origin", out var origins)) return; // Header-authenticated SDK/CLI clients.
        if (origins.Count != 1 || !Uri.TryCreate(origins[0], UriKind.Absolute, out var origin) ||
            !string.Equals(origin.Authority, request.Host.Value, StringComparison.OrdinalIgnoreCase) || origin.Scheme != request.Scheme)
            throw new QueueException("OriginRejected");
    }
}
