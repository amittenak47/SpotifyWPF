# Changelog

Fork updates on top of [mrpnut/SpotifyWPF](https://github.com/mrpnut/SpotifyWPF). Newest first.

---

## `master` — README checklist restructure (2026-07)

- Replaced narrative README with status checklist, consolidated usage guide, and linked changelog / original readme.

## `master` — `cursor/lyrics-branch-modifiers-e82b` merge

Infinite Jukebox polish and DJ-oriented tuning (not a new graph metric).

- **Lyrics** — LRCLIB synced lyrics on stage; karaoke column; lyric-flow Softmax layers (phrase / section / block-clean). See [`docs/infinite-jukebox-lyric-flow.md`](docs/infinite-jukebox-lyric-flow.md).
- **Local WAV branch modifiers** — supercharge / turbocharge EQ+drive on locked hops (Ctrl-drag / Alt-cycle); cleared when switching back to Spotify.
- **Navigation** — continuation-phase branching fixes; Shift+drag ring exclusions for dialogue outros; dwell / branch-probability tuning defaults from real Loop Lab sessions.
- **Liveliness** — optional single-roll replan of a random hop before trigger (sparse; no probability ramp).
- **Verbose hop logging** — per-beat branch rolls in activity log (Verbose filter).
- **UX** — 3-state repeat, tuning info tips, terminal-black stage, per-track EQ palette, Web Playback readiness gate before analyze/play.
- **Forward plan** — [`docs/infinite-jukebox-forward-plan.md`](docs/infinite-jukebox-forward-plan.md) (waveform, momentum, instrumental stems).

### Commits in merge

| Commit | Summary |
|--------|---------|
| `628c01d` | Synced lyrics, lyric-aware hops, Local-WAV branch modifiers |
| `e890d8f` | Clear Local WAV modifiers when switching back to Spotify |
| `cc7f1a1` | Toggleable lyric-flow layers; Phrase align vs bar phase docs |
| `3578654` | Karaoke lyrics column, 3-state repeat, tuning tips, terminal black |
| `c7679cd` | Continuation-phase branching, ring exclusions, stage polish, per-track EQ |
| `a1246fe` | Wait for Web Playback ready before analyze/play |
| `e17c9e3` | Verbose logging for branch probability rolls |
| `1fb8e0d` | Liveliness setting (replan once before hop) |
| `4402c0e` | Jukebox tuning defaults from Loop Lab preset |
| `b1b2ae2` | README focus + forward plan |

---

## `master` — Playlists & Manage UI (2026)

- Playlist paging fixes (`LastKnownTotal`, fetch cursor on deletes).
- Manage Playlists layout redesign; control panel hierarchy; accordion crash fix.
- Actions tab polish (slider jump-to-click, load menu stays open, abort/refresh colors).
- Theme color picker, Spotify green default, palette presets.
- Infinite Jukebox Phase 2: session library, search picker, transport polish.

---

## `master` — `algo-overhaul` merge: Enhanced Infinite Jukebox

Largest jukebox change since the original Echo Nest port: MIR overhaul, authoring UX, Local WAV playback, offline evaluation.

### Algorithm — Enhanced (Classic) metric

Path B analysis (`tools/analyze_track.py`) emits beat-synchronous feature vectors:

1. **Features** — L2-normalized chroma + MFCC[1:] + RMS-dB, z-scored per track.
2. **Beat sync** — `librosa.util.sync` (median) → one vector per beat.
3. **Time-delay stack** — `librosa.feature.stack_memory` (4 steps) → continuation fingerprint per beat.
4. **Continuation edges** — score `stack[i+1]` vs `stack[j]` (not twin downbeats).
5. **Graph** — kNN outside Theiler window, percentile quality cap, mutual kNN, SCC bridges, optional Essentia region gate.
6. **Phase** — Soft / Hard / Off mod-4 bar-phase penalty; BeatThis downbeats when available.
7. **Navigation** — Softmax(−dist/τ − λ·visits + w_pref·preference), dwell, end-loop, locked branches, preference reranking.

**Beat tracking** — BeatThis (ONNX) with Ellis DP fallback; gap-split for monster intervals.

### UX & infrastructure (Slices 1–6)

| Slice | Shipped |
|-------|---------|
| 1 | End-loop toggle, random-branches vs locks-only, tune presets with analysis fingerprint |
| 1B | Local WAV transport (`LocalWavPlaybackHost`), Spotify vs local router |
| 2 | Classic metric, BeatThis / DP beat tracker settings |
| 3 | SSM heatmap, ring Observe/hover hop diagnostics |
| 4 | Mutual kNN, SCC bridges, post-hop dwell, visit-radius novelty |
| 5 | Essentia region gate when embeddings exist |
| 6 | Preference ranker, scrub-after-hop negatives, tuning controls |

Also: ring UI, locks, session tracks, mini player, `tools/jukebox_harness.py`, wave-ring EQ preset, Local WAV speed dial.

### Compared to original Infinite Jukebox (Lamere, 2012)

| | Original | This build |
|--|----------|------------|
| Features | Echo Nest variable segments | Fixed beat-sync z-scored vectors |
| Graph | All pairs under ε | kNN + percentile cap |
| Phase | Hard bar veto | Soft / Hard / Off |
| Choice | Uniform random | Softmax + novelty + preferences |
| Authoring | Tune slider | Locks, presets, ring, heatmap |

### Compared to Remixatron

| | Remixatron | This build |
|--|------------|------------|
| Similarity | Spectral clustering | Continuous z-scored distances |
| Continuation | Cluster membership | Numeric stack[i+1] vs stack[j] |
| Playback | Native beat-buffer stitch | Spotify SDK or Local WAV seeks |
| Authoring | Automatic CLI | Ring UI, locks, harness metrics |

---

## `experimental` — Infinite Jukebox UI foundation

- Renamed Loop Lab → **Infinite Jukebox**; **Experimental → Infinite Jukebox** menu.
- **JukeboxRingView** / **JukeboxRingCanvas**, transport bar, collapsible control panel (Logs, Tuning, Session, Music Predictions, Settings).
- **MiniPlayerWindow** two-window swap; WebView2 parked on main window during mini mode.
- **PlaylistsControlPanel** (Logs, Actions, Tracks).
- **LoopLabSessionStore**, dark-theme controls, marquee status text.

---

## `cursor/playlist-tracks-content`

- Tracks grid on playlist load (double-click or Load Tracks).
- Paginated track fetch with rate-limit handling.
- Create-playlist fixes (user resolution, collaborative/private rules).

---

## `cursor/playlist-management-refactor` (merged to `master`)

- SpotifyAPI.Web 7.4.2, Authorization Code + PKCE.
- Playlists: load/limit/load-all, cache, export, staged deletion, Actions job queue.
- Albums and Artists pages; create playlist UI.
- Verbose logging, `SpotifyPlaylistProbe`.

---

## Earlier fork maintenance

- Migrated from Implicit Grant to PKCE (Spotify API v7).
- Modern SDK-style project; login/page dispatcher fixes.
