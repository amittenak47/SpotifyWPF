# SpotifyWPF (fork updates)

Personal fork of [SpotifyWPF](https://github.com/mrpnut/SpotifyWPF). The goal is to keep the original bulk-playlist tooling working on modern Spotify APIs, then add smaller quality-of-life improvements and personal experiments on top.

## Current focus

**Stage 1 — restore and update original functionality**

Get the app working again on current Spotify API endpoints and bring back the original playlist-management workflow: login, load playlists, stage items for deletion, and delete in batches with rate-limit awareness.

**Stage 2 — fine-grained fixes and feature polish**

Smaller, targeted improvements around playlist content and day-to-day use: populate the Playlists page track grid, fix create-playlist behavior, improve logging/status, and tighten edge cases found while using the app.

> These are working descriptions, not release milestones. Branch names below are just a log of what changed. Watch rate limits during usage; Try to set a delay (>500ms between actions) and reduce large requests into smaller chunks. 500 consecutive requests can result in 24-hour restriction.

## Changelog

### `cursor/playlist-tracks-content`

- Populate the Playlists page **Tracks** grid when loading a playlist (double-click or **Load Tracks**).
- Show track position, album, disc/track numbers, duration, item type, Spotify ID, and notes for unavailable items.
- Paginate playlist track loading with request spacing and rate-limit handling.
- Fix playlist creation: resolve the current user explicitly, enforce collaborative/private rules, and improve status/logging.
- Revise this README with fork updates, changelog, and planned work.

### `cursor/playlist-management-refactor` (merged to `master`)

- Migrated to modern SDK-style `SpotifyWPF.csproj` and **SpotifyAPI.Web 7.4.2** with **Authorization Code + PKCE**.
- Added **Accounts / Login** flow with saved Client IDs, token refresh, and safer auth-server lifecycle.
- Added **Playlists** workflow: load/limit/load-all with smart pagination offset persistence, local JSON cache, export, and refresh selected.
- Added **Staged For Deletion** queue with mark/unmark, batch delete, deletion status coloring, and persisted queue state.
- Added **Actions** tab with enqueueable Load/Load All/Delete jobs, Execute/Pause/Resume, Abort, and shared **request spacing**.
- Added **Albums** and **Artists** pages with dark Spotify-themed UI.
- Added playlist **Create** UI (name, description, public/collaborative).
- Added verbose logging, log filter, and `SpotifyPlaylistProbe` diagnostic tool.
- Fixed login revisit/refresh lockups, queued-delete UI sync, and delete/load request spacing behavior.

### Earlier fork maintenance

- Modernized the build and migrated from Implicit Grant to PKCE against Spotify API v7.
- Restored login/page flow and dispatcher fixes needed for authentication callbacks.

## Experimental

The **`experimental`** branch adds personal experimental features on a separate page under **Experimental → Prediction**. This is not part of the main playlist tooling roadmap. Current direction includes infinite looping ideas and personalized next-track prediction, kept isolated from the core Playlists workflow.

## Planned improvements

- Revise the installer/publish flow for this fork (certificate, `appinstaller`, and release packaging).
- Continue playlist content workflows (track grid actions, create/add tracks, import/export polish).
- Keep rate-limit-safe request spacing across load, delete, and track-fetch operations.

## Using this fork

Build and run locally from `SpotifyWPF/SpotifyWPF.csproj` after setting your Spotify Client ID on the login page.

### Playlist tracks

On the **Playlists** page:

1. Load playlists with **Load** or **Load All**.
2. Select a playlist and click **Load Tracks**, or double-click the playlist row.
3. Track results appear in the bottom **Tracks** grid.

### Create playlist

Use the **Create Playlist** panel at the top of the Playlists page. Collaborative playlists are created as private, per Spotify API rules.

### Installer note

The original upstream installer links below still point at the upstream author's publish location. A fork-specific installer update is planned but not done yet.

---

# Original README

The text below is preserved from the original project author.

# SpotifyWPF
An unofficial, simple tools application for Spotify

This application was born out of a community ask to delete multiple playlists at a time (they had thousands of them).  With that said,
that's the only feature this application supports currently.

The idea is for this to be a sort of "power tools" application for Spotify.  Stuff that you can't do in the app (but can with the public APIs)
will be considered for addition to this app.  Things that you can do in the Spotify app, but cumbersone to perform, will be considersed as
well.

For example:
* Adding all of an artist's tracks to a playlist
* Mass deletion (technically unfollow) of playlists
* Displaying information from the API that the Spotify client doesn't show

If you have any feature ideas, you can either add it as an issue to this repo, or post it on the Spotify Community.  I'll keep a lookout
for any asks I see that the official app doesn't support.

# Installation

If you want to use the [installer](https://mrpnut.github.io/SpotifyWPF/SpotifyWPF.appinstaller), then you need to install my self signed certificate to your computer's trusted certificate authorities.
To do that easily, run the following in an Administrator PowerShell command prompt.  After that, run the installer.

```
Invoke-WebRequest -Uri "https://mrpnut.github.io/SpotifyWPF/SpotifyWPF.cer" -OutFile "$env:temp\SpotifyWPF.cer"; Import-Certificate -FilePath "$env:temp\SpotifyWPF.cer" -CertStoreLocation Cert:\LocalMachine\Root
```

If you don't want to use the installer, you can pick one of the Github releases to extract to a folder and run.
Using the installer will keep SpotifyWPF up to date.

# Running

After installation, you can just search for "SpotifyWPF" in your start menu if you used the installer.

# Experimental: Loop Lab (Prediction page)

The `experimental` branch adds **Loop Lab** under **Experimental → Prediction**. It is an in-app Spotify player used for beat-aware looping and personalized next-track prediction experiments.

**Requirements (all users):**

- A **Spotify Premium** account (Web Playback SDK requirement)
- **Re-login after upgrading** — Loop Lab needs extra OAuth scopes (`streaming`, playback state, etc.). Cached tokens from older builds will prompt you to sign in again.
- **Microsoft Edge WebView2 Runtime** — hosts the embedded Spotify Web Playback SDK player.

Install WebView2 Runtime (Windows):

```powershell
winget install --id Microsoft.EdgeWebView2Runtime --accept-package-agreements --accept-source-agreements
```

Or download the Evergreen Bootstrapper from [Microsoft's WebView2 page](https://developer.microsoft.com/en-us/microsoft-edge/webview2/).

**Requirements (Path B local analysis only):**

If Spotify's `/audio-analysis` endpoint is unavailable for your developer app (403 — common for apps registered after Nov 2024), Loop Lab falls back to **local analysis**: it records one full play-through via WASAPI loopback, then runs a Python sidecar (`tools/analyze_track.py`) with **librosa**.

Install Python 3 and the sidecar dependencies:

```powershell
# Use the Windows Python launcher so you don't pick up an old Python 2.x on PATH
py -3.12 -m pip install --upgrade pip
py -3.12 -m pip install librosa soundfile
```

Verify:

```powershell
py -3.12 -c "import librosa, soundfile; print(librosa.__version__, soundfile.__version__)"
```

**Python interpreter (Path B):** Configure inside the app — no environment variables needed.

1. Install Python 3 and librosa (commands above).
2. Open **Experimental → Prediction → Loop Lab**.
3. Under **Python (Path B)**, click **Auto-detect** (resolves the full `python.exe` path via the Windows `py` launcher), or **Browse** to `python.exe`.
4. The path is saved in per-user app settings (same store as your Spotify Client ID), typically under `%LocalAppData%` in a `user.config` file for SpotifyWPF.

If the field is left empty, SpotifyWPF tries auto-detect the first time local analysis runs.

**Using Loop Lab:**

1. Log in (or re-login after upgrading).
2. Open **Experimental → Prediction**, or right-click a playlist on the Playlists page and choose **Open in Loop Lab**.
3. On first player start, the app probes Spotify's audio-analysis API once and saves the result under `%LocalAppData%\SpotifyWPF\Prediction\analysis-source.json`.
4. Use the **Loop Lab** tab for playback/looping; use the **Predictions** tab for next-track scoring after a track finishes.

**Local analysis tips:** mute other apps during capture (WASAPI records the system mix). Each track is captured and analyzed at most once; results are cached under `%LocalAppData%\SpotifyWPF\Prediction\`.

### Installers vs portable

This fork currently has two distribution paths (see [Installation](#installation) above):

| Method | What you get | Loop Lab extras |
|--------|----------------|-----------------|
| **MSIX / appinstaller** (`SpotifyWPF.MSIX`) | Store-style install, auto-update via `Package.appinstaller` | WebView2 is usually already on Windows 11; installer can declare the WebView2 bootstrapper as a dependency when you revise packaging. **Python is not bundled** — user installs once, then sets path in Loop Lab. |
| **Portable (GitHub release zip)** | Extract folder and run `SpotifyWPF.exe` | Same as MSIX for Python/WebView2: prerequisites are machine-wide, settings still live in per-user AppData (not beside the `.exe`). |

**Streamlined MSI/MSIX plan (when you revise the installer):**

1. Bundle or bootstrap **WebView2 Runtime** (required for all Loop Lab users).
2. Do **not** bundle Python/librosa in the installer (large, license-heavy) — optional custom action could run `winget install Python.Python.3.12` or show a first-run checklist.
3. On first Loop Lab open, prompt **Auto-detect Python** (already in the app UI).
4. Keep Spotify Client ID + Python path in **user settings** (AppData) — works identically for installed and portable builds.
