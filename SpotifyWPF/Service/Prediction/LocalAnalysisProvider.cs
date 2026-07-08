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
        private const string PythonOverrideEnvironmentVariable = "SPOTIFYWPF_PYTHON";

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

        public async Task<TrackAnalysis> GetAnalysisAsync(string trackId, IProgress<string> progress,
            CancellationToken cancellationToken)
        {
            var cached = AnalysisCache.Load(trackId);

            if (cached != null)
                return cached;

            await _analysisSemaphore.WaitAsync(cancellationToken);

            try
            {
                var wavPath = PredictionPaths.GetAudioCachePath(trackId);

                if (!File.Exists(wavPath))
                    wavPath = await CaptureTrackAsync(trackId, progress, cancellationToken);

                progress?.Report("Analyzing audio (librosa sidecar)…");

                var outputPath = PredictionPaths.GetAnalysisCachePath(trackId);
                PredictionPaths.EnsureDirectory(outputPath);

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
            };

            _playbackHost.TrackEnded += onTrackEnded;
            _playbackHost.StateChanged += onStateChanged;

            // A loop seeking around during the capture would corrupt the recording.
            _playbackHost.DisarmAction();

            _captureService.StartCapture(trackId);

            try
            {
                // Start capture slightly before play so the head of the track is never clipped.
                await Task.Delay(500, cancellationToken);

                await _playbackService.PlayTrackAsync(trackId, _playbackHost.DeviceId);

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
                        throw new TimeoutException("The track did not finish within the expected time.");
                }

                // Keep recording briefly past the end so the tail is intact.
                await Task.Delay(750, cancellationToken);

                progress?.Report("Finalizing capture…");

                return await _captureService.StopCaptureAsync();
            }
            catch
            {
                await _captureService.AbortCaptureAsync();
                throw;
            }
            finally
            {
                _playbackHost.TrackEnded -= onTrackEnded;
                _playbackHost.StateChanged -= onStateChanged;
            }
        }

        private static async Task RunSidecarAsync(string wavPath, string outputPath, string trackId,
            CancellationToken cancellationToken)
        {
            var scriptPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Tools", "analyze_track.py");

            if (!File.Exists(scriptPath))
                throw new FileNotFoundException($"Analysis sidecar not found at {scriptPath}.");

            var pythonPath = Environment.GetEnvironmentVariable(PythonOverrideEnvironmentVariable);

            if (string.IsNullOrWhiteSpace(pythonPath))
                pythonPath = "python";

            var startInfo = new ProcessStartInfo
            {
                FileName = pythonPath,
                Arguments = $"\"{scriptPath}\" \"{wavPath}\" \"{outputPath}\" --track-id \"{trackId}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };

            using (var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true })
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
                        $"(pip install librosa soundfile) or set {PythonOverrideEnvironmentVariable} " +
                        $"to the interpreter path. ({ex.Message})", ex);
                }

                var stderrTask = process.StandardError.ReadToEndAsync();
                var stdoutTask = process.StandardOutput.ReadToEndAsync();

                var finished = await Task.WhenAny(exitCompletion.Task,
                    Task.Delay(SidecarTimeout, cancellationToken));

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
                    var detail = string.IsNullOrWhiteSpace(stderr) ? "no error output" : stderr.Trim();
                    throw new InvalidOperationException($"Analysis sidecar failed (exit {process.ExitCode}): {detail}");
                }
            }

            if (!File.Exists(outputPath))
                throw new InvalidOperationException("The analysis sidecar exited successfully but wrote no output file.");
        }
    }
}
