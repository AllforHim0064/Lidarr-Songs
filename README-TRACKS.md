# Lidarr Tracks — experimental individual-song fork

This fork adds a search/download button to each song in an album's track list.
It searches Soulseek through slskd and configured Newznab/Torznab indexers.
Optional album fallback searches every supported Lidarr indexer, downloads the
album using the existing download client, and imports only the selected song.
It never bulk-replaces the other songs already in that album's library folder.

## Run with Docker

From this source directory:

```sh
cp tracks.env.example .env
# Edit .env: music/download paths and optional slskd connection.
docker compose -f compose.tracks.yaml up -d --build
```

Open http://localhost:8686, finish Lidarr's initial authentication setup, add a
root folder and configure your usual indexers and download clients. Use a new
configuration directory to try the fork before migrating your existing setup.
The Docker recipe targets Linux x64. It has not been built in this workspace;
the matching backend and production frontend were built separately.

## Select a song

1. Add the artist/album so Lidarr has its metadata and track list. If you only
   want selected songs, leave the album **unmonitored**. Normal album monitoring
   and RSS automation still work as in upstream Lidarr.
2. Open the album's track list and press the search icon in the song's Actions
   column. The button is disabled when that track already has a library file.
3. Song query results appear first. Leave **Include whole albums as a fallback**
   checked to see album releases as well. Choose one result to download.
4. Torrent/Usenet progress appears in Activity. Soulseek job status appears in
   the song dialog; the scheduled importer checks once per minute.

This version uses explicit result selection; it does not automatically retry
individual missing songs or add per-track monitoring. Failed song downloads
will not trigger upstream's automatic whole-album redownload.

## Album fallback and cleanup

Enable **Remove Completed** on the selected download client. For torrents,
configure seed limits and a pause/stop action in the client. After the requested
song imports, Lidarr's normal completed-download removal deletes the downloaded
release and its extras when the client reports that removal is allowed. Until
then, the album remains in the download directory for seeding. No extra songs
are copied into the music library.

Files are matched by MusicBrainz track/recording identity, or conservative
artist/title/album and duration checks. If the song is missing, tagged as another
version, ambiguous, or outside the allowed quality profile, the import blocks
and the download is retained. A download is marked complete only after the
requested song's import is confirmed, including across application restarts.

## Soulseek / slskd

Run slskd separately with your own account and enable an API key authorized for
searches and downloads. This integration requires the **download batches API**:
`POST /api/v0/transfers/downloads/batches` and
`GET /api/v0/transfers/downloads/batches/{id}`. Older slskd builds without these
endpoints are unsupported; upgrade slskd before enabling this source.

Set these variables on the Lidarr process/container:

| Variable | Value |
| --- | --- |
| `LIDARR_SLSKD_URL` | slskd base address, such as `http://slskd:5030`; omit `/api/v0` |
| `LIDARR_SLSKD_API_KEY` | slskd API key |
| `LIDARR_SLSKD_DOWNLOAD_ROOT` | Local path seen by Lidarr to slskd's completed download root |

Both containers must see the same underlying completed download files. Compose
sets the last variable to `/downloads/slskd`; slskd should write into the
corresponding host directory. Soulseek jobs use dedicated UUID subfolders under
`lidarr-tracks/`. Only that job's folder is removed after its song imports.
Failed or ambiguous imports retain the folder. Job state is saved in
`soulseek-track-jobs.json` beside the Lidarr database; include it in backups.

Soulseek search waits seven seconds for responses, so slow peers may not appear.
This release searches individual Soulseek files; album fallback uses Lidarr's
existing torrent/Usenet providers. One source failing does not prevent results
from the others. Credentials remain server-side.

## Existing installations and updates

Back up your configuration/database before migrating, and avoid running two
instances against one database. Disable upstream automatic application updates
while using the fork; an upstream update would replace its custom assemblies.
Keep the music library and downloads outside the application's installation
folder. The song importer never deletes unrelated library tracks.

## Build from source

Use the .NET SDK version pinned in `global.json`, Node 22 and Yarn 1.22.19:

```sh
yarn install --frozen-lockfile
yarn build --env production
dotnet build src/NzbDrone.Console/Lidarr.Console.csproj -c Release -p:EnableAnalyzers=false
dotnet build src/NzbDrone.Mono/Lidarr.Mono.csproj -c Release -p:EnableAnalyzers=false
cp -R _output/UI _output/net8.0/UI
dotnet _output/net8.0/Lidarr.dll -nobrowser -data=/absolute/path/to/tracks-config
```

The bundled Linux build is framework-dependent and requires .NET 8 ASP.NET Core
runtime, SQLite, chromaprint/fpcalc, mediainfo and ffmpeg. It includes the built
UI and native Mono helper. Start it with `./run.sh /absolute/path/to/config`.

## API

All routes use Lidarr's existing authentication and API key requirements:

| Method and route | Purpose |
| --- | --- |
| `GET /api/v1/trackdownload/search?trackId=123&albumFallback=true` | Search results and source warnings |
| `POST /api/v1/trackdownload/grab` | JSON body with `trackId` and server-issued `candidateId` |
| `GET /api/v1/trackdownload/soulseek` | Saved Soulseek job statuses |

Result tokens expire after 30 minutes or application restart and cannot be used
to supply arbitrary download URLs. Torrent/Usenet song results use the artist's
quality profile; album fallbacks also use upstream release decisions. This is
an interactive download workflow, not a replacement for every upstream release
filter or custom-format rule on song-only search results.

## Validation and scope

The backend and production frontend build successfully. Focused regression tests
cover song matching, rejecting alternate versions, conservative ambiguous-file
handling, preserving existing songs, generic track queries, completion checks,
and existing album download behavior. A local startup/API smoke check is also
included in the accompanying validation report.

Live downloads against a configured Soulseek account, private indexers and
download clients have not been exercised here. Container image builds and
Windows/macOS runtime packages are not validated. This is an experimental source
fork and Linux build; it is not a production-tested release.

Upstream: https://github.com/Lidarr/Lidarr (GPL-3.0). Original upstream license and
copyright notices remain in `LICENSE.md` and the source tree. The accompanying
patch applies to the upstream commit recorded in `TRACKS-BASE.txt`.
