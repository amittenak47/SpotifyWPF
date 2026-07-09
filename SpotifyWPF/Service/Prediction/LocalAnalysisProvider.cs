using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SpotifyWPF.Model.Prediction;
using SpotifyWPF.Service.Audio;
using SpotifyWPF.Service.Playback;

namespace SpotifyWPF.Service.Prediction
{
    /// <summary>
    /// Path B: produces TrackAnalysis locally when Spotify's audio-analysis endpoint is gated off.
    /// Records one full play-through of the track via WASAPI loopback (system mix — other apps should
    /// stay silent), then runs the librosa sidecar (Tools\analyze_track.py) to emit analysis JSON in
    /// the shared schema. The WAV and JSON are cached so each track is captured at most once.
    /// </summary>
    public class LocalAnalysisProvider : ITrackAnalysisProvider
    {
        private static readonly TimeSpan SidecarTimeout = TimeSpan.FromMinutes(10);

        /// <summary>Extra wait beyond the track duration before giving up on the capture pass.</summary>
        private static readonly TimeSpan CaptureGrace = TimeSpan.FromSeconds(45);

        private readonly IWebPlaybackHost _playbackHost;

        private readonly ISpotifyPlaybackService _playbackService;

        private readonly IAudioCaptureService _captureService;

        private readonly SemaphoreSlim _analysisSemaphore = new SemaphoreSlim(1, 1);

        public LocalAnalysisProvider(IWebPlaybackHost playbackHost, ISpotifyPlaybackService playbackService,
            IAudioCaptureService captureService)
        {
            _playbackHost = playbackHost;
            _playbackService = playbackService;
            _captureService = captureService;
        }

        public AnalysisSource Source => AnalysisSource.Local;

        public bool IsCached(string trackId)
        {
            return AnalysisCache.Exists(trackId);
        }

        public bool RequiresPlaybackCapture(string trackId)
        {
            if (IsCached(trackId))
                return false;

            return !WavCaptureValidator.HasCompleteCapture(trackId);
        }

        public async Task<TrackAnalysis> GetAnalysisAsync(string trackId, IProgress<string> progress,
            CancellationToken cancellationToken)
        {
            var cached = AnalysisCache.Load(trackId);

            if (cached != null)
                return cached;

            await _analysisSemaphore.WaitAsync(cancellationToken);

            try
            {
                var wavPath = PredictionPaths.ResolveAudioCachePath(trackId);

                if (!WavCaptureValidator.HasCompleteCapture(trackId))
                {
                    if (File.Exists(wavPath))
                    {
                        progress?.Report("Discarding incomplete capture — re-recording from the start…");
                        WavCaptureValidator.TryDeleteCapture(trackId);
                    }

                    wavPath = await CaptureTrackAsync(trackId, progress, cancellationToken);
                }
                else if (!string.Equals(wavPath, PredictionPaths.GetAudioCachePath(trackId), StringComparison.OrdinalIgnoreCase))
                {
                    progress?.Report($"Using cached capture: {wavPath}");
                }

                if (!WavCaptureValidator.HasCompleteCapture(trackId))
                {
                    throw new FileNotFoundException(
                        $"No complete captured WAV found for track {trackId}. Looked in {PredictionPaths.AudioCacheDirectory}. " +
                        "Re-run Analyze and let the track play from start to end.");
                }

                progress?.Report("Analyzing audio (librosa sidecar)…");

                var outputPath = PredictionPaths.GetAnalysisCachePath(trackId);
                PredictionPaths.EnsureDirectory(outputPath);

                if (File.Exists(outputPath))
                {
                    try
                    {
                        File.Delete(outputPath);
                    }
                    catch (IOException ex)
                    {
                        Console.WriteLine($"Could not clear prior analysis output: {ex.Message}");
                    }
                }

                await RunSidecarAsync(wavPath, outputPath, trackId, cancellationToken);

                var analysis = AnalysisCache.Load(trackId);

                if (analysis == null)
                    throw new InvalidOperationException("The analysis sidecar did not produce readable output.");

                analysis.TrackId = trackId;
                analysis.SourceType = "local";
                AnalysisCache.Save(analysis);

                progress?.Report("Analysis cached.");

                return analysis;
            }
            catch (OperationCanceledException)
            {
                await _captureService.AbortCaptureAsync();
                WavCaptureValidator.TryDeleteCapture(trackId);
                AnalysisCache.Delete(trackId);
                throw;
            }
            finally
            {
                _analysisSemaphore.Release();
            }
        }

