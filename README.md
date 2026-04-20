# Missing Media Checker — Jellyfin Plugin

Scans your Jellyfin library and compares it against [TMDB](https://www.themoviedb.org/) to identify:

- Missing TV episodes and whole seasons
- Missing movies from collections (e.g. the Harry Potter collection)

Results are shown in a sortable, filterable report on the plugin's configuration page.

## Install via Jellyfin plugin repository

> **Before you can use the repository URL below, replace `YOUR_GITHUB_USER` with your actual GitHub username in:**
> - `manifest.json` download URL (Jellyfin fetches from `raw.githubusercontent.com`)
> - The `sourceUrl` field generated in each release (handled automatically by the GitHub Action — no manual edit needed)

1. In Jellyfin: **Dashboard → Plugins → Repositories → `+`**.
2. Use these values:
   - **Name:** `Missing Media Checker`
   - **Repository URL:**
     ```
     https://raw.githubusercontent.com/YOUR_GITHUB_USER/Jellyfin.Plugin.MissingMediaChecker/main/manifest.json
     ```
3. Go to **Catalog**, find **Missing Media Checker**, click **Install**.
4. Restart Jellyfin.
5. Open the plugin from **Dashboard → Plugins → Missing Media Checker** and paste your TMDB v3 API key.

## Publishing a new version

Releases are fully automated via `.github/workflows/release.yml`.

1. Bump `<Version>` in `Jellyfin.Plugin.MissingMediaChecker.csproj` and `version:` in `build.yaml` (use a four-part version like `1.1.0.0`).
2. Commit, push, tag:
   ```bash
   git tag v1.1.0.0
   git push origin v1.1.0.0
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

The build output is `bin/Release/net9.0/Jellyfin.Plugin.MissingMediaChecker.dll`. Drop it into your Jellyfin server's `plugins/MissingMediaChecker_1.0.0.0/` folder for local testing (and restart Jellyfin).

## Requirements

- Jellyfin 10.11 or newer (`targetAbi: 10.11.0.0`)
- A free TMDB v3 API key — get one at <https://www.themoviedb.org/settings/api>

## License

MIT — see [LICENSE](LICENSE).
