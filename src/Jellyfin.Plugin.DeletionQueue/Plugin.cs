using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Plugins;
using Microsoft.Extensions.DependencyInjection;
using DeletionQueue.Infrastructure;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Jellyfin.Plugin.DeletionQueue;

// No BasePlugin<T>: its catch-and-reset configuration loader is inappropriate for authorization state.
// All settings are revisioned inside the authenticated plugin-owned store instead.
public sealed class Plugin : BasePlugin, IHasWebPages
{
    public Plugin()
    {
        var assembly = GetType().Assembly;
        SetAttributes(assembly.Location, Path.GetDirectoryName(assembly.Location)!, assembly.GetName().Version!);
    }

    public override Guid Id => Guid.Parse(GetType().Assembly.GetCustomAttribute<GuidAttribute>()!.Value);
    public override string Name => GetType().Assembly.GetCustomAttribute<AssemblyTitleAttribute>()!.Title;
    public override string Description => GetType().Assembly.GetCustomAttribute<AssemblyDescriptionAttribute>()!.Description;

    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo
        {
            Name = "DeletionQueueAdmin", DisplayName = "Deletion queue", EnableInMainMenu = true,
            MenuIcon = "delete", EmbeddedResourcePath = GetType().Namespace + ".Web.admin.html"
        };
        yield return new PluginPageInfo
        { Name = "DeletionQueueAdmin.js", EmbeddedResourcePath = GetType().Namespace + ".Web.admin.js" };
    }
}

public sealed class ServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services, IServerApplicationHost applicationHost)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<QueueRuntime>();
        services.AddSingleton<LiveIdentity>();
        services.AddSingleton<TargetResolver>();
        services.AddSingleton<EnhancedConnection>();
        services.AddSingleton<ISeerrClientFactory, SeerrClientFactory>();
        services.AddSingleton<ActivityFence>();
        services.AddSingleton<ActivityInventory>();
        services.AddTransient<SeerrExecutionGateway>();
        services.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter, WebInjection>();
        services.AddHostedService<ActivityWorker>();
        services.AddHostedService<ExecutionWorker>();
    }
}
