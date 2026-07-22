# Lyric-aware Infinite Jukebox branching

How Loop Lab steers beat-graph hops using timed lyrics, how that differs from
**Phrase align** / **Bar phase penalty**, and the research this design draws on.

## Phrase align vs bar phase (not lyrics)

These two Tuning controls are **independent**. Turning Phrase align to `0`
does **not** disable Bar phase penalty.

| Control | When it applies | What it does |
|---------|-----------------|--------------|
| **Bar phase penalty** (`soft` / `hard` / `off`) | Graph **build** | Soft/hard distance penalty from `IndexInBar` (mod-4 bar slot). |
| **Phrase align (N beats)** | Navigation **filter** | Hard keep-only: hop `i→j` only if `i % N == j % N`. `0`/`1` = off. |

### Why N = 4 / 8 / 16 can wipe options on *Dreams* (Fleetwood Mac)

Phrase align uses **raw beat index modulo N**, not musical `IndexInBar`.
If beat 0 is not a true downbeat, or Path B inserted **gap-split** beats,
index phase drifts from the groove. Neighbors fail `i % N == j % N`, Softmax
sees an empty list, and the ring looks branchless. That is a navigation
filter — not SCC “components” at graph build, and **not lyrics**.

Laid-back tracks (e.g. *Dreams*): keep **Phrase align = off**, use **Bar
phase = soft**, or lock branches by ear.

## Three layered lyric Softmax bonuses (toggleable)

Shared weight: `LyricPhraseWeight` (0 = all lyric layers off).

1. **Phrase cuts** — prefer exit/land on timed lyric line starts; penalize mid-word landings.
2. **Same section** — prefer hops inside the same analysis section (verse/chorus-scale).
3. **Block-clean** — prefer lyric-block starts and line-end exits.

Layers are Softmax bonuses only: lyrics **steer**; they do not hard-filter.

## Research / publications

- Paul Lamere, *The Infinite Jukebox* (2012).
  https://musicmachinery.com/2012/11/12/the-infinite-jukebox/
- Remixatron: https://github.com/drensin/Remixatron
- Davies et al., *AutoMashUpper* (IEEE/ACM TASLP 2014) — phrase-level mashup transitions.
  https://doi.org/10.1109/TASLP.2014.2347135
- Foote, *Automatic audio segmentation using a measure of audio novelty* (ICME 2000).
  https://doi.org/10.1109/ICME.2000.869637
- Paulus, Müller, Klapuri, *Audio-based Music Structure Analysis* (ISMIR 2010 survey).
  https://www.audiolabs-erlangen.de/content/05_fau/professor/00_mueller/03_publications/2010_PaulusMuellerKlapuri_STAR-MusicStructure_ISMIR.pdf
- Wang / Kan et al., *LyricAlly* — timed lyric ↔ audio sync.
  https://www.comp.nus.edu.sg/~kanmy/papers/04432643.pdf
- *Multimodal Lyrics-Rhythm Matching* (arXiv:2301.02732).
  https://doi.org/10.48550/arXiv.2301.02732
- Kim et al., DJ mix subsequence alignment (arXiv:2008.10267).
  https://ar5iv.labs.arxiv.org/html/2008.10267
