# Jellyfin 12.x native plugin pages: verification

Verified against official **Jellyfin Server 12.2 and Jellyfin Web 12.2** source and an isolated Docker/browser fixture on 2026-10-21. Earlier/later 12.x releases still need compatibility tests.

## Result

**Yes: native plugin pages are available.** They are the selected approach for the elevated deletion-queue administrator workspace, with no persistent Jellyfin Web modifications.

**Important distinction:** the stock native configuration-page host is administrator-only. Ordinary-user queue pages need a separately verified entry point/host; the existence of native admin pages is not sufficient proof of that access.

**Integrated Web update (2026-10-22):** ordinary controls now use an original request-time ASP.NET `IStartupFilter` injector, not a configuration page or persistent Web-file rewrite. This is the same server extension point demonstrated by [Enhanced's pinned injection service](https://github.com/n00bcodr/Jellyfin-Enhanced/blob/e46b1129d68bd56505dcbfbb31f5478ffa6ef7c9/Jellyfin.Plugin.JellyfinEnhanced/Services/ScriptInjectionStartupFilter.cs); that source explicitly describes Jellyfin 12's lack of a native Web script-injection hook. Enhanced implementation code was not copied. Our bell is outside its overflow tray; available Requests cards are augmented through native media-ID DOM attributes. Server-side connection import reads only the installed enabled plugin's public configuration. The standalone route/redirect and resources have been removed; private-page verification notes below are historical. See [current status and test boundaries](implementation-status.md).

## Official source evidence

| Evidence | What it establishes |
| --- | --- |
| [`IHasWebPages.cs`](https://github.com/jellyfin/jellyfin/blob/v12.2/MediaBrowser.Model/Plugins/IHasWebPages.cs) | Plugins register embedded pages through `GetPages()`. |
| [`PluginPageInfo.cs`, lines 11–36](https://github.com/jellyfin/jellyfin/blob/v12.2/MediaBrowser.Model/Plugins/PluginPageInfo.cs#L11-L36) | Page name, embedded resource, display name, menu visibility, section and icon metadata are available. |
| [`DashboardController.cs`, lines 49–63](https://github.com/jellyfin/jellyfin/blob/v12.2/Jellyfin.Api/Controllers/DashboardController.cs#L49-L63) | Page discovery supports `enableInMainMenu`, but requires `RequiresElevation`. |
| [`DashboardController.cs`, lines 73–94](https://github.com/jellyfin/jellyfin/blob/v12.2/Jellyfin.Api/Controllers/DashboardController.cs#L73-L94) | `GET /web/ConfigurationPage?name=...` serves an assembly-embedded resource. This action has no action-level authorization attribute; never embed sensitive data or keys in the resource. |
| [Web `routes.tsx`, lines 18–54](https://github.com/jellyfin/jellyfin-web/blob/v12.2/src/apps/dashboard/routes/routes.tsx#L18-L54) | `configurationpage` is hosted inside the dashboard route tree under `ConnectionRequired level='admin'`. |
| [Web `ConnectionRequired.tsx`, lines 164–179](https://github.com/jellyfin/jellyfin-web/blob/v12.2/src/components/ConnectionRequired.tsx#L164-L179) | The native Web route checks the current user's administrator policy. |
| [Web `ServerContentPage.tsx`, lines 20–44](https://github.com/jellyfin/jellyfin-web/blob/v12.2/src/components/ServerContentPage.tsx#L20-L44) | Jellyfin loads the server-provided HTML/JS into its native view manager. |
| [Web `PluginDrawerSection.tsx`](https://github.com/jellyfin/jellyfin-web/blob/v12.2/src/apps/dashboard/components/drawer/sections/PluginDrawerSection.tsx) | The dashboard discovers enabled plugin menu entries and uses each page's display name and menu icon. |

`EnableInMainMenu` is presentation metadata, not a permission granting ordinary-user access. Likewise, a publicly retrievable static HTML resource does not grant access to protected plugin API data.

## Planned registration

Illustrative registration using the verified API:

```csharp
public IEnumerable<PluginPageInfo> GetPages()
{
    yield return new PluginPageInfo
    {
        Name = "DeletionQueueAdmin",
        DisplayName = "Deletion queue",
        EmbeddedResourcePath =
            "Jellyfin.Plugin.DeletionQueue.Web.admin.html",
        EnableInMainMenu = true,
        MenuIcon = "delete"
    };
}
```

The class implements `IHasWebPages`; resources are embedded in the plugin assembly. Jellyfin hosts the page at `#/configurationpage?name=DeletionQueueAdmin`. Use native page lifecycle/theme conventions, and fetch all private content through separately protected plugin API routes.

The plugin must independently enforce live administrator authorization on those APIs. A client-side route guard alone does not satisfy the requirement, and an API key must not qualify as an elevated user session.

## Runtime verification

The implemented plugin was loaded into the official Jellyfin 12.2 container without any media mounts. Automated API smoke tests passed for native page registration/resource loading, ordinary-user page-discovery denial, user-session-only APIs, service-key rejection and immediate rejection of a demoted administrator's existing session.

A browser was signed in through the unmodified Jellyfin Web login and navigated to `#/configurationpage?name=DeletionQueueAdmin`. The native dashboard drawer, plugin menu item, connection/policy controls, populated evidence and authenticated journal rendered correctly. The same browser opened `/DeletionQueue/ui`; the existing Web session was reused, the saved personal delay loaded and no authentication error appeared.

The repeatable Chromium check now also exercises per-account override saves and native-themed opt-in confirmation/cancellation. Two real compatibility details are covered: Web 12.2's custom-element polyfill requires `document.createElement('select', 'emby-select')` rather than the v1 options object, and policy dictionary keys must be dashed GUIDs even though Jellyfin renders ordinary Guid values without hyphens. The private page loads native lazy control chunks and the selected `themes/<id>/theme.css`; a `data-theme` attribute alone is not sufficient outside the native Web application.

Jellyfin 12's legacy `X-Emby-Token` header is disabled by default. The private page therefore uses the modern `Authorization: MediaBrowser Token="..."` header, never a query-string token or administrator ID.

The fixture used networking-disabled containers plus a loopback-only test proxy because Docker's default/shared network setup stalled in this environment. The proxy does not support WebSockets; universal native clients and other 12.x builds remain uncertified.

### Repeatable checklist

Use an isolated Docker Jellyfin instance, not production configuration:

1. Install a minimal plugin with an embedded admin page, start Jellyfin and authenticate as an administrator.
2. Verify native drawer registration, icon, page rendering, resource loading and theme/back-navigation behavior.
3. Log in as a genuinely non-admin user and check both page discovery and the `#/configurationpage` route. Do not infer ordinary-user support from an admin session.
4. Verify that direct static-resource retrieval exposes no private state, and direct admin API calls reject unauthenticated, non-admin and API-key-only callers.
5. Demote/disable the administrator during an open session; API reads and mutations must stop immediately after live policy reevaluation.
6. Test the chosen ordinary-user integration separately. If it needs a runtime extension or upstream/client support, document that explicitly; do not remove Jellyfin's admin guard to make the test pass.
7. Verify Jellyfin Web files are unchanged before/after install, navigation and uninstall.

The implementation smoke checks run only against a fresh disposable server and refuse an already-configured instance. No destructive request or real-media test was performed.
