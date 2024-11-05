using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Meilisearch;

public class PluginRegister : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<UpdateMeilisearchIndexTask>();
        serviceCollection.AddSingleton<MeilisearchClientHolder>();
        serviceCollection.AddSingleton<Indexer, DbIndexer>();
    }
}