using System;
using System.Collections.Generic;
using SpotifyWPF.Model.Prediction;
using SpotifyWPF.Service.Lyrics;
using SpotifyWPF.Service.Playback;

namespace SpotifyWPF.Service.Prediction
{
    public interface ILoopController
    {
        /// <summary>Profile driving the current track's loop, if any.</summary>
        LoopProfile ActiveProfile { get; }

        string CurrentTrackId { get; }

        /// <summary>Live random-branch probability from the armed navigator, if any.</summary>
        double? NavigatorBranchChance { get; }

        /// <summary>Beat index under the last reported playback position, if a navigator is armed.</summary>
        int? NavigatorBeatIndex { get; }

        /// <summary>True when a loop (simple or jukebox) is currently enforcing seeks.</summary>
        bool IsLoopActive { get; }

        /// <summary>Loads the stored profile for a track (or a fresh disabled one).</summary>
        LoopProfile GetProfileForTrack(string trackId);

        /// <summary>Persists the profile and (de)activates it when it belongs to the current track.</summary>
        void ApplyProfile(LoopProfile profile);

        /// <summary>Human-readable loop activity for the UI log ("armed", "jumped", …).</summary>
        event EventHandler<string> LoopEvent;

        /// <summary>Per-beat branch probability rolls and dwell (activity log Verbose filter).</summary>
        event EventHandler<string> LoopVerboseEvent;

        /// <summary>Raised when a jukebox jump is planned or performed (ring glow).</summary>
        event EventHandler<JukeboxJumpEventArgs> JukeboxJump;

        /// <summary>Raised whenever the active track or loop state changes (UI refresh).</summary>
        event EventHandler ActiveLoopChanged;

        void InvalidateGraphCache(bool rearmIfActive = true);

        /// <summary>
        /// After a user scrub/seek, drop the armed jump and replan from the new playhead so an
        /// old planned hop cannot override the scrub.
        /// </summary>
        void NotifyPlaybackSeek(long positionMs);

        /// <summary>Slice 6: how many labeled branch edges are stored.</summary>
        int PreferenceEdgeCount { get; }

        /// <summary>Slice 6: wipe pairwise preference memory.</summary>
        void ClearBranchPreferences();

        /// <summary>Per-beat energy controller state (tuning status line). Null while disabled.</summary>
        event EventHandler<EnergyPidSample> EnergyStateChanged;

        /// <summary>Cached per-beat energy curve for a track, or null when no analysis exists.</summary>
        BeatEnergyProfile GetEnergyProfileForTrack(string trackId);

        /// <summary>
        /// Supply lyric-flow Softmax context (phrase / section / block). Empty/null clears.
        /// Takes effect on the next jukebox rearm. Does not rebuild the beat graph.
        /// </summary>
        void SetLyricFlowContext(LyricFlowContext context);

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

        /// <summary>Lyric Softmax context; refreshed when lyrics / analysis sections load.</summary>
        private LyricFlowContext _lyricFlow = LyricFlowContext.Empty;

        /// <summary>Beat graphs are pure functions of the cached analysis; keep them per track.</summary>
        private readonly Dictionary<string, BeatGraph> _graphCache = new Dictionary<string, BeatGraph>();

        private BeatNavigator _navigator;

        private JukeboxJump _plannedJump;

        private long _lastPositionMs;

        private readonly Random _livelinessRandom = new Random();

        private readonly BeatEnergyProfileBuilder _energyBuilder = new BeatEnergyProfileBuilder();

        /// <summary>Energy curves are pure functions of the cached analysis; keep them per track.</summary>
        private readonly Dictionary<string, BeatEnergyProfile> _energyCache =
            new Dictionary<string, BeatEnergyProfile>();

        private EnergyPidController _energyPid;

        private SeekLeadCalibrator _seekLeadCalibrator;

        private int _lastPidBeat = -1;

        /// <summary>Controller output when the armed hop was planned — the replan hysteresis baseline.</summary>
        private double _outputAtPlan;

