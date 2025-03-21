using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Meilisearch;

[Route("meilisearch")]
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
public class Controller(MeilisearchClientHolder clientHolder) : ControllerBase
{
    [HttpGet("status")]
    public ActionResult GetStatus()
    {
        return new JsonResult(new
        {
            db = Plugin.Instance?.DbPath,
            meilisearch = clientHolder.Status,
            meilisearchOk = clientHolder.Ok,
            lastIndex = Plugin.Instance!.Indexer.LastIndex?.ToString() ?? "Not yet indexed",
            lastIndexed = Plugin.Instance.Indexer.LastIndexCount,
            averageSearchTime = $"{Plugin.Instance.AverageSearchTime}ms"
        });
    }
    
    [HttpGet("reconnect")]
    public async Task<ActionResult> Reconnect()
    {
        if (!clientHolder.Ok) await Plugin.Instance!.TryCreateMeilisearchClient();
        return GetStatus();
    }
}
