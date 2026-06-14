# TB&J

A Jellyfin plugin that syncs your TorBox library into Jellyfin as a native STRM-based media library. Movies, TV episodes, extras, specials, and pilots are all classified and organised automatically. Playback goes directly from Jellyfin clients to the TorBox CDN — your server never proxies video data.

## Install

In Jellyfin, open **Dashboard → Plugins → Manage Repositories → New Repository**.

Enter a repository name such as `TB&J`, then use this repository URL:

```
https://raw.githubusercontent.com/andesco/tb-and-j/main/manifest.json
```

Save the repository, open the plugin catalogue, install **TB&J**, and restart Jellyfin.

## Setup

Edit the plugin configuration XML at:

```
/var/lib/jellyfin/plugins/configurations/Jellyfin.Plugin.TorBoxSync.xml
```

### Required

| Field | Description |
|---|---|
| `TorBoxApiKey` | Your TorBox API key |
| `JellyfinPublicBaseUrl` | Public URL of your Jellyfin server, e.g. `https://jelly.example.com` — used to build play URLs in STRM files |

### Optional

| Field | Default | Description |
|---|---|---|
| `LibraryRootPath` | `/var/lib/jellyfin/data/torbox-sync/library` | Where STRM files are written |
| `MovieLibraryName` | `TorBox Movies` | Jellyfin library name for movies |
| `SeriesLibraryName` | `TorBox Series` | Jellyfin library name for TV shows |
| `AutoCreateJellyfinLibraries` | `true` | Creates and registers the libraries on first sync |
| `SyncTorrents` | `true` | Include torrents |
| `SyncUsenet` | `true` | Include Usenet downloads |
| `SyncWebDownloads` | `true` | Include web downloads |
| `SyncIntervalMinutes` | `15` | How often to sync |
| `RemoteMissingGraceSyncs` | `2` | Consecutive complete snapshots that must miss a file before its STRM is removed |
| `StateRetentionDays` | `90` | Retain terminal tombstone and completed deletion records for this many days |
| `DeleteTorBoxItemWhenAllManagedFilesTombstoned` | `false` | Delete a TorBox download when all its media files are removed from Jellyfin |
| `PlaySecret` | *(auto-generated)* | Random secret appended to play URLs as `?s=`. Auto-generated on first sync. Clear to rotate. |

## How it works

### Sync

Every sync cycle the plugin:

1. Fetches your TorBox `torrents`, `usenet`, and `webdl` lists
2. Classifies each video file as a movie, TV episode, TV special, or extra
3. Writes a STRM file for each item under `LibraryRootPath`
4. Deletes STRM files for items that have disappeared from TorBox
5. Triggers a Jellyfin library scan

### Playback

Each STRM contains a URL like:

```
https://jelly.example.com/torboxsync/play/torrents/12345/1/Movie.Title.2024?s=<secret>
```

When a client plays the item, Jellyfin hits this endpoint, validates the secret, and returns a `302` redirect to the TorBox CDN. Your server handles no video data. The TorBox API key is never sent to the client.

Sync is skipped when `JellyfinPublicBaseUrl` is not configured. The plugin never writes raw TorBox API URLs containing the API key into STRM files or persistent sync state.

The `?s=` secret is a random 32-character hex token unique to your installation. It is embedded in every STRM file at sync time and validated on every play request — wrong or missing secret returns `404`. To rotate it, clear `PlaySecret` in the config XML and trigger a sync; all STRM files will be rewritten with the new secret.

### File classification

TB&J inspects each file's full path within the TorBox download to determine where it belongs:

| Content | STRM location |
|---|---|
| Movie | `movies/{Title (Year)}/{Title (Year)}.strm` |
| TV episode | `series/{Show (Year)}/Season {NN}/{Show} - S{NN}E{NN} - {Title}.strm` |
| Extras, bonus content, featurettes, pilots, deleted scenes, etc. from a TV show torrent | `series/{Show}/Season 00/...strm` |

Season 00 is used for all TV show bonus content because Jellyfin's TV library scanner does not properly index named extras subfolders (`extras/`, `featurettes/`, etc.) — files placed there are orphaned with no parent and invisible in the UI.

Any unrecognised non-season subfolder within a TV show torrent (e.g. `Bonus Bits/`, `Making Of/`, `Pilot/`) is automatically routed to Season 00. No configuration required.

Title cleaning includes year extraction from sibling files when the torrent root folder carries no year, making it easier for Jellyfin to match shows like `The Planets (2019)` rather than just `The Planets`.

### State

The plugin tracks every file it has ever managed in:

```
/var/lib/jellyfin/plugins/Jellyfin.Plugin.TorBoxSync/state.json
```

This lets it detect when items disappear from TorBox and tombstone (delete) the corresponding STRM files. It will only delete a TorBox download itself if `DeleteTorBoxItemWhenAllManagedFilesTombstoned` is enabled and every managed file from that download has been tombstoned.

## Compatibility

Tested with **Jellyfin 10.11** and **Infuse** (via Jellyfin server connection). Should work with any Jellyfin client that supports direct play of STRM files.

## Build from source

```bash
dotnet publish -c Release
```

Copy the published DLL and `meta.json` to:

```
/var/lib/jellyfin/plugins/TorBoxSync_0.1.0.0/
```

Restart Jellyfin.

## Attribution

Initial plugin architecture ported from [TorBox Media Center](https://github.com/TorBox-App/torbox-media-center), which is MIT licensed.
