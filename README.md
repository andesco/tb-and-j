# TB&J

Local Jellyfin plugin prototype for replacing TorBox Media Center STRM generation.

## Jellyfin Repository

Add this repository URL in Jellyfin:

```text
https://raw.githubusercontent.com/andesco/tb-and-j/main/manifest.json
```

## Behavior

- Fetches TorBox `torrents`, `usenet`, and `webdl` lists.
- Generates STRM files in a Jellyfin-owned library folder.
- Creates/attaches Jellyfin Movies and Shows libraries for its managed STRM folders.
- Tracks only media files that were managed into Jellyfin.
- Tombstones deleted/missing STRM files.
- Deletes a TorBox download only when every managed media file ever mapped to that TorBox item is tombstoned.
- Ignores `.nfo`, `.txt`, samples, images, and any non-media file that never entered the managed Jellyfin set.

## Configuration

After first load, edit Jellyfin's plugin configuration XML for `TB&J`.

Important fields:

- `TorBoxApiKey`
- `LibraryRootPath`
- `AutoCreateJellyfinLibraries`
- `MovieLibraryName`
- `SeriesLibraryName`
- `DeleteTorBoxItemWhenAllManagedFilesTombstoned`
- `SyncTorrents`
- `SyncUsenet`
- `SyncWebDownloads`

The default library root is:

```text
/var/lib/jellyfin/data/torbox-sync/library
```

When `AutoCreateJellyfinLibraries` is enabled, the plugin creates `movies` and `series` folders under that root and registers them as Jellyfin libraries during sync.

## Build

```bash
/usr/local/dotnet/dotnet publish -c Release
```

Install the published DLL and `meta.json` into:

```text
/var/lib/jellyfin/plugins/TorBoxSync_0.1.0.0/
```

Then restart Jellyfin.

## Attribution

This plugin ports architecture and TorBox API behavior from TorBox Media Center, which is MIT licensed.
