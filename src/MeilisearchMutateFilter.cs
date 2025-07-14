﻿using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using Meilisearch;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Microsoft.IdentityModel.Tokens;
using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using Index = Meilisearch.Index;

namespace Jellyfin.Plugin.Meilisearch;

// ReSharper disable once ClassNeverInstantiated.Global
public class MeilisearchMutateFilter(
    MeilisearchClientHolder ch,
    ILogger<MeilisearchMutateFilter> logger,
    ILibraryManager libraryManager,
    IUserManager userManager)
    : IAsyncActionFilter
{
    // Build the Jellyfin type map dynamically
    private IReadOnlyDictionary<string, string> JellyfinTypeMap { get; } = new Dictionary<string, string>()
    {
        { "AggregateFolder", typeof(AggregateFolder).FullName! },
        { "Audio", typeof(Audio).FullName! },
        { "AudioBook", typeof(AudioBook).FullName! },
        { "BasePluginFolder", typeof(BasePluginFolder).FullName! },
        { "Book", typeof(Book).FullName! },
        { "BoxSet", typeof(BoxSet).FullName! },
        { "Channel", typeof(Channel).FullName! },
        { "CollectionFolder", typeof(CollectionFolder).FullName! },
        { "Episode", typeof(Episode).FullName! },
        { "Folder", typeof(Folder).FullName! },
        { "Genre", typeof(Genre).FullName! },
        { "Movie", typeof(Movie).FullName! },
        { "LiveTvChannel", typeof(LiveTvChannel).FullName! },
        { "LiveTvProgram", typeof(LiveTvProgram).FullName! },
        { "MusicAlbum", typeof(MusicAlbum).FullName! },
        { "MusicArtist", typeof(MusicArtist).FullName! },
        { "MusicGenre", typeof(MusicGenre).FullName! },
        { "MusicVideo", typeof(MusicVideo).FullName! },
        { "Person", typeof(Person).FullName! },
        { "Photo", typeof(Photo).FullName! },
        { "PhotoAlbum", typeof(PhotoAlbum).FullName! },
        { "Playlist", typeof(Playlist).FullName! },
        { "PlaylistsFolder", "Emby.Server.Implementations.Playlists.PlaylistsFolder" },
        { "Season", typeof(Season).FullName! },
        { "Series", typeof(Series).FullName! },
        { "Studio", typeof(Studio).FullName! },
        { "Trailer", typeof(Trailer).FullName! },
        { "TvChannel", typeof(LiveTvChannel).FullName! },
        { "TvProgram", typeof(LiveTvProgram).FullName! },
        { "UserRootFolder", typeof(UserRootFolder).FullName! },
        { "UserView", typeof(UserView).FullName! },
        { "Video", typeof(Video).FullName! },
        { "Year", typeof(Year).FullName! }
    }.ToFrozenDictionary();

    private static readonly Collection<string> MatchingPaths = ["/Items"];
    // ["/Users/{userId}/Items", "/Persons", "/Artists/AlbumArtists", "/Artists", "/Genres"];

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var path = context.HttpContext.Request.Path.ToString();
        logger.LogDebug("path={path} query={query}", path, context.HttpContext.Request.QueryString);

        var searchTerm = GetSearchTerm(context);
        if (!string.IsNullOrEmpty(searchTerm))
        {
            logger.LogDebug("path={path} searchTerm={searchTerm}", path, searchTerm);
            var stopwatch = Stopwatch.StartNew();
            var result = await Mutate(context, searchTerm);
            stopwatch.Stop();
            Plugin.Instance?.UpdateAverageSearchTime(stopwatch.ElapsedMilliseconds);
            context.HttpContext.Response.Headers.Add(new KeyValuePair<string, StringValues>(
                "x-meilisearch-result",
                $"{stopwatch.ElapsedMilliseconds}ms, {result.Count} items, bypass={result.ShouldBypass}"));

            if (result is { ShouldBypass: true, Count: 0 })
            {
                context.Result = EmptyResult;
                return;
            }
        }

        await next();
    }

    private string? GetSearchTerm(ActionExecutingContext context)
    {
        var path = context.HttpContext.Request.Path.ToString();
        logger.LogDebug("path={path} query={query}", path, context.HttpContext.Request.QueryString);

        if (!MatchingPaths.Contains(context.HttpContext.Request.Path)) return null;
        if (!context.ActionArguments.TryGetValue("searchTerm", out var searchTermObj)) return null;
        var searchTerm = (string?)searchTermObj;
        return searchTerm is not { Length: > 0 } ? null : searchTerm;
    }

    private async Task<IReadOnlyCollection<MeilisearchItem>> Search(Index index, string searchTerm,
        IEnumerable<KeyValuePair<string, string>> filters, List<KeyValuePair<string, string>> additionalFilters,
        int limit = 20)
    {
        List<MeilisearchItem> items = [];
        try
        {
            var additionQuery = additionalFilters.Select(it => $"{it.Key} = {it.Value}").ToList();
            foreach (var query in filters.Select(it => (List<string>)[$"{it.Key} = {it.Value}"]))
            {
                var results = await index.SearchAsync<MeilisearchItem>(
                    searchTerm,
                    new SearchQuery
                    {
                        Filter = string.Join(" AND ", query.Concat(additionQuery)),
                        Limit = limit,
                        AttributesToSearchOn = Plugin.Instance?.Configuration.AttributesToSearchOn
                    }
                );
                items.AddRange(results.Hits);
            }
        }
        catch (MeilisearchCommunicationError e)
        {
            logger.LogError(e, "Meilisearch communication error");
            ch.Unset();
        }

        return items;
    }


    /// <summary>
    ///     Mutates the current search request context by overriding the ids with the results of the Meilisearch query.
    ///     This part code is somewhat copied or adapted from Jellysearch.
    /// </summary>
    /// <param name="context">The action context.</param>
    /// <param name="searchTerm">The search term.</param>
    /// <remarks>
    ///     If the search term is empty, or if there are no results, the method does nothing.
    /// </remarks>
    /// <returns>A task representing the asynchronous operation.</returns>
    private async Task<MutateResult> Mutate(ActionExecutingContext context, string searchTerm)
    {
        if (!ch.Ok || ch.Index == null)
        {
            logger.LogWarning(
                "Meilisearch is not configured or unable to connect, skipping search mutation, will fallback to Jellyfin");
            Plugin.Instance?.TryCreateMeilisearchClient(false);
            return new MutateResult(false, 0);
        }

        var filteredTypes = new List<string>();
        var additionalFilters = new List<KeyValuePair<string, string>>();

        // includeItemTypes add types from the search
        var includeItemTypes = ParseQueryCommaOrMulti(context, "includeItemTypes");
        logger.LogDebug("includeItemTypes={includeItemTypes}", string.Join(", ", includeItemTypes));
        foreach (var x in includeItemTypes)
        {
            if (JellyfinTypeMap.TryGetValue(x, out var type))
            {
                filteredTypes.Add(type);
            }
            else
            {
                logger.LogWarning("includeItemTypes: no mapping for '{mediaType}'", x);
            }
        }

        // excludeItemTypes remove types from the search
        var excludeItemTypes = ParseQueryCommaOrMulti(context, "excludeItemTypes");
        logger.LogDebug("excludeItemTypes={excludeItemTypes}", string.Join(", ", excludeItemTypes));
        foreach (var x in excludeItemTypes)
        {
            if (JellyfinTypeMap.TryGetValue(x, out var excludeItemType))
            {
                filteredTypes.Remove(excludeItemType);
            }
            else
            {
                logger.LogWarning("excludeItemTypes: no mapping for '{mediaType}'", x);
            }
        }

        // mediaTypes add types from the search
        var mediaTypes = ParseQueryCommaOrMulti(context, "mediaTypes");
        logger.LogDebug("mediaTypes={mediaTypes}", string.Join(", ", mediaTypes));
        if (!mediaTypes.IsNullOrEmpty())
        {
            // If mediaTypes is set, we only search for those types
            foreach (var x in mediaTypes)
            {
                if (JellyfinTypeMap.TryGetValue(x, out var mappedType))
                {
                    filteredTypes.Add(mappedType);
                }
                else
                {
                    logger.LogWarning("mediaTypes: no mapping for '{mediaType}'", x);
                }
            }
        }
        else
        {
            var path = context.HttpContext.Request.Path.ToString();
            // Handle direct endpoints and their types
            if (path.EndsWith("/Persons", true, CultureInfo.InvariantCulture))
            {
                filteredTypes.Add(JellyfinTypeMap["Person"]);
            }
            else if (path.EndsWith("/Artists", true, CultureInfo.InvariantCulture))
            {
                filteredTypes.Add(JellyfinTypeMap["MusicArtist"]);
            }
            else if (path.EndsWith("/AlbumArtists", true, CultureInfo.InvariantCulture))
            {
                // Album artists are marked as folder
                filteredTypes.Add(JellyfinTypeMap["MusicArtist"]);
                additionalFilters.Add(new KeyValuePair<string, string>("isFolder", "true"));
            }
            else if (path.EndsWith("/Genres", true, CultureInfo.InvariantCulture))
            {
                filteredTypes.Add(JellyfinTypeMap["Genre"]); // TODO: Handle genre search properly
            }
        }

        // get user Id
        if (!context.ActionArguments.TryGetValue("userId", out var userIdObj))
            userIdObj = null;

        // Use the Authorization header if there is no userId
        // The userId query parameter is more important
        userIdObj ??= context.HttpContext.User.Claims.FirstOrDefault(claim => claim.Type.Equals("Jellyfin-UserId", StringComparison.OrdinalIgnoreCase))?.Value;

        var user = userIdObj switch
        {
            string strUserId => userManager.GetUserById(Guid.Parse(strUserId)),
            Guid guidUserId => userManager.GetUserById(guidUserId),
            _ => null
        };

        // Override the limit if it is less than 20 from request
        if (context.ActionArguments.TryGetValue("limit", out var limitObj))
            limitObj = null;
        var limit = (int?)limitObj ?? 20;
        var filter = filteredTypes
            .Select(it => new KeyValuePair<string, string>("type", it)).ToList();
        var items = await Search(ch.Index, searchTerm, filter, additionalFilters, limit);

        // remove items that are not visible to the user
        if (user != null && Plugin.Instance?.Configuration.DisablePermissionChecks != true)
        {
            items = items.Where(x =>
            {
                var item = libraryManager.GetItemById(Guid.Parse(x.Guid));
                return item?.IsVisibleStandalone(user) ?? false;
            }).ToImmutableList();
        }

        var notFallback = !(Plugin.Instance?.Configuration.FallbackToJellyfin ?? false);
        if (items.Count > 0 || notFallback)
        {
            logger.LogDebug("Mutating search request with {hits} results", items.Count);
            // Get all query arguments to pass along to Jellyfin
            // Remove searchterm since we already searched
            // Remove sortby and sortorder since we want to display results as Meilisearch returns them
            // Remove limit since we are requesting by specific IDs and don't want Jellyfin to remove some of them
            // Remove isMissing since we serve all items, not just missing ones
            context.ActionArguments["searchTerm"] = null;
            context.ActionArguments["isMissing"] = null;
            context.ActionArguments["sortBy"] = (ItemSortBy[]) [];
            context.ActionArguments["sortOrder"] = (SortOrder[]) [];
            context.ActionArguments["ids"] = items.Select(x => Guid.Parse(x.Guid)).ToArray();
            if (items.Count == 0)
                context.ActionArguments["limit"] = 0;
            else if (filter.Count == 1)
                context.ActionArguments["limit"] = limit < 20 ? 20 : limit;
        }
        else
        {
            logger.LogDebug("Not mutate request: results={hits}, fallback={fallback}", items.Count, !notFallback);
        }

        return new MutateResult(notFallback, items.Count);
    }

    private record MutateResult(bool ShouldBypass, int Count);

    private static readonly OkObjectResult EmptyResult = new(new QueryResult<BaseItemDto>
    {
        Items = new List<BaseItemDto>(),
        TotalRecordCount = 0,
        StartIndex = 0
    });


    /// <summary>
    /// Parse a query parameter that may contain comma delimited values or multiple values.
    /// </summary>
    /// <param name="context">The HttpContext extension.</param>
    /// <param name="key">The query parameter name.</param>
    /// <returns>The list of values.</returns>
    private static ImmutableList<string> ParseQueryCommaOrMulti(ActionExecutingContext context, string key)
    {
        if (!context.HttpContext.Request.Query.TryGetValue(key, out var values) || StringValues.IsNullOrEmpty(values))
            return ImmutableList<string>.Empty; // no values
        var types = values.SelectMany(it =>
            it?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? []);
        return types.ToImmutableList();
    }
}