        private bool _gateActiveAtPlan;

        private int _replansThisHop;

        /// <summary>Single-roll liveliness: replan once at this beat before the armed hop fires.</summary>
        private bool _livelinessReconsiderScheduled;

        private int _livelinessReconsiderAtBeat = -1;

        public LoopProfile ActiveProfile { get; private set; }

        public string CurrentTrackId { get; private set; }

        /// <summary>Live random-branch probability from the armed navigator, if any.</summary>
        public double? NavigatorBranchChance => _navigator?.CurrentBranchChance;

        /// <summary>Beat index under the last reported playback position, if a navigator is armed.</summary>
        public int? NavigatorBeatIndex =>
            _navigator == null ? (int?)null : _navigator.FindBeatIndexAtMs(_lastPositionMs);

        public event EventHandler<string> LoopEvent;

        public event EventHandler<string> LoopVerboseEvent;

        public event EventHandler<JukeboxJumpEventArgs> JukeboxJump;

        public event EventHandler ActiveLoopChanged;

        public event EventHandler<EnergyPidSample> EnergyStateChanged;

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

            // New track = new energy curve; nothing about the old controller state transfers.
            _energyPid = null;
            _lastPidBeat = -1;
            _seekLeadCalibrator?.Reset();

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
            var settings = _jukeboxSettings.Get();
            var beatAtPosition = Math.Max(0, FindBeatIndexInGraph(graph, _lastPositionMs));

            EnsureEnergyController(settings, beatAtPosition);

            _navigator = new BeatNavigator(graph, settings, ActiveProfile,
                preferences: _preferences, lyricFlow: _lyricFlow,
                hopBias: (IHopBias)_energyPid ?? NullHopBias.Instance);
            _navigator.VerboseLogged += OnNavigatorVerboseLogged;
            _navigator.ImportVisitMemory(priorVisits);
            _navigator.ImportVisitCounts(priorCounts);
            _navigator.ImportBeatsSinceJump(priorDwell);

            if (_navigator.IsIdleWithoutLocks)
            {
                _playbackHost.DisarmAction();
                LoopEvent?.Invoke(this,
                    "Jukebox: random branches off and no locks — playing linearly" +
                    (_jukeboxSettings.Get().EnableEndLoop ? " (end loop still active if a guard edge exists)." : "."));

                // End-loop / exclusions can still arm a late jump; plan it if possible.
                if (_navigator.EffectiveLastBranchPoint >= 0 &&
                    (_jukeboxSettings.Get().EnableEndLoop ||
                     (ActiveProfile.ExcludedRanges != null && ActiveProfile.ExcludedRanges.Count > 0)))
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
            ClearLivelinessReconsideration();

            // Baseline for the replan hysteresis: how far u has moved since this hop was committed.
            _outputAtPlan = _energyPid?.Output ?? 0;
            _gateActiveAtPlan = _energyPid?.BuildupGateActive ?? false;
            _replansThisHop = 0;

            _plannedJump = _navigator.PlanNextJump(fromBeatIndex);

            if (_plannedJump == null)
            {
                _playbackHost.DisarmAction();
                LoopEvent?.Invoke(this, "Jukebox: no more branches ahead; playing out linearly.");
                return;
            }

            ArmCurrentPlannedJump(scheduleLiveliness: true);
        }

        private void ArmCurrentPlannedJump(bool scheduleLiveliness)
        {
            if (_plannedJump == null)
                return;

            // If the trigger is already behind the playhead (common for end-loop escape after
            // overshooting the last branch point), fire on the next transport tick.
            var triggerMs = _plannedJump.TriggerMs;

            if (triggerMs <= _lastPositionMs)
                triggerMs = Math.Max(0, _lastPositionMs);

            _playbackHost.ArmAction(JukeboxActionId, triggerMs, _plannedJump.SeekToMs);
            LoopEvent?.Invoke(this,
                $"Jukebox: next jump at {FormatMs(triggerMs)} " +
                $"→ beat {_plannedJump.TargetBeatIndex} ({FormatMs(_plannedJump.SeekToMs)}).");
            LoopVerboseEvent?.Invoke(this,
                $"Jukebox: armed beat {_plannedJump.FromBeatIndex} → {_plannedJump.TargetBeatIndex} · " +
                $"chance after plan {(_navigator?.CurrentBranchChance ?? 0) * 100:0.##}% " +
                "(no re-roll at trigger — plan simulates forward until a roll wins)");

            if (scheduleLiveliness)
                TryScheduleLivelinessReconsideration(_plannedJump);

            RaiseJukeboxJump(_plannedJump, planned: true);
        }

