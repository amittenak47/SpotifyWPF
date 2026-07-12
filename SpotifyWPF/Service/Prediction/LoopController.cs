using System;
using System.Collections.Generic;
using SpotifyWPF.Model.Prediction;
using SpotifyWPF.Service.Playback;

namespace SpotifyWPF.Service.Prediction
{
    public interface ILoopController
    {
        /// <summary>Profile driving the current track's loop, if any.</summary>
        LoopProfile ActiveProfile { get; }

        string CurrentTrackId { get; }

        /// <summary>True when a loop (simple or jukebox) is currently enforcing seeks.</summary>
        bool IsLoopActive { get; }

        /// <summary>Loads the stored profile for a track (or a fresh disabled one).</summary>
        LoopProfile GetProfileForTrack(string trackId);

        /// <summary>Persists the profile and (de)activates it when it belongs to the current track.</summary>
        void ApplyProfile(LoopProfile profile);

        /// <summary>Human-readable loop activity for the UI log ("armed", "jumped", …).</summary>
        event EventHandler<string> LoopEvent;

        /// <summary>Raised when a jukebox jump is planned or performed (ring glow).</summary>
        event EventHandler<JukeboxJumpEventArgs> JukeboxJump;

        /// <summary>Raised whenever the active track or loop state changes (UI refresh).</summary>
        event EventHandler ActiveLoopChanged;

        void InvalidateGraphCache();

        /// <summary>
        /// After a user scrub/seek, drop the armed jump and replan from the new playhead so an
        /// old planned hop cannot override the scrub.
        /// </summary>
        void NotifyPlaybackSeek(long positionMs);

        /// <summary>
        /// Returns the (cached) beat graph for a track, building it from the cached analysis when
        /// needed. Null when no analysis exists yet. Used by the ring UI — the graph itself stays
        /// service-side.
        /// </summary>
        BeatGraph GetGraphForTrack(string trackId);
    }

    /// <summary>
    /// Seek-based looping on the live stream (no audio rewriting): plays until a boundary and seeks.
    /// Simple mode implements the outro skip — when position reaches LoopEndMs, seek to LoopStartMs.
    /// The actual position watch runs inside the player page (armed action); this class decides what
    /// to arm and re-arms after each jump. Jukebox mode plans beat-graph jumps via BeatNavigator.
    /// </summary>
    public class LoopController : ILoopController
    {
        private const string SimpleLoopActionId = "loop:simple";

        private const string JukeboxActionId = "loop:jukebox";

        private readonly IJukeboxTransport _playbackHost;

        private readonly ILoopRegionStore _store;

        private readonly IJukeboxSettingsStore _jukeboxSettings;

        private readonly BeatGraphBuilder _graphBuilder = new BeatGraphBuilder();

        private readonly BranchPreferenceStore _preferences = new BranchPreferenceStore();

        /// <summary>Beat graphs are pure functions of the cached analysis; keep them per track.</summary>
        private readonly Dictionary<string, BeatGraph> _graphCache = new Dictionary<string, BeatGraph>();

        private BeatNavigator _navigator;

        private JukeboxJump _plannedJump;

        private long _lastPositionMs;

        public LoopProfile ActiveProfile { get; private set; }

        public string CurrentTrackId { get; private set; }

        public event EventHandler<string> LoopEvent;

        public event EventHandler<JukeboxJumpEventArgs> JukeboxJump;

        public event EventHandler ActiveLoopChanged;

        public LoopController(IJukeboxTransport playbackHost, ILoopRegionStore store,
            IJukeboxSettingsStore jukeboxSettings)
        {
            _playbackHost = playbackHost;
            _store = store;
            _jukeboxSettings = jukeboxSettings;

            _jukeboxSettings.SettingsChanged += OnJukeboxSettingsChanged;

            _playbackHost.StateChanged += OnStateChanged;
            _playbackHost.ActionFired += OnActionFired;
            _playbackHost.PositionUpdated += OnPositionUpdated;
        }