        /// <summary>Plays the track once from the top in the SDK player while recording the system mix.</summary>
        private async Task<string> CaptureTrackAsync(string trackId, IProgress<string> progress,
            CancellationToken cancellationToken)
        {
            if (!_playbackHost.IsReady)
                throw new InvalidOperationException("The embedded player is not ready.");

            progress?.Report("Capturing one play-through — mute other apps until the track ends…");

            var endedCompletion = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            long durationMs = 0;

            EventHandler<string> onTrackEnded = (_, endedTrackId) =>
            {
                if (endedTrackId == trackId)
                    endedCompletion.TrySetResult(null);
            };

            EventHandler<PlayerStateSnapshot> onStateChanged = (_, state) =>
            {
                if (state.TrackId == trackId && state.DurationMs > 0)
                    Interlocked.CompareExchange(ref durationMs, state.DurationMs, 0);

                TryCompleteCaptureAtEnd(state.TrackId, state.PositionMs, state.DurationMs, state.Paused);
            };

            var lastReportedPercent = -1;

            EventHandler<PositionSnapshot> onPositionUpdated = (_, position) =>
            {
                var duration = Interlocked.Read(ref durationMs);

                if (position.TrackId == trackId && duration > 0)
                {
                    var percent = (int)Math.Min(99, position.PositionMs * 100.0 / duration);

                    if (percent >= lastReportedPercent + 2 || percent >= 95)
                    {
                        lastReportedPercent = percent;
                        progress?.Report($"Capturing play-through… {percent}%");
                    }
                }

                TryCompleteCaptureAtEnd(position.TrackId, position.PositionMs, duration, position.Paused);
            };

            void TryCompleteCaptureAtEnd(string currentTrackId, long positionMs, long trackDurationMs, bool paused)
            {
                if (currentTrackId != trackId)
                    return;

                var duration = trackDurationMs > 0 ? trackDurationMs : Interlocked.Read(ref durationMs);

                if (duration <= 0)
                    return;

                // SDK often stops at the end timestamp instead of reporting paused @ position 0.
                if (positionMs >= duration - 1500 || (paused && positionMs >= duration - 5000))
                    endedCompletion.TrySetResult(null);
            }

            _playbackHost.TrackEnded += onTrackEnded;
            _playbackHost.StateChanged += onStateChanged;
            _playbackHost.PositionUpdated += onPositionUpdated;

            // A loop seeking around during the capture would corrupt the recording.
            _playbackHost.DisarmAction();
            _playbackHost.Pause();
            _playbackHost.Seek(0);

            try
            {
                await _playbackService.PauseAsync(_playbackHost.DeviceId);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Pre-capture pause failed: {ex.Message}");
            }

            WavCaptureValidator.TryDeleteCapture(trackId);
            _captureService.StartCapture(trackId);

            try
            {
                // Start capture slightly before play so the head of the track is never clipped.
                await Task.Delay(500, cancellationToken);

                await _playbackService.PlayTrackAsync(trackId, _playbackHost.DeviceId, positionMs: 0);
                await WaitForPlaybackStartedAsync(trackId, cancellationToken);

                // Wait for the natural end of the track, re-checking the timeout as the duration arrives.
                var waited = TimeSpan.Zero;
                var slice = TimeSpan.FromSeconds(1);

                while (!endedCompletion.Task.IsCompleted)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    await Task.WhenAny(endedCompletion.Task, Task.Delay(slice, cancellationToken));
                    waited += slice;

                    var currentDuration = Interlocked.Read(ref durationMs);
                    var limit = (currentDuration > 0
                                    ? TimeSpan.FromMilliseconds(currentDuration)
                                    : TimeSpan.FromMinutes(12)) + CaptureGrace;

                    if (waited > limit)
                    {
                        throw new TimeoutException(
                            $"The track did not finish within the expected time " +
                            $"(waited {waited.TotalSeconds:0}s, limit {limit.TotalSeconds:0}s, " +
                            $"duration {currentDuration}ms). The player may not have reported track end — " +
                            "try Analyze track again after this fix, or delete any partial .wav in audio-cache.");
                    }
                }

                // Keep recording briefly past the end so the tail is intact.
                await Task.Delay(750, cancellationToken);

                progress?.Report("Finalizing capture…");

                var trackDurationMs = Interlocked.Read(ref durationMs);
                return await _captureService.StopCaptureAsync(trackDurationMs);
            }
            catch (OperationCanceledException)
            {
                await _captureService.AbortCaptureAsync();
                WavCaptureValidator.TryDeleteCapture(trackId);
                throw;
            }
            catch
            {
                await _captureService.AbortCaptureAsync();
                WavCaptureValidator.TryDeleteCapture(trackId);
                throw;
            }
            finally
            {
                _playbackHost.TrackEnded -= onTrackEnded;
                _playbackHost.StateChanged -= onStateChanged;
                _playbackHost.PositionUpdated -= onPositionUpdated;
            }
        }

