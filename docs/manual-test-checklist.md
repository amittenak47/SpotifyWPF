# Manual Test Checklist — Playlists Page

Run this checklist after any structural change to the Playlists page. All
behaviors must match the pre-refactor master exactly.

## Loading & cache

- [ ] Launch app, log in: playlists previously cached under
      `%LocalAppData%\SpotifyWPF\Playlists\<clientId>\` appear immediately.
- [ ] **Load** fetches one page (respects the Limit box, clamped 1–50) and
      appends only new playlists; offset advances and survives restart.
- [ ] **Load All** pages until Spotify's reported total is reached; abortable.
- [ ] **Refresh Selected** re-fetches chosen playlists; 404s remove them from
      the local cache.
- [ ] Create Playlist adds the playlist to Spotify and the local cache, then
      clears the form.

## Activity log (Logs tab)

- [ ] Default filter hides `[Verbose]` entries; switching to Verbose reveals
      them; switching back re-filters.
- [ ] Entries are formatted `[HH:mm:ss] message`, oldest at top, appended at
      bottom, capped at 200.
- [ ] Right-click → Copy Selected copies only highlighted rows; Copy All
      copies every visible row.

## Action queue (Actions tab)

- [ ] Enqueue Load / Enqueue Load All / Enqueue Delete add entries with
      correct display names and expandable detail items.
- [ ] Delete key on a detail item removes just that playlist from the action;
      removing the last detail removes the action.
- [ ] Execute runs actions front-to-back; button toggles Execute → Pause →
      Resume; Abort clears the queue and cancels the running action.

## Deletion queue

- [ ] `->` stages selected playlists (moves them out of the main grid,
      persists across restart); `<-` or Delete key unstages.
- [ ] Mark/Unmark For Deletion toggles the Queue column; Delete only affects
      marked+non-deleted rows and asks for confirmation.
- [ ] Deleted rows turn red, failed rows yellow; Refresh acknowledges results
      (deleted rows drop out on second Refresh, failed rows reset to Pending).
- [ ] Rate limit (429) stops remaining work and reports the Retry-After.

## Tracks & export

- [ ] Double-click (or Load Tracks) fills the Tracks grid and retitles the
      group box; non-owned playlists log the ownership note.
- [ ] Export writes the visible playlists grid to a chosen JSON file.
      (Import is intentionally disabled.)

## Navigation

- [ ] Navigate away (Albums/Artists/Search) and back to Playlists: no crash,
      grids/log/queue state intact, periodic grid refresh still running.
