# Arr Dashboard — a Jellyfin plugin for Sonarr and Radarr

Adds an **Arr Dashboard** page to Jellyfin showing a release calendar, the status of
every show and movie your *arr apps track, the live download queue, what is missing,
and recent grab/import history — all in one place, without leaving Jellyfin.

Sonarr and Radarr are fully implemented. SABnzbd and qBittorrent are not wired up yet;
see [Adding download clients](#adding-download-clients) for where they slot in.

## What you get

| Tab | Shows |
| --- | --- |
| **Calendar** | Agenda or month grid of episode airings and movie cinema/digital/physical release dates, colour-coded by downloaded / downloading / missing |
| **Shows** | Every Sonarr series with a completion bar, episode counts, size on disk, next airing; filter and sort; one-click search for missing episodes |
| **Movies** | Every Radarr movie with downloaded / missing / unreleased state, quality, size; filter and sort; one-click search |
| **Activity** | Live queue across all instances with progress, ETA, client, indexer, warnings; remove/blocklist; auto-refreshes every 15s |
| **Missing** | Monitored episodes and movies with no file, newest first, with a search button |
| **History** | Recent grabs, imports, failures and deletions |

A status strip at the top shows each instance as online/offline with its version, and a
counter row summarises series, movies, missing items, queue depth and total size on disk.

Multiple instances of each app are supported — for example a 1080p Sonarr and a 4K
Sonarr — and results are merged, tagged with the instance name.

## How it is built

- **.NET 9 / Jellyfin 10.11** plugin, C#.
- **All *arr traffic is server-side.** The browser never sees an API key and never needs
  network access to Sonarr/Radarr. Posters are proxied through
  `GET /ArrDashboard/Image` so they work from outside your LAN too.
- **One dead instance does not blank the page.** Every aggregating endpoint returns
  `{ Items, Errors }`; failures are reported per instance and rendered as a banner.
- **Short-lived caching** (default 30s, 15s for the queue) keeps the dashboard from
  hammering the backends when several widgets want the same data.
- **Writes require admin.** Reads follow the *Let non-administrators open the dashboard*
  setting; searching and queue removal always require a Jellyfin administrator.

### Layout

```
Jellyfin.Plugin.ArrDashboard/
  Plugin.cs                     plugin registration + page registration
  PluginServiceRegistrator.cs   DI wiring
  Api/ArrDashboardController.cs REST surface consumed by the UI
  Services/ArrApiClient.cs      HTTP + auth + error mapping for the v3 APIs
  Services/ArrService.cs        fan-out across instances, Sonarr/Radarr -> one DTO shape
  Services/ResponseCache.cs     time-based memoisation with single-flight
  Models/ArrApiModels.cs        Sonarr/Radarr wire models
  Models/Dtos.cs                what the UI actually consumes
  Configuration/configPage.html admin settings page
  Web/arrdashboard.html         the dashboard itself
```

### API surface

All routes sit under `/ArrDashboard` and use normal Jellyfin authentication.

| Method | Route | Purpose |
| --- | --- | --- |
| GET | `/Status` | per-instance online state and version |
| GET | `/Summary` | counters for the header |
| GET | `/Calendar?daysPast=&daysFuture=` | merged release calendar |
| GET | `/Queue` | merged download queue |
| GET | `/Series` | all Sonarr series |
| GET | `/Movies` | all Radarr movies |
| GET | `/Missing?limit=` | monitored items with no file |
| GET | `/History?limit=` | recent events |
| GET | `/Image?instanceId=&path=` | poster proxy |
| POST | `/Refresh` | drop caches |
| POST | `/Search/Series/{instanceId}/{seriesId}` | admin — Sonarr series search |
| POST | `/Search/Episodes/{instanceId}` | admin — Sonarr episode search (body: `[123]`) |
| POST | `/Search/Movie/{instanceId}/{movieId}` | admin — Radarr movie search |
| DELETE | `/Queue/{kind}/{instanceId}/{queueId}` | admin — remove from queue |
| POST | `/TestConnection` | admin — validate URL + key before saving |

## Building

Needs the **.NET 9 SDK** (`dotnet --list-sdks` should show a `9.x`). Get it from
<https://dotnet.microsoft.com/download/dotnet/9.0>, or on Windows:

```bash
winget install Microsoft.DotNet.SDK.9
```

Then:

```bash
dotnet publish Jellyfin.Plugin.ArrDashboard/Jellyfin.Plugin.ArrDashboard.csproj -c Release -o out
```

## Releasing

Cutting a release publishes it to the plugin repository automatically:

1. Bump `version` in `build.yaml` and `<Version>`/`<AssemblyVersion>`/`<FileVersion>`
   in the `.csproj` to match, and add a changelog entry to `build.yaml`.
2. Commit, then tag and push:
   ```bash
   git tag v1.0.0.2
   git push origin v1.0.0.2
   ```
3. The `Release plugin` GitHub Actions workflow builds the DLL, zips it, publishes a
   GitHub Release with the zip attached, and updates `manifest.json` on the
   `gh-pages` branch with the new version's checksum and download URL.

The `gh-pages` branch needs **Settings → Pages → Source: Deploy from a branch →
gh-pages** enabled once, so `manifest.json` is served at the URL above.

## Installing

### Via the plugin repository (recommended)

1. In Jellyfin, go to **Dashboard → Plugins → Repositories → +**.
2. Add this repository URL:
   ```
   https://andycqos74.github.io/jellyfin-plugin-arrdashboard/manifest.json
   ```
3. Go to **Catalog**, find **Arr Dashboard** under General, and install it.
4. Restart Jellyfin, then continue from step 4 below.

### Manually

1. On the Jellyfin server, create a folder `plugins/ArrDashboard_1.0.0.1/` inside the
   Jellyfin **data** directory:
   - Windows: `%ProgramData%\Jellyfin\Server\plugins\`
   - Linux (native): `/var/lib/jellyfin/plugins/`
   - Docker: `/config/plugins/` inside the container
2. Copy `Jellyfin.Plugin.ArrDashboard.dll` from `out/` into that folder.
3. Restart Jellyfin.
4. Go to **Dashboard → Plugins → Arr Dashboard** and add your servers.
5. Open the dashboard from the link at the top of that settings page, or go
   straight to `http://<your-server>/web/#/configurationpage?name=arrdashboard`.

### Opening the dashboard

Jellyfin's web client dropped main-menu entries for plugin pages after 10.8, so the
dashboard is not in the sidebar and cannot be put there from a plugin. It lives at:

```
http://<your-server>/web/#/configurationpage?name=arrdashboard
```

Worth bookmarking. The settings page links to it, and the dashboard links back.

Plugin pages are served under the admin section of the web client, so only Jellyfin
administrators can open them, whatever *Let non-administrators open the dashboard* is
set to — that setting governs the `/ArrDashboard/*` API only, which matters if you
build your own front end against it.

### Configuring an instance

- **URL** — the app root as you would type it in a browser, e.g. `http://192.168.1.10:8989`.
  Do **not** include `/api`. If Sonarr runs behind a reverse proxy with a URL base,
  include it: `https://media.example.com/sonarr`.
- **API key** — Sonarr/Radarr → Settings → General → Security → API Key.
- Press **Test** to verify before saving.

The Jellyfin server, not your browser, is what must be able to reach these URLs.

## Notes and limitations

- The month grid always renders the current calendar month; the range buttons control
  how much data is fetched, which the agenda view shows in full.
- HTTPS with a self-signed certificate will fail — Jellyfin's HTTP client validates
  certificates. Use `http://` on the LAN or a properly trusted certificate.
- Sonarr/Radarr **v3** API only (Sonarr v3+/v4, Radarr v3+). Sonarr v2 is not supported.
- Episode runtimes fall back to the series runtime when Sonarr does not supply one.
- Plugin pages are admin-only in the 10.11 web client; see
  [Opening the dashboard](#opening-the-dashboard).

## Troubleshooting

- **The plugin's Settings button opens the dashboard instead of the settings form.**
  Fixed in 1.0.0.1. The web client picks a plugin's settings page with
  `findBestConfigurationPage()`, which prefers any page flagged `EnableInMainMenu`;
  the dashboard page carried that flag, so it won. The flag no longer does anything
  useful, so it is gone.
- **The page loads but stays empty.** Open the browser console. Failures now show as a
  banner on the page with the HTTP status; anything server-side is logged by Jellyfin
  under `[ArrDashboard]`.
- **Errors in the Jellyfin log about `/Items/<guid>/Images/Primary`.** Those come from
  Jellyfin fetching artwork for your own library items from a metadata provider, not
  from this plugin. Every request this plugin makes is logged with an `[ArrDashboard]`
  prefix and every route it serves begins with `/ArrDashboard`.

## Adding download clients

SABnzbd and qBittorrent were deliberately left out of this first pass — Sonarr and
Radarr already report per-item download progress, client name and ETA in `/Queue`,
which covers most of what a download-client view would show.

When you want them, the shape is already there:

1. Add instance arrays to `PluginConfiguration` (qBittorrent additionally needs
   username/password and a cookie-based login; SABnzbd uses an API key in the query
   string, not a header).
2. Add a client alongside `ArrApiClient` — SABnzbd: `GET /api?mode=queue&output=json`;
   qBittorrent: `POST /api/v2/auth/login` then `GET /api/v2/torrents/info`.
3. Map them into `QueueItemDto` (it is already client-agnostic) and merge in
   `ArrService.BuildQueueAsync`, or add a separate `/ArrDashboard/Downloads` endpoint
   if you want global speed/disk-space figures that the *arr APIs do not expose.
4. Add a tab in `Web/arrdashboard.html` — the render helpers take plain arrays.