        private void ClearLivelinessReconsideration()
        {
            _livelinessReconsiderScheduled = false;
            _livelinessReconsiderAtBeat = -1;
        }

        /// <summary>
        /// One sparse roll per random hop: if it hits, replan once at a random beat before the trigger.
        /// Does not accumulate — unlike branch-probability ramp.
        /// </summary>
        private void TryScheduleLivelinessReconsideration(JukeboxJump planned)
        {
            ClearLivelinessReconsideration();

            if (planned == null || planned.Kind != JukeboxHopKind.Random || _navigator == null)
                return;

            if (ActiveProfile?.RandomBranches != true)
                return;

            var liveliness = Math.Max(0, Math.Min(1, _jukeboxSettings.Get().Liveliness));
            if (liveliness <= 0)
                return;

            var currentBeat = _navigator.FindBeatIndexAtMs(_lastPositionMs);
            var hopBeat = planned.FromBeatIndex;
            var span = hopBeat - currentBeat - 1;

            if (span <= 0)
                return;

            var roll = _livelinessRandom.NextDouble();
            if (roll >= liveliness)
            {
                LoopVerboseEvent?.Invoke(this,
                    $"Jukebox: liveliness roll {roll:0.###} ≥ {liveliness * 100:0.#}% — keeping planned hop");
                return;
            }

            _livelinessReconsiderAtBeat = currentBeat + 1 + _livelinessRandom.Next(span);
            _livelinessReconsiderScheduled = true;
            LoopVerboseEvent?.Invoke(this,
                $"Jukebox: liveliness roll {roll:0.###} < {liveliness * 100:0.#}% — " +
                $"will replan once at beat {_livelinessReconsiderAtBeat} " +
                $"(armed {planned.FromBeatIndex}→{planned.TargetBeatIndex})");
        }

        private void TryLivelinessReconsiderIfDue(PositionSnapshot position)
        {
            if (!_livelinessReconsiderScheduled || _navigator == null || _plannedJump == null || position.Paused)
                return;

            var beat = _navigator.FindBeatIndexAtMs(position.PositionMs);
            if (beat < _livelinessReconsiderAtBeat)
                return;

            ClearLivelinessReconsideration();

            // Shares the energy replan path so both routes roll back the plan-time visit count
            // identically. Liveliness does not count toward the energy replan cap — it is a single
            // roll that already fires at most once per armed hop.
            ReplanArmedHop(beat, "liveliness", countsTowardCap: false);
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
            {
                // Even without a planned jump, eject if scrubbed/played into an excluded span.
                if (IsLoopActive && ActiveProfile?.Mode == LoopModes.Jukebox &&
                    _navigator != null && !position.Paused)
                {
                    var beat = _navigator.FindBeatIndexAtMs(position.PositionMs);

                    // Keep the controller tracking even with nothing armed, so its state is warm
                    // (and its setpoint meaningful) by the time the next hop is planned.
                    TickEnergyController(beat);

                    if (_navigator.IsBeatExcluded(beat))
                        EjectFromExcludedRegion(beat);
                }

                return;
            }

            if (position.Paused)
                return;

            // If we somehow entered an excluded span, bail immediately.
            {
                var beat = _navigator.FindBeatIndexAtMs(position.PositionMs);
                if (_navigator.IsBeatExcluded(beat))
                {
                    EjectFromExcludedRegion(beat);
                    return;
                }

                // Reuse the beat index already computed above rather than recomputing it.
                // May replan the armed hop, so it runs before the trigger check below.
                TickEnergyController(beat);

                if (_plannedJump == null)
                    return;
            }

            TryLivelinessReconsiderIfDue(position);

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
                ClearLivelinessReconsideration();
                _playbackHost.DisarmAction();
                LoopEvent?.Invoke(this,
                    $"Jukebox: watchdog — forcing overdue jump {jump.FromBeatIndex} → {jump.TargetBeatIndex}.");
                _playbackHost.Seek(jump.SeekToMs);
                _lastPositionMs = jump.SeekToMs;

                _navigator?.NotifyJumpFired(jump.FromBeatIndex, jump.TargetBeatIndex);
                NotifyEnergyHopFired(jump);

                // Deliberately no seek-lead sample here: the watchdog fires precisely because the
                // transport did NOT report, so its timing says nothing about transport latency.

                LoopEvent?.Invoke(this,
                    $"Jukebox: jumped beat {jump.FromBeatIndex} → {jump.TargetBeatIndex}.");
                RaiseJukeboxJump(jump, planned: false);

                if (IsLoopActive && ActiveProfile?.Mode == LoopModes.Jukebox)
                    PlanAndArmJump(jump.TargetBeatIndex);
            }
            finally
            {
                _watchdogBusy = false;
            }
        }