        private void OnJukeboxSettingsChanged(object sender, EventArgs e)
        {
            // Navigator-only settings (branch chance, reverse/long filters, seek lead) just re-arm.
            // Topology changes (threshold, end-loop) clear the graph cache.
            if (IsLoopActive && ActiveProfile?.Mode == LoopModes.Jukebox)
                Rearm();
        }

        public bool IsLoopActive =>
            ActiveProfile != null && ActiveProfile.Enabled &&
            ActiveProfile.TrackId == CurrentTrackId && CurrentTrackId != null &&
            (ActiveProfile.Mode == LoopModes.Jukebox || ActiveProfile.IsValidRegion);

        public LoopProfile GetProfileForTrack(string trackId)
        {
            if (string.IsNullOrEmpty(trackId))
                return null;

            return _store.Get(trackId) ?? new LoopProfile { TrackId = trackId };
        }

        public void ApplyProfile(LoopProfile profile)
        {
            if (profile == null || string.IsNullOrEmpty(profile.TrackId))
                return;

            _store.Save(profile);

            if (profile.TrackId == CurrentTrackId)
            {
                ActiveProfile = profile;
                Rearm();
            }

            ActiveLoopChanged?.Invoke(this, EventArgs.Empty);
        }

        private void OnStateChanged(object sender, PlayerStateSnapshot state)
        {
            if (string.IsNullOrEmpty(state.TrackId) || state.TrackId == CurrentTrackId)
                return;

            CurrentTrackId = state.TrackId;
            ActiveProfile = _store.Get(state.TrackId);
            _navigator = null;
            _plannedJump = null;
            _lastPositionMs = state.PositionMs;

            // The player page dropped any armed action on track change; arm for the new track.
            Rearm();

            ActiveLoopChanged?.Invoke(this, EventArgs.Empty);
        }

        private void OnActionFired(object sender, ArmedActionFiredEventArgs e)
        {
            if (e.ActionId == SimpleLoopActionId)
            {
                LoopEvent?.Invoke(this,
                    $"Loop: reached {FormatMs(e.FiredAtMs)}, jumped back to {FormatMs(e.SeekToMs)}.");
                Rearm();
                return;
            }

            OnJukeboxActionFired(e);
        }

        private void Rearm()
        {
            if (!IsLoopActive)
            {
                _playbackHost.DisarmAction();
                return;
            }

            if (ActiveProfile.Mode == LoopModes.Jukebox)
            {
                RearmJukebox();
                return;
            }

            _playbackHost.ArmAction(SimpleLoopActionId, ActiveProfile.LoopEndMs, ActiveProfile.LoopStartMs);
            LoopEvent?.Invoke(this,
                $"Loop armed: {FormatMs(ActiveProfile.LoopStartMs)} ↔ {FormatMs(ActiveProfile.LoopEndMs)}.");
        }

        private void RearmJukebox()
        {
            var graph = GetGraphForTrack(CurrentTrackId);

            if (graph == null)
            {
                _playbackHost.DisarmAction();
                LoopEvent?.Invoke(this, "Infinite Jukebox needs analysis — use \"Analyze track\" first.");
                return;
            }

            // Recreate on every rearm so branch-lock edits on the profile take effect immediately.
            // Preserve visit / dwell state so lock/settings edits don't reset anti-local-minima.
            var priorVisits = _navigator?.ExportVisitMemory();
            var priorCounts = _navigator?.ExportVisitCounts();
            var priorDwell = _navigator?.ExportBeatsSinceJump() ?? int.MaxValue / 4;
            _navigator = new BeatNavigator(graph, _jukeboxSettings.Get(), ActiveProfile,
                preferences: _preferences);
            _navigator.ImportVisitMemory(priorVisits);
            _navigator.ImportVisitCounts(priorCounts);
            _navigator.ImportBeatsSinceJump(priorDwell);

            if (_navigator.IsIdleWithoutLocks)
            {
                _playbackHost.DisarmAction();
                LoopEvent?.Invoke(this,
                    "Jukebox: random branches off and no locks — playing linearly" +
                    (_jukeboxSettings.Get().EnableEndLoop ? " (end loop still active if a guard edge exists)." : "."));

                // End-loop alone can still arm a late jump; plan it if possible.
                if (_jukeboxSettings.Get().EnableEndLoop && graph.LastBranchPointIndex >= 0)
                {
                    PlanAndArmJump(_navigator.FindBeatIndexAtMs(_lastPositionMs));
                    return;
                }

                return;
            }

            if (!_navigator.CanJump)
            {
                _playbackHost.DisarmAction();
                LoopEvent?.Invoke(this, "Jukebox: no similar-beat branches found in this track.");
                return;
            }

            PlanAndArmJump(_navigator.FindBeatIndexAtMs(_lastPositionMs));
        }

