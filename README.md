# SpotifyWPF (fork updates)

Personal fork of [SpotifyWPF](https://github.com/mrpnut/SpotifyWPF). Bulk-playlist tooling on modern Spotify APIs, plus **Infinite Jukebox** (experimental) and library pages.

> **Rate limits:** use >500 ms between API actions; batch large requests. ~500 consecutive calls can trigger a 24-hour restriction.

## Checklist

**Legend:** `[x]` done · `🟡` in progress · `[ ]` planned/not started

**Current priority:** **experimental features** that make the music fun to use — Infinite Jukebox momentum, automated DJ, embeddings, manual hop UI, prediction tab, personal Local WAV audio, then packaging. Playlist / Albums / Artists / Search API depth is **not** the focus right now.

### Auth & platform
- [x] SpotifyAPI.Web 7.4.2, Authorization Code + PKCE
- [x] Login flow, saved Client IDs, token refresh
- [x] File → Accounts (log in, change account, refresh token)
- [x] Modern SDK-style `SpotifyWPF.csproj`, x64 build

### Playlists & library
- [x] Load / Load All playlists with pagination, offset persistence, local JSON cache, export
- [x] Tracks grid (position, album, disc/track #, duration, type, ID, unavailable notes)
- [x] Staged For Deletion queue, batch delete, persisted queue state
- [x] Actions tab job queue (Load, Load All, Delete) with spacing, pause/resume/abort
- [x] Create playlist (name, description, public/collaborative rules)
- [x] Albums and Artists pages (dark theme)
- [x] Playlists control panel (Logs, Actions, Tracks tabs)
- [x] Verbose activity log + filter
- [x] Open playlist in Infinite Jukebox from context menu

### Infinite Jukebox
- [x] Enhanced beat graph (z-scored stacked features, continuation edges, kNN + percentile, mutual kNN, SCC bridges)
- [x] Softmax navigation, visit novelty, dwell, end-loop guard, locked branches, tune presets
- [x] Path B local analysis (`tools/analyze_track.py`, librosa, BeatThis / Ellis DP fallback)
- [x] Spotify Web Playback + Local WAV transport (sample-accurate seeks)
- [x] Ring UI, hop authoring, SSM heatmap, session track list, mini player window
- [x] Preference learning from hop choices and scrub-after-hop negatives
- [x] Essentia region gate (when `regionEmbeddings` exist)
- [x] Shift+drag beat exclusions on ring (dialogue outros)
- [x] Synced lyrics via LRCLIB (karaoke column; hop highlight follows transport)
- [x] Lyric-flow Softmax steering (phrase cuts / same section / block-clean)
- [x] Local WAV branch modifiers (supercharge / turbocharge EQ+drive on locked hops)
- [x] Liveliness (single-roll replan before a random hop fires)
- [x] Verbose per-beat branch-probability logging (activity log → Verbose)
- [x] 3-state repeat, tuning info tips, terminal-black stage, per-track EQ palette
- [x] Web Playback readiness gate before analyze/play
🟡 **Fix manual branch/hop UI** — multi-step hop chains on the ring; improve lock deletions; allow freestyle branches (not only graph edges)
🟡 **Fix Prediction tab** — Music Predictions / similar-song finder (broken or incomplete today)
- [ ] **Momentum** — section-aware DJ feel (waveform / envelope + structure)
- [ ] **Waveform / envelope-aware splicing** — cleaner hop landings, quiet-outro gating
- [ ] **Automated DJ** — momentum-driven replans on top of liveliness + dwell
- [ ] **Embeddings** — test and tune Essentia region gate in real sessions
- [ ] **Personal audio** — expand Local WAV EQ/drive hooks, per-track enhancement presets
🟡 Optional **compress WAV → FLAC** for cache storage (keep WAV for waveform editing; FLAC for space)
- [ ] Laplacian section labels (McFee & Ellis 2014) for structure steering
- [ ] Instrumental stem remix + waveform-aligned overlays (Local WAV only; Phase 6 in forward plan)

### Tooling
- [x] `tools/jukebox_harness.py` offline metrics
- [x] `tools/attach_region_embeddings.py`, `SpotifyPlaylistProbe`

### Release & infra
🟡 Fork-specific release zip
🟡 MSIX / appinstaller with Azure-signed certificate
🟡 WebView2 bootstrap in installer
🟡 Optional x86 build (after x64 packaging is stable)

### Deferred
- [ ] Playlists schema, track grid actions, add-tracks workflow, import/export polish, Search page parity (Spotify API depth — revisit after experimental features)
- [ ] Further UI/control-panel refactoring (split single-file bloat, shared collapsible components)
- [ ] HMM / NMF structure layers (only if momentum + embeddings still disappoint)
- [ ] **Cross-platform shell** — port to Wails / Tauri (or similar) so the app is not Windows-only WPF
- [ ] **Vulkan rendering** — move ring, spectrum, and visual chrome off WPF immediate-mode drawing to a GPU path (lighter CPU, room for richer stage effects)

## Changelog

See [CHANGELOG.md](CHANGELOG.md).

## Usage guide

### Build from source

1. Open `SpotifyWPF/SpotifyWPF.csproj` in Visual Studio or run `dotnet build`.
2. Set your Spotify **Client ID** on the login page (create an app at [Spotify Developer Dashboard](https://developer.spotify.com/dashboard)).
3. Add redirect URI `http://localhost:5000/callback` (or whatever the login page shows).

### Install (upstream / portable)

This fork does not yet ship its own signed installer. Options:

| Method | Notes |
|--------|--------|
| **Build from source** | Recommended for development (above). |
| **Upstream appinstaller** | See [ORIGINAL_README.md](ORIGINAL_README.md) — requires trusting mrpnut's certificate. |
| **GitHub release zip** | Extract and run `SpotifyWPF.exe` when a fork release is published. |

**Planned:** MSIX / appinstaller with Azure-signed certificate, WebView2 bootstrap, portable release zip.

**Infinite Jukebox prerequisites (all users):**

- Spotify **Premium** (Web Playback SDK)
- **Re-login after upgrading** for streaming scopes
- [WebView2 Runtime](https://developer.microsoft.com/en-us/microsoft-edge/webview2/)

```powershell
winget install --id Microsoft.EdgeWebView2Runtime --accept-package-agreements --accept-source-agreements
```

### Playlists

1. Open **Playlists** → **Load** or **Load All**.
2. Select a playlist → **Load Tracks** (or double-click the row).
3. Stage rows for deletion → **Actions** tab → batch delete (respect spacing).

**Create playlist:** use the panel at the top of the Playlists page. Collaborative playlists are created private per Spotify API rules.

### Infinite Jukebox

Menu: **Experimental → Infinite Jukebox**, or right-click a playlist → **Open in Loop Lab**.

1. Log in (Premium; re-login if streaming scopes are missing).
2. Enter a track ID or pick from session/search; press play.
3. **Analyze track** — Spotify `/audio-analysis` or Path B local capture (see below).
4. **Control Panel → Tuning** — branch probability, dwell, liveliness, lyric flow, etc.
5. Ring — hover hops, press+drag to queue locks, ✓ confirm; Shift+drag paints excluded beats.
6. **Mini player** — inward arrow on transport bar or Settings; drag backdrop to move; close mini player exits the app.

**Path B** (when Spotify returns 403 on `/audio-analysis`):

```powershell
py -3.12 -m pip install --upgrade pip
py -3.12 -m pip install librosa soundfile
py -3.12 -m pip install beat_this onnxruntime   # optional, recommended
py -3.12 -c "import librosa, soundfile; print(librosa.__version__, soundfile.__version__)"
```

Configure in **Control Panel → Settings → Python (Path B)** (Auto-detect or Browse). Mute other apps during WASAPI capture. Cache: `%LocalAppData%\SpotifyWPF\Prediction\`.

**Installers vs portable (planned packaging):**

| Method | Infinite Jukebox extras |
|--------|-------------------------|
| MSIX / appinstaller | WebView2 dependency; Python not bundled — user installs once, sets path in Settings |
| Portable zip | Same prerequisites; settings live in per-user AppData |

## References

### In-repo docs

| Doc | Contents |
|-----|----------|
| [`docs/infinite-jukebox-forward-plan.md`](docs/infinite-jukebox-forward-plan.md) | Waveform, momentum, instrumental stems/overlays roadmap |
| [`docs/infinite-jukebox-lyric-flow.md`](docs/infinite-jukebox-lyric-flow.md) | Lyric Softmax layers; Phrase align vs bar phase |
| [CHANGELOG.md](CHANGELOG.md) | Fork history and merge notes |
| [ORIGINAL_README.md](ORIGINAL_README.md) | Upstream project readme |

### Research (Infinite Jukebox & lyric flow)

| Topic | Citation |
|-------|----------|
| Infinite beat-graph remix | Lamere, *The Infinite Jukebox* (2012); [Remixatron](https://github.com/drensin/Remixatron) |
| Phrase/section mashup cuts | Davies et al., *AutoMashUpper*, IEEE/ACM TASLP 2014 |
| Novelty / section boundaries | Foote, ICME 2000; Paulus, Müller, Klapuri, ISMIR 2010 |
| Timed lyrics ↔ audio | LyricAlly (Wang/Kan et al.); *Multimodal Lyrics-Rhythm Matching*, arXiv:2301.02732 |
| Beat-sync DJ transitions | Kim et al., arXiv:2008.10267 |
| Beat tracking | Ellis, *J. New Music Research* 2007; BeatThis |
| MIR foundations | Müller, *Fundamentals of Music Processing*; [FMP notebooks](https://www.audiolabs-erlangen.de/resources/MIR/FMP) |
| Structure / clustering | McFee & Ellis, ISMIR 2014; Foote SSM (1999/2000) |
| Preference learning | Bradley & Terry (1952); Burges, RankNet (ICML 2005) |