        private void EjectFromExcludedRegion(int excludedBeat)
        {
            if (_watchdogBusy || _navigator == null)
                return;

            _watchdogBusy = true;

            try
            {
                var escapeFrom = excludedBeat;

                while (escapeFrom > 0 && _navigator.IsBeatExcluded(escapeFrom))
                    escapeFrom--;

                _plannedJump = null;
                _playbackHost.DisarmAction();
                PlanAndArmJump(escapeFrom);

                if (_plannedJump != null)
                {
                    LoopEvent?.Invoke(this,
                        $"Jukebox: excluded region — escaping beat {excludedBeat} via {_plannedJump.FromBeatIndex} → {_plannedJump.TargetBeatIndex}.");
                    _playbackHost.Seek(_plannedJump.SeekToMs);
                    _lastPositionMs = _plannedJump.SeekToMs;
                    _navigator.NotifyJumpFired(_plannedJump.FromBeatIndex, _plannedJump.TargetBeatIndex);
                    RaiseJukeboxJump(_plannedJump, planned: false);
                    var landed = _plannedJump.TargetBeatIndex;
                    _plannedJump = null;
                    PlanAndArmJump(landed);
                }
                else
                {
                    // No reverse edge — snap to the beat before the exclusion wall.
                    var wallMs = _navigator.Graph.Beats[escapeFrom].StartMs;
                    _playbackHost.Seek(wallMs);
                    _lastPositionMs = wallMs;
                    LoopEvent?.Invoke(this,
                        $"Jukebox: excluded region — snapped to beat {escapeFrom}.");
                }
            }
            finally
            {
                _watchdogBusy = false;
            }
        }

        /// <summary>Bleed the integral and reseed the measurement at the landing beat.</summary>
        private void NotifyEnergyHopFired(JukeboxJump jump)
        {
            if (_energyPid == null || jump == null)
                return;

            _energyPid.NotifyHopFired(jump.FromBeatIndex, jump.TargetBeatIndex);
            _energyPid.Reseed(jump.TargetBeatIndex);
            _lastPidBeat = -1;
        }

