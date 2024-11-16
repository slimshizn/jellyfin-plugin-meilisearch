using Meilisearch;
using Microsoft.Extensions.Logging;
using Index = Meilisearch.Index;

namespace Jellyfin.Plugin.Meilisearch;

public abstract class Indexer(MeilisearchClientHolder clientHolder, ILogger<Indexer> logger)
{
    public abstract DateTimeOffset? LastIndex { get; protected set; }
    public abstract long LastIndexCount { get; protected set; }

    public async Task Index()
    {
        var task = clientHolder.Call(IndexInternal);
        if (task == null)
        {
            logger.LogWarning("Meilisearch is not configured, skipping index update");
            return;
        }

        await task;
    }

    protected abstract Task IndexInternal(MeilisearchClient meilisearchClient, Index index);
}