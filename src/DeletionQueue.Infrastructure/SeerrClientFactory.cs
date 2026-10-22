using DeletionQueue.Core;

namespace DeletionQueue.Infrastructure;

public interface ISeerrClientFactory
{
    SeerrClient Create(SeerrSettings settings);
}

public sealed class SeerrClientFactory : ISeerrClientFactory
{
    public SeerrClient Create(SeerrSettings settings) => new(new Uri(settings.BaseUrl), settings.ApiKey);
}
