using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Meilisearch;

public class PluginRegister : IPluginServiceRegistrator
{
    public static string ServerName { get; private set; } = "Meilisearch";

    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<UpdateMeilisearchIndexTask>();
        serviceCollection.AddSingleton<MeilisearchClientHolder>();
        serviceCollection.AddSingleton<Indexer, DbIndexer>();
        ServerName = applicationHost.Name;
    }
}