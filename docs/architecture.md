# SpotifyWPF Architecture

This document describes the internal structure established by the master-branch
MVVM refactor. The UI layout, feature set, and user-visible behavior are
unchanged; only where code lives has moved.

## Layering rules

```
View/Page/*.xaml          → page layout only; minimal code-behind
View/Component/*.xaml     → reusable UserControls (data grids, ActivityLogView)
ViewModel/Page/*.cs       → thin: properties, commands, delegates to services
ViewModel/Component/*.cs  → component view models (ActivityLogViewModel, grid VMs)
Service/*.cs              → business logic, Spotify API, queues, persistence
Model/*.cs                → DTOs and bindable items; no page logic
```

Rule of thumb: if logic survives app restarts, talks to Spotify, touches disk,
or manages a queue, it belongs in a **Service**. If it binds to XAML it belongs
in a **ViewModel**. If it renders UI it belongs in **View/Component**.

## Services

| Service | Responsibility |
|---------|----------------|
| `IPlaylistLocalStore` / `PlaylistLocalStore` | JSON persistence under `%LocalAppData%\SpotifyWPF\Playlists\<clientId>\`: available playlists, deletion queue, pagination state. The client-id subfolder is resolved per call so a re-login with a different client id switches stores. |
| `IRequestSpacingService` / `RequestSpacingService` | Serializes outgoing Spotify requests with a configurable minimum delay (default 150 ms). Shared by paging and deletion so their requests are spaced against each other. |
| `IPlaylistPagingService` / `PlaylistPagingService` | Fetches playlist pages from Spotify into the local store and owns the resumable paging position (offset + last known total, persisted across restarts). Skips ahead past pages that are already fully cached. |
| `IPlaylistDeletionService` / `PlaylistDeletionService` | Deletes (unfollows) staged playlists in up to 4 parallel batches, retries transient connection errors (max 4 attempts, 500 ms × attempt backoff), and cancels all remaining work on a Spotify rate limit. |
| `IPlaylistActionQueueService` / `PlaylistActionQueueService` | Owns the deferred action queue (load page / load all / delete selection): pending actions, pause/resume, and the sequential execution loop. The page view model supplies the executor callback. UI-thread only. |

Services report progress through a `LogMessage(string message, bool isVerbose)`
event; the owning page view model forwards these into its activity log. They are
registered as singletons in `ViewModelLocator` and resolved via SimpleIoc
constructor injection.

## Shared activity log component

`View/Component/ActivityLogView.xaml` + `ViewModel/Component/ActivityLogViewModel.cs`
provide the reusable log panel (Consolas 11 list, optional Default/Verbose
filter, Copy Selected / Copy All context menu, 200-entry cap).

- Page view models own an `ActivityLogViewModel` instance by **composition**
  (`PlaylistsPageViewModel.ActivityLog`) — do not register it in SimpleIoc,
  each page needs its own instance.
- Host it with `<component:ActivityLogView DataContext="{Binding ActivityLog}"/>`.
- Options: `ShowFilter` (default true), `NewestFirst` (default false — oldest
  first, appended at the bottom), `MaxEntries` (default 200), `FontFamily`,
  `FontSize`.
- `Log(message, verbose: true)` entries are only visible under the Verbose
  filter. Messages are formatted `[HH:mm:ss] [Verbose]? message`.

## Page lifecycle and navigation

Navigation swaps `MainViewModel.CurrentPage` between singleton page view
models; the corresponding view (`UserControl`) is **recreated on every
navigation** by the `ContentControl` data templates. This asymmetry is the
source of most revisit bugs, so the following rules apply:

1. **Page view models are singletons** (SimpleIoc default); never assume a
   fresh instance per visit.
2. **Heavy initialization must be idempotent.** Prefer an
   `EnsureInitializedAsync()` that can run on every navigate-to over a
   one-shot `if (_initialized) return;` flag — a flag set before a failed init
   permanently blocks retry.
3. **Views detach shared controls on `Unloaded`** if they host singletons
   owned elsewhere, but must not dispose them.
4. **Messenger subscriptions are registered once in the constructor.** Never
   register in a load/navigated handler, or revisits will duplicate handlers.

Page view models that need refresh-on-show implement `IPageLifecycle`
(`ViewModel/IPageLifecycle.cs`); `MainViewModel.NavigateTo` invokes
`OnNavigatedFromAsync` on the outgoing page and `OnNavigatedToAsync` on the
incoming one. `PlaylistsPageViewModel` implements it with an idempotent grid
refresh from the local store.

## Models

Types persisted to disk or bound into grids live in `Model/`:
`PlaylistCacheItem`, `DeletionQueueItem`, `DeletionStatus`,
`QueuedPlaylistAction`, `QueuedActionDetailItem`, `PlaylistActionType`,
`DeletePlaylistResult`, `PlaylistPaginationState`, `LogEntry`. Bindable models
derive from MvvmLight's `ObservableObject`; plain DTOs stay POCO.

`SpotifyApiErrorHelper` (Service/) centralizes Spotify failure classification:
rate-limit retry delays, transient connection error detection, insufficient
scope, and forbidden playlist-track access.