        /// <summary>
        /// Feed one fired hop to the seek-lead loop. The learned value goes into
        /// SeekLeadAutoTrimMs, never into the user's SeekLeadMs slider.
        /// </summary>
        private void UpdateSeekLeadCalibration(long plannedTriggerMs, long firedAtMs)
        {
            var settings = _jukeboxSettings.Get();

            if (!settings.SeekLeadAutoCalibrate)
                return;

            // Local WAV seeks in-process on the same tick, so the error is ~0 and the loop would
            // just be learning noise. Skip rather than special-case it inside the calibrator.
            if (string.Equals(settings.PlaybackSource, "Local", StringComparison.OrdinalIgnoreCase))
                return;

            if (_seekLeadCalibrator == null)
                _seekLeadCalibrator = new SeekLeadCalibrator(settings.SeekLeadAutoMaxMs);

            if (!_seekLeadCalibrator.NotifyHopFired(plannedTriggerMs, firedAtMs))
                return;

            settings.SeekLeadAutoTrimMs = _seekLeadCalibrator.TrimMs;
            LoopVerboseEvent?.Invoke(this,
                $"Jukebox: seek lead auto-trim → {_seekLeadCalibrator.TrimMs} ms " +
                $"(manual {settings.SeekLeadMs} ms, {_seekLeadCalibrator.SampleCount} samples)");
        }

        private void OnNavigatorVerboseLogged(object sender, string message) =>
            LoopVerboseEvent?.Invoke(this, message);

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

        public void InvalidateGraphCache(bool rearmIfActive = true)
        {
            _graphCache.Clear();
            _energyCache.Clear();
            _navigator = null;
            _plannedJump = null;
            _energyPid = null;
            _lastPidBeat = -1;

            if (rearmIfActive && IsLoopActive && ActiveProfile.Mode == LoopModes.Jukebox)
                Rearm();
        }

        public void NotifyPlaybackSeek(long positionMs)
        {
            _lastPositionMs = Math.Max(0, positionMs);
            _plannedJump = null;
            ClearLivelinessReconsideration();
            _playbackHost.DisarmAction();

            // Scrub shortly after a hop = preference negative (Slice 6).
            if (_navigator != null && _navigator.NotifySkipAfterLastJump())
            {
                LoopEvent?.Invoke(this,
                    "Jukebox: scrub after hop → preference negative " +
                    $"(window {_jukeboxSettings.Get().PreferenceSkipWindowMs}ms).");
            }

            if (!IsLoopActive)
                return;

            if (ActiveProfile.Mode == LoopModes.Jukebox)
            {
                // Preserve navigator state (branch chance / visit memory) but replan from scrub point.
                if (_navigator == null)
                {
                    RearmJukebox();
                }
                else
                {
                    var fromBeat = _navigator.FindBeatIndexAtMs(_lastPositionMs);
                    // Large seek / skip-ahead: clear recent-destination exhaustion so Softmax
                    // does not fall through to an end-segment-only loop.
                    _navigator.NotifySeekReplan(fromBeat);

                    // A scrub is a change of intent, so integral and derivative history are
                    // meaningless — full reset rather than the landing reseed a hop gets.
                    _energyPid?.Reset(fromBeat);
                    _lastPidBeat = -1;

                    PlanAndArmJump(fromBeat);
                }

                return;
            }

            Rearm();
        }

        public int PreferenceEdgeCount => _preferences.EdgeCount;

        public void ClearBranchPreferences()
        {
            _preferences.ClearAll();
            LoopEvent?.Invoke(this, "Jukebox: cleared branch preference memory.");
        }

        public void SetLyricFlowContext(LyricFlowContext context)
        {
            _lyricFlow = context ?? LyricFlowContext.Empty;

            if (IsLoopActive && ActiveProfile?.Mode == LoopModes.Jukebox)
                RearmJukebox();
        }

        private void OnJukeboxActionFired(ArmedActionFiredEventArgs e)
        {
            if (e.ActionId != JukeboxActionId || _navigator == null)
                return;

            var jump = _plannedJump;

            if (jump != null)
            {
                _navigator.NotifyJumpFired(jump.FromBeatIndex, jump.TargetBeatIndex);
                NotifyEnergyHopFired(jump);
                UpdateSeekLeadCalibration(jump.TriggerMs, e.FiredAtMs);

                var endLoopNote = ActiveProfile?.Mode == LoopModes.Jukebox &&
                                  _navigator != null &&
                                  jump.FromBeatIndex >= _navigator.EffectiveLastBranchPoint &&
                                  jump.TargetBeatIndex < jump.FromBeatIndex
                    ? (_jukeboxSettings.Get().EnableEndLoop ? " (end-loop escape)" : " (excluded-region escape)")
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
                    $"({graph.BranchableBeatCount} branchable, {(string.Equals(graph.MetricMode, "classic", StringComparison.OrdinalIgnoreCase) ? "enhanced" : graph.MetricMode)}" +
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
                LoopEvent?.Invoke(this, $"Jukebox: beat graph failed — {ex.GetBaseException().Message}");
                return null;
            }
        }

