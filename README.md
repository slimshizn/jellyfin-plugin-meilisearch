# Meilisearch Plugin for Jellyfin

This plugin is inspired by [Jellysearch](https://gitlab.com/DomiStyle/jellysearch).

---

### Usage

1. add following repository and install the Meilisearch plugin
    ```
    https://raw.githubusercontent.com/arnesacnussem/jellyfin-plugin-meilisearch/refs/heads/master/manifest.json
    ```

2. Set up your Meilisearch instance, or maybe you can use a hosted one I guess
3. Fill url to your Meilisearch instance in plugin settings, and maybe api key also required
4. Remember click `Save` and make sure the status reports `ok`.
5. Try typing something in search page
---

Because I don't really like setting up a reverse proxy or any of that hassle,
so I am writing this, but it still requires a Meilisearch instance.

At the moment it only works in the search page and only for the `/Items` endpoint, but it still improves a lot.

The core feature, which is to proxy the search request, is done by injecting an [`ActionFilter`](https://learn.microsoft.com/en-us/aspnet/core/mvc/controllers/filters?view=aspnetcore-8.0#action-filters) with reflection. 
So it may only support a few versions of Jellyfin. At the moment I'm using `Jellyfin 10.10.0`, 
but it should work on other versions as long as the required parameter name of `/Items` doesn't change.
