using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Meilisearch;

[Route("meilisearch")]
[ApiController]
public class Controller(MeilisearchClientHolder clientHolder) : ControllerBase
{
    [HttpGet("status")]
    public Task<IActionResult> GetStatus()
    {
        return Task.FromResult<IActionResult>(new JsonResult(new
        {
            db = Plugin.Instance?.DbPath,
            meilisearch = clientHolder.Status,
            meilisearchOk = clientHolder.Ok,
            lastIndex = Plugin.Instance?.Indexer.LastIndex?.ToString() ?? "Not yet indexed",
            lastIndexed = Plugin.Instance?.Indexer.LastIndexCount,
            averageSearchTime = $"{Plugin.Instance?.AverageSearchTime}ms"
        }));
    }
    
    [HttpGet("reconnect")]
    public Task<IActionResult> Reconnect()
    {
        if (!clientHolder.Ok) Plugin.Instance?.TryCreateMeilisearchClient().Wait();
        return GetStatus();
    }
}