        public BeatEnergyProfile GetEnergyProfileForTrack(string trackId)
        {
            if (trackId == null)
                return null;

            if (_energyCache.TryGetValue(trackId, out var cached))
                return cached;

            var analysis = AnalysisCache.Load(trackId);

            if (analysis == null || analysis.Beats == null || analysis.Beats.Count == 0)
                return null;

            try
            {
                var profile = _energyBuilder.Build(analysis);

                if (profile == null)
                    return null;

                _energyCache[trackId] = profile;

                LoopEvent?.Invoke(this,
                    $"Jukebox: energy profile — {profile.BeatCount} beats, tier {profile.Tier} " +
                    $"({profile.SourceDescription}), median {profile.MedianEnergy01:0.00}.");

                return profile;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to build energy profile for {trackId}: {ex}");
                LoopEvent?.Invoke(this, $"Jukebox: energy profile failed — {ex.GetBaseException().Message}");
                return null;
            }
        }

        /// <summary>
        /// Create or refresh the energy controller. A settings edit reseeds rather than resets, so
        /// nudging a gain mid-song does not wipe the accumulated controller state.
        /// </summary>
        private void EnsureEnergyController(JukeboxSettings settings, int beatAtPosition)
        {
            if (!settings.EnergyControlEnabled)
            {
                _energyPid = null;
                _lastPidBeat = -1;
                return;
            }

            var profile = GetEnergyProfileForTrack(CurrentTrackId);

            if (profile == null || profile.BeatCount == 0)
            {
                _energyPid = null;
                _lastPidBeat = -1;
                return;
            }

            if (_energyPid == null)
            {
                _energyPid = new EnergyPidController(profile, settings);
                _energyPid.Reset(beatAtPosition);
                _lastPidBeat = -1;
            }
            else
            {
                _energyPid.Reseed(beatAtPosition);
            }
        }

        private static int FindBeatIndexInGraph(BeatGraph graph, long positionMs)
        {
            if (graph?.Beats == null || graph.Beats.Count == 0)
                return -1;

            for (var i = 0; i < graph.Beats.Count; i++)
            {
                if (positionMs < graph.Beats[i].EndMs)
                    return i;
            }

            return graph.Beats.Count - 1;
        }

        /// <summary>
        /// Advance the controller on beat crossings and decide whether the armed hop should be
        /// reconsidered. PositionUpdated fires roughly every 250 ms while a beat at 160 BPM lasts
        /// 375 ms, so this normally sees each beat once or twice — but a UI stall can skip several,
        /// hence the bounded catch-up.
        /// </summary>
        private void TickEnergyController(int beat)
        {
            if (_energyPid == null || beat < 0)
                return;

            // Backward hop / end-loop escape: a jump in time, not a gap to integrate through.
            if (beat < _lastPidBeat)
            {
                _energyPid.Reseed(beat);
                _lastPidBeat = beat;
                return;
            }

            var from = _lastPidBeat < 0 ? beat : Math.Max(_lastPidBeat + 1, beat - 8);
            EnergyPidSample sample = null;

            for (var b = from; b <= beat; b++)
                sample = _energyPid.UpdateForBeat(b);

            _lastPidBeat = beat;

            if (sample == null)
                return;

            EnergyStateChanged?.Invoke(this, sample);
            LoopVerboseEvent?.Invoke(this, FormatEnergySample(sample));

            TryEnergyReconsiderIfDue(beat, sample);
        }