        private async Task WaitForPlaybackStartedAsync(string trackId, CancellationToken cancellationToken)
        {
            var started = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            var deadline = DateTime.UtcNow.AddSeconds(20);

            void OnState(object sender, PlayerStateSnapshot state)
            {
                if (state.TrackId == trackId && state.DurationMs > 0 && !state.Paused)
                    started.TrySetResult(null);
            }

            _playbackHost.StateChanged += OnState;

            try
            {
                while (DateTime.UtcNow < deadline)
                {
                    if (started.Task.IsCompleted)
                        return;

                    await Task.WhenAny(started.Task, Task.Delay(250, cancellationToken));
                    cancellationToken.ThrowIfCancellationRequested();
                }

                throw new InvalidOperationException(
                    "The embedded player did not start playback for capture. Confirm Spotify Premium login, " +
                    "that Loop Lab is the active device, and that Windows can hear the player.");
            }
            finally
            {
                _playbackHost.StateChanged -= OnState;
            }
        }

        private static async Task RunSidecarAsync(string wavPath, string outputPath, string trackId,
            CancellationToken cancellationToken)
        {
            var scriptPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Tools", "analyze_track.py");

            if (!File.Exists(scriptPath))
                throw new FileNotFoundException($"Analysis sidecar not found at {scriptPath}.");

            var arguments =
                $"\"{scriptPath}\" \"{wavPath}\" \"{outputPath}\" --track-id \"{trackId}\"";

            var startInfo = PythonLauncher.CreateSidecarStartInfo(arguments);

            try
            {
                using (var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true })
                using (cancellationToken.Register(() =>
                {
                    try
                    {
                        if (!process.HasExited)
                            process.Kill();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Could not kill analysis sidecar: {ex.Message}");
                    }
                }))
                {
                    var exitCompletion = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
                    process.Exited += (_, __) => exitCompletion.TrySetResult(null);

                    try
                    {
                        process.Start();
                    }
                    catch (Exception ex)
                    {
                        throw new InvalidOperationException(
                            "Could not start Python for local analysis. Install Python 3 with librosa " +
                            "(pip install librosa soundfile), then set the Python path on the Loop Lab page " +
                            "or use Auto-detect. Microsoft Store Python must run via the py launcher " +
                            "(Auto-detect handles this). If you set the path manually, use py:-3.12 or a " +
                            $"non-Store python.exe under Local\\Programs\\Python. ({ex.Message})", ex);
                    }

                    var stderrTask = process.StandardError.ReadToEndAsync();
                    var stdoutTask = process.StandardOutput.ReadToEndAsync();

                    var finished = await Task.WhenAny(exitCompletion.Task,
                        Task.Delay(SidecarTimeout, cancellationToken));

                    cancellationToken.ThrowIfCancellationRequested();

                    if (finished != exitCompletion.Task)
                    {
                        try
                        {
                            process.Kill();
                        }
                        catch (InvalidOperationException)
                        {
                        }

                        throw new TimeoutException("The analysis sidecar timed out.");
                    }

                    var stderr = await stderrTask;
                    await stdoutTask;

                    if (process.ExitCode != 0)
                    {
                        TryDeletePartialOutput(outputPath);

                        var detail = string.IsNullOrWhiteSpace(stderr) ? "no error output" : stderr.Trim();

                        if (detail.IndexOf("Input audio is empty", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            WavCaptureValidator.TryDeleteCapture(trackId);
                            throw new InvalidOperationException(
                                "Captured audio was empty. The previous WAV was removed — analyze again with the Loop Lab " +
                                "player audible in Windows (volume up, correct output device, not muted in Volume Mixer).");
                        }

                        throw new InvalidOperationException($"Analysis sidecar failed (exit {process.ExitCode}): {detail}");
                    }
                }

                if (!File.Exists(outputPath))
                    throw new InvalidOperationException("The analysis sidecar exited successfully but wrote no output file.");
            }
            catch (OperationCanceledException)
            {
                TryDeletePartialOutput(outputPath);
                throw;
            }
        }

        private static void TryDeletePartialOutput(string outputPath)
        {
            if (string.IsNullOrWhiteSpace(outputPath) || !File.Exists(outputPath))
                return;

            try
            {
                File.Delete(outputPath);
            }
            catch (IOException ex)
            {
                Console.WriteLine($"Could not delete partial analysis output: {ex.Message}");
            }
        }
    }
}
