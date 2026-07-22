# SpotifyWPF (fork updates)

Personal fork of [SpotifyWPF](https://github.com/mrpnut/SpotifyWPF). The goal is to keep the original bulk-playlist tooling working on modern Spotify APIs, then add smaller quality-of-life improvements and personal experiments on top.

## Current focus

**Playlist, albums, artists, search**

Still working on playlist, albums, artists, and search flows against current Spotify API endpoints and bringing back the original playlist-management workflow: login, load playlists, stage items for deletion, and delete in batches with rate-limit awareness.

**Playlists functionality**

Playlists functionality is for the most part complete with logging, track grid loading, create-playlist fixes, staged deletion, and the Actions tab job queue.

**Ongoing — refactoring**

Refactoring parts of the codebase to reduce single-file bloat and abstract/restructure UI into reusable components (control panels, collapsible sections, shared dark-theme controls).

**Infinite Jukebox (experimental)**

**Infinite Jukebox** under **Experimental** — now merged from `algo-overhaul` into `master`. Beat-aware infinite looping with an **Enhanced** similarity pipeline (z-scored stacked features, continuation scoring, kNN + percentile graph, softmax navigation), ring mini player, session track list, Local WAV transport, and cached AppData analyses. Builds are **x64** today; an **x86** build may be added later.

**Lyrics + Local WAV branch modifiers (in progress)**

- Synced lyrics via **LRCLIB** (not Spotify) shown on the Infinite Jukebox stage; hops update the highlight from transport position.
- Softmax **lyric phrase bias** prefers hops at line boundaries when lyrics are mapped to beats.
- **Branch modifiers** (supercharge / turbocharge EQ+drive) are **Local WAV only** — Ctrl-drag a locked chord outward; Alt+double-click cycles tiers. Spotify streaming never runs the DSP chain.

**Release packaging**

Building a release zip and MSIX installer with an Azure-signed app certificate.

> These are working descriptions, not release milestones. Watch rate limits during usage; try to set a delay (>500ms between actions) and reduce large requests into smaller chunks. 500 consecutive requests can result in a 24-hour restriction.

## Changelog

### `master` — `algo-overhaul` merge: Enhanced Infinite Jukebox algorithm

Merged **`algo-overhaul`** into **`master`** (fast-forward from `628d958` through `f3dec16`). This is the largest jukebox change since the original Echo Nest port: a full MIR overhaul, authoring UX, Local WAV playback, and offline evaluation tooling.

**Algorithm — Enhanced (Classic) metric (Slices 2–6)**

Path B analysis (`tools/analyze_track.py`) now emits **beat-synchronous feature vectors** alongside the legacy Echo Nest-shaped segment list:

1. **Features** — L2-normalized chroma + MFCC[1:] (MFCC-0 dropped) + RMS-dB, per-dimension **z-scored** over the track.
2. **Beat sync** — `librosa.util.sync` with **median** aggregation → one vector per beat (symmetric; no segment-index pairing).
3. **Time-delay stack** — `librosa.feature.stack_memory` (`STACK_STEPS=4`) → each beat carries a short continuation fingerprint.
4. **Continuation edges** — hop `i→j` scores **`stack[i+1]` vs `stack[j]`** (landing fits what follows the exit), not twin downbeats (avoids doubled attacks/cymbals).
5. **Graph build** — **kNN** neighbors outside a **Theiler min-jump** window (≥8 beats default), **track-relative percentile** quality cap (replaces ε-threshold auto-tune loop), optional **mutual kNN**, **SCC bridge** edges for reachability, optional **Essentia region gate** when `regionEmbeddings` exist.
6. **Phase** — graduated **Soft / Hard / Off** circular mod-4 bar-phase penalty; **BeatThis** downbeats when available (bars from real downbeats, not `beat_times[::4]` only).
7. **Navigation** — **Softmax(−dist/τ − λ·visits + w_pref·preference)** with visit-radius novelty, post-hop dwell (`MinBeatsBetweenJumps`), end-loop guard (toggleable), locked branches with per-edge probability, and **Slice 6** pairwise preference reranking from scrub-after-hop negatives.

**Beat tracking** — **BeatThis** (learned beat/downbeat, ONNX or pip) with **Ellis DP** (`librosa.beat.beat_track`) fallback; **gap-split** for monster inter-beat intervals. Settings: Auto / BeatThis / DP. Legacy graph metric still available for old caches (`Graph metric: Legacy`).

**Compared to the original Infinite Jukebox (Paul Lamere, Echo Nest, 2012)**

| | **Original** | **This build (Enhanced)** |
|--|--------------|---------------------------|
| Features | Variable-length Echo Nest segments; weighted pitch/timbre/loudness/duration/confidence | Fixed beat-sync vectors; duration/confidence dropped on Path B |
| Distance | Single-beat segment list pairing (asymmetric); global threshold raised until ~10% of beats branch | Symmetric z-scored stack; continuation-oriented edge score |
| Graph | All pairs under ε; keeps nearest *and farthest* under threshold | Per-beat kNN + percentile cap only |
| Phase | Hard +100 veto for different bar position | Graduated soft penalty (or hard/off) |
| Playback choice | Uniform random among threshold survivors | Temperature softmax + visit novelty + optional preference ranker |
| Authoring | Tune slider only | Locks, presets bound to analysis fingerprint, ring hover, SSM heatmap |

**Compared to Remixatron (Dave Rensin)**

| | **Remixatron** | **This build (Enhanced)** |
|--|----------------|---------------------------|
| Similarity | **Spectral clustering** → each beat in one discrete cluster | **Continuous** z-scored distances end-to-end |
| Continuation | Boolean: candidate in correct cluster; next-beat cluster alignment | Numeric: `stack[i+1]` vs `stack[j]` |
| Phase | **Hard** same position in measure | Soft/Hard/Off graduated circular mod-4 |
| Jump choice | Random among cluster-valid targets | Softmax(−dist/τ − λ·visits) |
| Playback | Sample-accurate beat-buffer stitch (native engine) | Spotify Web Playback or **Local WAV** in-process seeks (±~50ms SDK jitter on streaming) |
| Authoring | Automatic CLI/player | Ring UI, locks, tune presets, heatmap, harness metrics |

Remixatron is the closest open-source prior art for beat-sync features and structure-aware jumps; Enhanced diverges by keeping **graded** similarity through graph build and navigation, scoring **continuation** instead of cluster membership, and exposing an **authoring** surface the original and Remixatron lack.

**UX & infrastructure (Slices 1, 1B, 3)**

- **Slice 1** — Toggleable end-loop guard, random-branches vs locks-only, tune presets with analysis fingerprint.
- **Slice 1B** — **Local WAV** transport (`LocalWavPlaybackHost`) for sample-accurate in-app seeks during jukebox hops; transport router switches Spotify vs local.
- **Slice 3** — **Self-similarity heatmap** (`SelfSimilarityHeatmapControl`) for Classic stacked features; ring Observe/hover hop diagnostics.
- **Manage** page, login → Infinite Jukebox landing, fractal/EQ visual chrome, session Local WAV cache controls, `tools/jukebox_harness.py` offline metrics (dead-beat rate, jump-length distribution, component coverage).

**Related readings**

*Foundations — where the original design came from*

- Tristan Jehan, *Creating Music by Listening* (PhD thesis, MIT Media Lab, 2005) — intellectual origin of the Echo Nest analyzer (segments, timbre, pitches, loudness, beats/bars); explains why duration/confidence existed for variable-length onset segments (dead on Path B).
- The Echo Nest Analyzer Documentation (Jehan & DesRoches) — JSON format spec; timbre as PCA coefficients (not raw MFCCs), which is why ported Echo Nest weights are misleading once Path B uses MFCCs.
- [Paul Lamere's Infinite Jukebox posts (2012)](https://musicmachinery.com/2012/11/12/the-infinite-jukebox/) — original weighted segment distance and threshold-to-target-branch-ratio heuristic.
- [Remixatron](https://github.com/drensin/Remixatron) — open-source reimplementation with beat-sync features, clustering, and jump selection; closest prior art to Enhanced Slices 2+4.

*Slice 2 — beat-synchronous features, stacking, distance*

- Ellis, "Beat Tracking by Dynamic Programming" (*J. New Music Research*, 2007) — `librosa.beat.beat_track`; monster-beat failure mode in quiet gaps; motivates gap-split.
- Bartsch & Wakefield, "To Catch a Chorus…" (WASPAA 2001) — beat-synchronous chroma aggregation; ancestor of `librosa.util.sync`.
- Logan, "Mel Frequency Cepstral Coefficients for Music Modeling" (ISMIR 2000) — MFCCs; MFCC-0 as log-energy (loudness triple-count fix).
- Fujishima (ICMC 1999); Müller, *Fundamentals of Music Processing* (Springer, 2015/2021) — chroma, SSM/structure toolkit; [FMP notebooks](https://www.audiolabs-erlangen.de/resources/MIR/FMP).
- Serrà, Serra & Andrzejak (2009); Takens (1981) — time-delay embedding justification for `stack_memory` / k=2–4 stacking.
- Theiler (1986); Kantz & Schreiber, *Nonlinear Time Series Analysis* — Theiler window → build-time min-jump.

*Slice 3 — self-similarity matrices*

- Foote (ACM Multimedia 1999; ICME 2000) — SSM visualization and diagonal-stripe interpretation (heatmap diagnostic).
- Müller & Kurth (ICASSP 2006) — path/diagonal SSM enhancement (`librosa.segment.path_enhance`).
- Paulus, Müller & Klapuri (ISMIR 2010) — music structure analysis survey.

*Slice 4 — graph topology, components, walk policy*

- McFee & Ellis (ISMIR 2014) — spectral clustering on recurrence + timeline (future Laplacian segmentation).
- von Luxburg (2007) — mutual-kNN vs hubs; Laplacian machinery.
- Radovanović et al. (JMLR 2010); Schnitzer et al. (ISMIR 2011) — hubness and mutual proximity in audio similarity.
- Tarjan (1972) — SCC for reachability bridges.
- Sutton & Barto, *Reinforcement Learning* (2nd ed., 2018), ch. 2 — softmax/Boltzmann selection and count-based novelty (exp(−d/τ) − λ·visits).

*Slice 5 — embeddings (optional region gate)*

- Pons & Serra, musicnn (2019); Alonso-Jiménez et al. (ICASSP 2020, ISMIR 2022) — Essentia TensorFlow models; `tools/attach_region_embeddings.py`.
- Cramer et al., OpenL3 (ICASSP 2019); Li et al., MERT (2023) — region vs beat-resolution tradeoffs; two-stage "embeddings pick region, Classic picks beat."

*Slice 6 — preference learning*

- Bradley & Terry (1952); Burges et al., RankNet (ICML 2005); Joachims (KDD 2002) — pairwise ranker and implicit negative feedback (scrub-after-hop).

*Rejected alternatives (confirmed deferrals)*

- Rabiner (1989) HMM; Lee & Seung (1999) NMF — deferred unless layered structure still disappoints after P1+P4.
- Transposition-invariant chroma — harmful without pitch-shifted playback (finds key-change splices that sound wrong).

**If you only read three:** Müller *FMP* (ch. 3–4, 7), McFee & Ellis 2014, Jehan's thesis.

---

### `experimental` since `aecb17d` — Infinite Jukebox UI, control panels, and mini player

**Infinite Jukebox (Prediction page)**

- Renamed Loop Lab UI to **Infinite Jukebox** (Paul Lamere attribution link).
- Added **JukeboxRingView** / **JukeboxRingCanvas**: beat-segment ring, playhead, branch-jump glow, and mini-player segment extensions.
- Unified **transport bar**: track ID input, prev/play-pause/next, Analyze, and green scrubber tied to Web Playback SDK position.
- Added collapsible **Control Panel** with tabs: **Logs**, **Tuning**, **Session**, **Music Predictions**, **Settings**.
- **Tuning** tab: jukebox branch sliders via **TuningSliderRow**; moved next-track prediction controls into **Music Predictions**.
- **Session** tab: queue of tracks for Loop Lab / Infinite Jukebox with play, remove, clear, export, import.
- **Settings** tab: Python Path B configuration and **Ring mini player mode** toggle.
- **LoopLabSessionStore** and **LoopLabSessionTrack** for persisted session tracks.
- **MarqueeTextBlock** for status-bar now-playing text.
- **CollapsiblePanel**, **CollapsibleActivityLogView**, **LoopLabBottomPanel** with drag-to-resize height and resize preview ghost.
- Shared **DarkThemeControls** styles: media scrubber, control-panel tabs, header toggle, toolbar, tuning value badges.
- Fixed inverted compact-layout visibility; removed auto-resize compact mode in favor of manual mini player.

**Mini player (two-window swap)**

- Separate **MiniPlayerWindow** (`AllowsTransparency` + borderless, set in XAML before first show).
- **MiniPlayerView**: ring + transport on a small draggable backdrop; restore (maximize) button top-left of controls.
- **MainWindow** hides while mini player is open; **File → Exit** and closing the mini player call **Application.Shutdown()** so the process does not linger.
- **JukeboxRingCanvas** mini-mode rendering: transparent inner area, corner segment wedges, custom hit-testing for ring interaction.

**Playlists page**

- **PlaylistsControlPanel**: collapsible **Controls** with **Logs**, **Actions**, and **Tracks** tabs (mirrors main Actions/logging patterns).
- Playlists page layout simplified to use the new control panel component.

**Menus and navigation**

- **File → Accounts** submenu: Log In, Change Account, Refresh token.
- **Experimental → Infinite Jukebox** (was Prediction).
- Login page polish; **Open in Loop Lab** from playlists context menu.

**Infrastructure**

- **MessageType.MiniPlayerModeChanged** for main/mini window coordination.
- WebView2 player remains parked on **MainWindow** while mini player is visible so playback survives mode switches.

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

**Infinite Jukebox** lives under **Experimental → Infinite Jukebox** on `master` (merged from `algo-overhaul`). It is isolated from the core Playlists workflow: beat-aware infinite looping, personalized next-track prediction experiments, ring mini player, and Local WAV analysis/playback. See the **`algo-overhaul` merge** changelog entry above for the Enhanced algorithm, Remixatron/original comparison, and bibliography.

## Planned improvements

- Revise the installer/publish flow for this fork (certificate, `appinstaller`, and release packaging).
- Continue playlist content workflows (track grid actions, create/add tracks, import/export polish).
- Keep rate-limit-safe request spacing across load, delete, and track-fetch operations.
- Laplacian section labels (McFee & Ellis 2014) and further Essentia region-gate polish on Infinite Jukebox.

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

# Experimental: Infinite Jukebox (Prediction page)

**Infinite Jukebox** (Loop Lab) is under **Experimental → Infinite Jukebox** on `master`. It is an in-app Spotify player used for beat-aware looping, infinite-jukebox-style branch tuning, and personalized next-track prediction experiments. The **Enhanced** graph metric (z-scored stacked beat vectors, continuation edges, kNN + percentile, softmax navigation) is documented in the main changelog under **`algo-overhaul` merge**.

**Requirements (all users):**

- A **Spotify Premium** account (Web Playback SDK requirement)
- **Re-login after upgrading** — Infinite Jukebox needs extra OAuth scopes (`streaming`, playback state, etc.). Cached tokens from older builds will prompt you to sign in again.
- **Microsoft Edge WebView2 Runtime** — hosts the embedded Spotify Web Playback SDK player.

Install WebView2 Runtime (Windows):

```powershell
winget install --id Microsoft.EdgeWebView2Runtime --accept-package-agreements --accept-source-agreements
```

Or download the Evergreen Bootstrapper from [Microsoft's WebView2 page](https://developer.microsoft.com/en-us/microsoft-edge/webview2/).

**Requirements (Path B local analysis only):**

If Spotify's `/audio-analysis` endpoint is unavailable for your developer app (403 — common for apps registered after Nov 2024), Infinite Jukebox falls back to **local analysis**: it records one full play-through via WASAPI loopback, then runs a Python sidecar (`tools/analyze_track.py`) with **librosa** and optional **BeatThis** beat/downbeat tracking.

Install Python 3 and the sidecar dependencies:

```powershell
# Use the Windows Python launcher so you don't pick up an old Python 2.x on PATH
py -3.12 -m pip install --upgrade pip
py -3.12 -m pip install librosa soundfile
```

Optional — learned beat/downbeat tracking (recommended; falls back to Ellis DP if missing):

```powershell
py -3.12 -m pip install beat_this onnxruntime
# Place BeatThis ONNX under tools/models/ — see tools/models/README.md
```

Verify:

```powershell
py -3.12 -c "import librosa, soundfile; print(librosa.__version__, soundfile.__version__)"
```

**Python interpreter (Path B):** Configure inside the app — no environment variables needed.

1. Install Python 3 and librosa (commands above).
2. Open **Experimental → Infinite Jukebox**.
3. In the **Control Panel → Settings** tab, under **Python (Path B)**, click **Auto-detect** (resolves the full `python.exe` path via the Windows `py` launcher), or **Browse** to `python.exe`.
4. The path is saved in per-user app settings (same store as your Spotify Client ID), typically under `%LocalAppData%` in a `user.config` file for SpotifyWPF.

If the field is left empty, SpotifyWPF tries auto-detect the first time local analysis runs.

**Using Infinite Jukebox:**

1. Log in (or re-login after upgrading).
2. Open **Experimental → Infinite Jukebox**, or right-click a playlist on the Playlists page and choose **Open in Loop Lab**.
3. On first player start, the app probes Spotify's audio-analysis API once and saves the result under `%LocalAppData%\SpotifyWPF\Prediction\analysis-source.json`.
4. Use the transport bar and **Control Panel** tabs for playback, tuning, session tracks, and predictions.
5. Enter **mini player** via the inward-arrow on the transport bar, **Control Panel → Settings**, or the ring mini player checkbox; drag the control backdrop to move the mini player; restore with the outward-arrow or close the mini player window to exit the app.

**Local analysis tips:** mute other apps during capture (WASAPI records the system mix). Each track is captured and analyzed at most once; results are cached under `%LocalAppData%\SpotifyWPF\Prediction\`.

### Installers vs portable

This fork currently has two distribution paths (see [Installation](#installation) below):

| Method | What you get | Infinite Jukebox extras |
|--------|----------------|-------------------------|
| **MSIX / appinstaller** (`SpotifyWPF.MSIX`) | Store-style install, auto-update via `Package.appinstaller` | WebView2 is usually already on Windows 11; installer can declare the WebView2 bootstrapper as a dependency when you revise packaging. **Python is not bundled** — user installs once, then sets path in Settings. |
| **Portable (GitHub release zip)** | Extract folder and run `SpotifyWPF.exe` | Same as MSIX for Python/WebView2: prerequisites are machine-wide, settings still live in per-user AppData (not beside the `.exe`). |

**Streamlined MSI/MSIX plan (when you revise the installer):**

1. Bundle or bootstrap **WebView2 Runtime** (required for all Infinite Jukebox users).
2. Do **not** bundle Python/librosa in the installer (large, license-heavy) — optional custom action could run `winget install Python.Python.3.12` or show a first-run checklist.
3. On first Infinite Jukebox open, prompt **Auto-detect Python** (already in the app UI).
4. Keep Spotify Client ID + Python path in **user settings** (AppData) — works identically for installed and portable builds.

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