        private void PlanAndArmJump(int fromBeatIndex)
        {
            _plannedJump = _navigator.PlanNextJump(fromBeatIndex);

            if (_plannedJump == null)
            {
                _playbackHost.DisarmAction();
                LoopEvent?.Invoke(this, "Jukebox: no more branches ahead; playing out linearly.");
                return;
            }

            // If the trigger is already behind the playhead (common for end-loop escape after
            // overshooting the last branch point), fire on the next transport tick.
            var triggerMs = _plannedJump.TriggerMs;

            if (triggerMs <= _lastPositionMs)
                triggerMs = Math.Max(0, _lastPositionMs);

            _playbackHost.ArmAction(JukeboxActionId, triggerMs, _plannedJump.SeekToMs);
            LoopEvent?.Invoke(this,
                $"Jukebox: next jump at {FormatMs(triggerMs)} " +
                $"→ beat {_plannedJump.TargetBeatIndex} ({FormatMs(_plannedJump.SeekToMs)}).");

            RaiseJukeboxJump(_plannedJump, planned: true);
        }

        private bool _watchdogBusy;

        private void OnPositionUpdated(object sender, PositionSnapshot position)
        {
            if (position.TrackId != CurrentTrackId)
                return;

            _lastPositionMs = position.PositionMs;

            if (_watchdogBusy)
                return;

            // Watchdog: planned jump trigger is behind us but transport never fired.
            if (!IsLoopActive || ActiveProfile?.Mode != LoopModes.Jukebox || _plannedJump == null)
                return;

            if (position.Paused)
                return;

            var jump = _plannedJump;

            if (position.PositionMs + 40 < jump.TriggerMs)
                return;

            // More than ~120ms past the trigger with no ActionFired → force the seek once.
            if (position.PositionMs < jump.TriggerMs + 120)
                return;

            _watchdogBusy = true;

            try
            {
                // Clear first so a synchronous PositionUpdated from Seek cannot re-enter.
                _plannedJump = null;
                _playbackHost.DisarmAction();
                LoopEvent?.Invoke(this,
                    $"Jukebox: watchdog — forcing overdue jump {jump.FromBeatIndex} → {jump.TargetBeatIndex}.");
                _playbackHost.Seek(jump.SeekToMs);
                _lastPositionMs = jump.SeekToMs;

                LoopEvent?.Invoke(this,
                    $"Jukebox: jumped beat {jump.FromBeatIndex} → {jump.TargetBeatIndex}.");
                RaiseJukeboxJump(jump, planned: false);

                if (IsLoopActive && ActiveProfile.Mode == LoopModes.Jukebox && _navigator != null)
                    PlanAndArmJump(jump.TargetBeatIndex);
            }
            finally
            {
                _watchdogBusy = false;
            }
        }

        private void RaiseJukeboxJump(JukeboxJump jump, bool planned)
        {
            if (jump == null || _navigator?.Graph == null)
                return;

            var beats = _navigator.Graph.Beats;
            JukeboxJump?.Invoke(this, new JukeboxJumpEventArgs
            {
                FromBeatIndex = jump.FromBeatIndex,
                ToBeatIndex = jump.TargetBeatIndex,
                FromMs = beats[jump.FromBeatIndex].StartMs,
                ToMs = jump.SeekToMs,
                BranchDistance = jump.BranchDistance,
                IsPlanned = planned
            });
        }

