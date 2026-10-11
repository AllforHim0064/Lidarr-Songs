# Cadence

A self-hosted song request and playlist companion for **Lidarr Songs**. Dark charcoal, lavender accents, album artwork, responsive layouts, and an always-visible music player. Runs beside the existing Lidarr container and keeps its database in `cadence-data`.

## Start on your existing VM

From your Lidarr-Songs clone:

```bash
git pull --ff-only
nano .env
```

Append these settings to your existing `.env`. Keep your existing music/download settings:

```dotenv
CADENCE_LIDARR_API_KEY=your-lidarr-api-key
CADENCE_ADMIN_USER=admin
CADENCE_ADMIN_PASSWORD=your-long-unique-password
CADENCE_PORT=8787
CADENCE_SECURE_COOKIE=false
```

Find the API key in **Lidarr → Settings → General** (enable advanced settings if necessary). This is the **Lidarr API key**, not the slskd key. The admin password must contain at least 10 characters. Use a unique password; the first admin account is created only when the database is empty. Changing this environment variable later does not reset an existing password.

Then launch the companion. This command starts both services as needed:

```bash
sudo docker compose -f compose.tracks.yaml -f compose.cadence.yaml up -d --build
```

Open `http://YOUR-VM-IP:8787` and sign in with the account above. If your VM is still `192.168.101.5`, open `http://192.168.101.5:8787`.

If Lidarr is already running and you only want to build/start Cadence:

```bash
sudo docker compose -f compose.tracks.yaml -f compose.cadence.yaml up -d --build --no-deps cadence
```

The companion uses `http://lidarr-tracks:8686` on the Compose network, so changing Lidarr's **host** port does not affect it. If you configured a URL base within Lidarr itself, override `CADENCE_LIDARR_URL` in `compose.cadence.yaml` to include that base.

For HTTPS through Nginx Proxy Manager, proxy to the VM's port 8787 and set `CADENCE_SECURE_COOKIE=true` before recreating Cadence. Use HTTPS for remote access. Cookies marked Secure cannot sign in over plain HTTP.

## How it works

- **Discover / Your library:** browse albums and search songs, artists, and album names across Lidarr's existing track catalog. Filter downloaded or missing songs. Refresh imports updated availability.
- **Song requests:** listeners request missing tracks. Admins see every request; listeners see their own. Requests are deduplicated across accounts. An admin approves a request and chooses an indexer/slskd candidate through the fork's selected-song search API. Album fallbacks retain the same selective import behavior as Lidarr Songs.
- **Status:** pending → approved → downloading → available. Availability is checked against Lidarr's `hasFile` flag. The status refreshes every 45 seconds. A failed/stalled download remains “downloading” until an admin inspects it; the Manage activity panel shows Lidarr queue messages and Soulseek jobs. Automatic retries are not implemented.
- **Playlists:** create personal playlists, add downloaded or missing songs, rename, delete, remove songs, and drag rows to reorder. Play available songs with next/previous, shuffle, repeat, seeking, and volume controls. Missing songs remain in the playlist until downloaded. A playlist does not automatically request its songs.
- **Playback:** streams local files from the same `/music` path as Lidarr, mounted read-only in Cadence. HTTP byte ranges support seeking. The browser must support the audio format; this version does not transcode audio. No Spotify account or streaming integration is required or included.
- **M3U export:** exports available tracks as Cadence playback URLs. Those URLs require a signed-in session, so the export is intended for browser-session-aware clients, not general unauthenticated media players. It never embeds API keys or login tokens.
- **Manage:** admins can create listener accounts, search/add artists via Lidarr's metadata service, and inspect the download queue. Artist adds use the first configured root folder, quality profile, and metadata profile, with monitoring and automatic searches disabled. Configure your preferred defaults in Lidarr before adding artists.

This first version is a companion to the configured Lidarr catalog. To request a song by a new artist, an admin adds that artist, waits for Lidarr to load album/track metadata, then refreshes Cadence. Global recording discovery, Spotify imports, shared playlists, email notifications, SSO, automatic source selection, transcoding, and automatic download retries are not included.

## Accounts and data

Passwords use scrypt hashes. Sessions use HttpOnly, SameSite=Strict cookies; API mutations require a session CSRF token. The Lidarr API key stays on the server. Private playlists are restricted to their owner. Normal accounts cannot approve requests, choose download sources, add artists, or create accounts.

Back up `cadence-data/cadence.sqlite` with the container stopped (or use SQLite's backup API). Do not commit `.env` or `cadence-data`. Both are excluded from Git and the main Docker build context.

To reset an account password, run this command, enter the username and a new password at the prompts, then restart Cadence. The new password is not echoed or placed in shell history:

```bash
sudo docker compose -f compose.tracks.yaml -f compose.cadence.yaml exec cadence python reset_password.py
```

## Updates and logs

```bash
git pull --ff-only
sudo docker compose -f compose.tracks.yaml -f compose.cadence.yaml up -d --build --no-deps cadence
sudo docker compose -f compose.tracks.yaml -f compose.cadence.yaml logs --tail=100 cadence
```

## Validation

```bash
python3 -m unittest discover -s cadence -v
node --check cadence/static/app.js
```

Five HTTP integration tests cover authentication/CSRF/logout, request approval and duplicate handling, download candidate forwarding, playlist ownership/order validation and metadata identity remapping, audio byte ranges/path containment, and static security headers. Lidarr responses are mocked in these tests. Python and JavaScript syntax checks pass.

The Docker image, visual browser layout, live Lidarr connection, live downloading, and browser audio playback need verification on a configured VM. No browser QA infrastructure or Docker daemon was available in the build environment.

License: GPL-3.0, following the containing Lidarr fork.
