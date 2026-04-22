# Missing Media Checker — Jellyfin Plugin

Scans your Jellyfin library and compares it against [TMDB](https://www.themoviedb.org/) to identify:

- Missing TV episodes and whole seasons
- Missing movies from collections (e.g. the Harry Potter collection)

Results are shown in a sortable, filterable, paginated report on the plugin's configuration page.

## Features

- **Missing episode & movie detection** — cross-references every series and collection against TMDB
- **New since last scan** — highlights items that appeared since the previous run
- **Season badges** — collapsed series headers show which seasons have missing content (full red / partial amber)
- **Poster thumbnails** — TMDB artwork shown next to each group header
- **Ignore list** — suppress individual episodes, seasons, series, movies, or entire collections; managed from the UI
- **Incremental / smart scan** — skips ended series whose owned-episode count hasn't changed, reducing TMDB API calls dramatically
- **Recently-aired-only mode** — limits results to episodes/movies aired within a configurable window (e.g. last 30 days)
- **Streaming / paginated API** — `/Results/Summary` + `/Results/Groups` endpoints; UI loads pages on demand
- **Plugin notifications** — posts a Jellyfin activity-log entry when new missing media is found

## Install via Jellyfin plugin repository

1. In Jellyfin: **Dashboard → Plugins → Repositories → `+`**.
2. Use these values:
   - **Name:** `Missing Media Checker`
   - **Repository URL:**
     ```
     https://raw.githubusercontent.com/Aryza/Jellyfin.Plugin.MissingMediaChecker/main/manifest.json
     ```
3. Go to **Catalog**, find **Missing Media Checker**, click **Install**.
4. Restart Jellyfin.
5. Open the plugin from **Dashboard → Plugins → Missing Media Checker** and paste your TMDB v3 API key.

## Publishing a new version

Releases are fully automated via `.github/workflows/release.yml`.

1. Bump `<Version>` in `Jellyfin.Plugin.MissingMediaChecker.csproj` and `version:` in `build.yaml` (four-part version, e.g. `1.2.0.0`).
2. Commit, push, tag:
   ```bash
   git tag v1.2.0.0
   git push origin v1.2.0.0
   ```
3. Draft a GitHub **Release** from that tag — paste the changelog into the release body.
4. Publishing the release triggers the workflow, which will:
   - Build the plugin with [jprm](https://github.com/oddstr13/jellyfin-plugin-repository-manager).
   - Attach the built `.zip` to the release.
   - Append a new entry to `manifest.json` with the correct checksum + source URL.
   - Commit the updated manifest back to `main`.

Users who already added the repo URL will see the new version appear in their Jellyfin catalog on the next refresh.

## Development

```bash
dotnet build -c Release
```

The build output is `bin/Release/net9.0/Jellyfin.Plugin.MissingMediaChecker.dll`. Drop it into your Jellyfin server's `plugins/MissingMediaChecker_1.1.0.0/` folder for local testing and restart Jellyfin.

## Requirements

- Jellyfin 10.11 or newer (`targetAbi: 10.11.0.0`)
- A free TMDB v3 API key — get one at <https://www.themoviedb.org/settings/api>

## License

MIT — see [LICENSE](LICENSE).