        public void InvalidateGraphCache()
        {
            _graphCache.Clear();
            _navigator = null;
            _plannedJump = null;

            if (IsLoopActive && ActiveProfile.Mode == LoopModes.Jukebox)
                Rearm();
        }

        public void NotifyPlaybackSeek(long positionMs)
        {
            _lastPositionMs = Math.Max(0, positionMs);
            _plannedJump = null;
            _playbackHost.DisarmAction();

            // Scrub shortly after a hop = preference negative (Slice 6).
            _navigator?.NotifySkipAfterLastJump();

            if (!IsLoopActive)
                return;

            if (ActiveProfile.Mode == LoopModes.Jukebox)
            {
                // Preserve navigator state (branch chance / visit memory) but replan from scrub point.
                if (_navigator == null)
                    RearmJukebox();
                else
                    PlanAndArmJump(_navigator.FindBeatIndexAtMs(_lastPositionMs));
                return;
            }

            Rearm();
        }

        private void OnJukeboxActionFired(ArmedActionFiredEventArgs e)
        {
            if (e.ActionId != JukeboxActionId || _navigator == null)
                return;

            var jump = _plannedJump;

            if (jump != null)
            {
                var endLoopNote = ActiveProfile?.Mode == LoopModes.Jukebox &&
                                  _jukeboxSettings.Get().EnableEndLoop &&
                                  _navigator?.Graph != null &&
                                  jump.FromBeatIndex >= _navigator.Graph.LastBranchPointIndex &&
                                  jump.TargetBeatIndex < jump.FromBeatIndex
                    ? " (end-loop escape)"
                    : string.Empty;

                LoopEvent?.Invoke(this,
                    $"Jukebox: jumped beat {jump.FromBeatIndex} → {jump.TargetBeatIndex}.{endLoopNote}");
                RaiseJukeboxJump(jump, planned: false);
            }

            if (!IsLoopActive || ActiveProfile.Mode != LoopModes.Jukebox)
                return;

            PlanAndArmJump(jump?.TargetBeatIndex ?? _navigator.FindBeatIndexAtMs(e.SeekToMs));
        }

        public BeatGraph GetGraphForTrack(string trackId)
        {
            if (trackId == null)
                return null;

            if (_graphCache.TryGetValue(trackId, out var cached))
                return cached;

            var analysis = AnalysisCache.Load(trackId);

            if (analysis == null || analysis.Beats == null || analysis.Beats.Count == 0)
                return null;

            try
            {
                var graph = _graphBuilder.Build(analysis, _jukeboxSettings.Get());
                _graphCache[trackId] = graph;

                LoopEvent?.Invoke(this,
                    $"Jukebox: built beat graph — {graph.Beats.Count} beats, {graph.TotalBranchCount} branches " +
                    $"({graph.BranchableBeatCount} branchable, {graph.MetricMode}" +
                    (graph.UsedMutualKnn ? ", mutual-kNN" : "") +
                    (graph.ComponentCount > 0 ? $", {graph.ComponentCount} components" : "") +
                    (graph.BridgeEdgeCount > 0 ? $", {graph.BridgeEdgeCount} bridges" : "") +
                    $", threshold {graph.BranchDistanceThreshold:0.###}" +
                    (graph.LastBranchPointIndex >= 0
                        ? $", end-loop escape @ beat {graph.LastBranchPointIndex}"
                        : ", end-loop off") +
                    ").");

                return graph;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to build beat graph for {trackId}: {ex}");
                return null;
            }
        }

        private static string FormatMs(long ms)
        {
            var time = TimeSpan.FromMilliseconds(ms < 0 ? 0 : ms);
            return time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss\.f") : time.ToString(@"m\:ss\.f");
        }
    }
}