        private static string FormatEnergySample(EnergyPidSample sample) =>
            $"Energy: beat {sample.BeatIndex} · y {sample.Measurement:0.00} r {sample.Setpoint:0.00} " +
            $"e {sample.Error:+0.00;-0.00} · P {sample.P:+0.00;-0.00} I {sample.I:+0.00;-0.00} " +
            $"D {sample.D:+0.00;-0.00} → u {sample.Output:+0.00;-0.00} · " +
            $"gate {(sample.GateActive ? "ON" : "off")} · tier {sample.Tier}";

        /// <summary>
        /// Energy-driven replan of an already-armed hop. Hops are planned ahead and armed, so this
        /// is the only way live energy can affect a decision that has already been committed.
        ///
        /// Unlike the liveliness roll, this is deliberate rather than random — a random re-roll is
        /// exactly as likely to swap a good hop for a bad one as the reverse. A gate that only just
        /// tripped bypasses the replan cap: a hop armed to fire in the middle of a riser we have
        /// just detected is the single highest-value replan available.
        /// </summary>
        private void TryEnergyReconsiderIfDue(int beat, EnergyPidSample sample)
        {
            if (_plannedJump == null || _navigator == null)
                return;

            if (_plannedJump.Kind != JukeboxHopKind.Random)
                return;

            if (ActiveProfile?.RandomBranches != true)
                return;

            var settings = _jukeboxSettings.Get();
            var gateFiredLate = sample.GateActive && !_gateActiveAtPlan;
            var hysteresis = Math.Max(0, settings.EnergyReplanHysteresis);
            var drifted = hysteresis > 0 && Math.Abs(sample.Output - _outputAtPlan) > hysteresis;

            if (!gateFiredLate && !(drifted && _replansThisHop < 2))
                return;

            var reason = gateFiredLate
                ? "buildup gate tripped after arming"
                : $"u drifted {_outputAtPlan:+0.00;-0.00} → {sample.Output:+0.00;-0.00}";

            ReplanArmedHop(beat, reason, countsTowardCap: !gateFiredLate);
        }

        /// <summary>
        /// Shared replan path for liveliness and energy: drop the armed hop, plan again from the
        /// current beat, and re-arm if the plan actually changed.
        /// </summary>
        private void ReplanArmedHop(int beat, string reason, bool countsTowardCap)
        {
            var previous = _plannedJump;

            if (previous == null || _navigator == null)
                return;

            var newJump = _navigator.PlanNextJump(beat);

            if (newJump == null)
            {
                LoopVerboseEvent?.Invoke(this,
                    $"Jukebox: replan at beat {beat} ({reason}) — no alternate; keeping " +
                    $"{previous.FromBeatIndex}→{previous.TargetBeatIndex}");
                return;
            }

            if (newJump.FromBeatIndex == previous.FromBeatIndex &&
                newJump.TargetBeatIndex == previous.TargetBeatIndex)
            {
                LoopVerboseEvent?.Invoke(this,
                    $"Jukebox: replan at beat {beat} ({reason}) — same hop " +
                    $"{previous.FromBeatIndex}→{previous.TargetBeatIndex}");
                return;
            }

            // The discarded plan already counted a visit at plan time; undo it so a destination
            // nobody ever heard does not accumulate a novelty penalty.
            _navigator.RollbackPlannedDestination(previous.TargetBeatIndex);

            if (countsTowardCap)
                _replansThisHop++;

            _plannedJump = newJump;
            LoopEvent?.Invoke(this,
                $"Jukebox: replanned hop {previous.FromBeatIndex}→{previous.TargetBeatIndex} " +
                $"to {newJump.FromBeatIndex}→{newJump.TargetBeatIndex} ({reason}).");

            _playbackHost.DisarmAction();
            ArmCurrentPlannedJump(scheduleLiveliness: false);
        }

        private static string FormatMs(long ms)
        {
            var time = TimeSpan.FromMilliseconds(ms < 0 ? 0 : ms);
            return time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss\.f") : time.ToString(@"m\:ss\.f");
        }
    }
}
