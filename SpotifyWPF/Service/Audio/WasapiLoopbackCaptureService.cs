using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using SpotifyWPF.Service.Prediction;

namespace SpotifyWPF.Service.Audio
{
    public interface IAudioCaptureService
    {
        bool IsCapturing { get; }

        /// <summary>
        /// Starts recording the default render device (system mix) into
        /// Prediction\audio-cache\{trackId}.wav. Mute other apps during the capture pass.
        /// </summary>
        void StartCapture(string trackId);

        /// <summary>Stops recording, finalizes the WAV and its metadata sidecar, and returns the WAV path.</summary>
        Task<string> StopCaptureAsync();

        /// <summary>Discards an in-flight capture (e.g. playback failed mid-recording).</summary>
        Task AbortCaptureAsync();
    }

    /// <summary>
    /// WASAPI loopback capture (AUDCLNT_STREAMFLAGS_LOOPBACK via NAudio): records whatever Windows is
    /// playing while a track plays once, for offline analysis only. Playback itself stays in the
    /// WebView2 SDK player; fidelity is bounded by Spotify's stream, which is plenty for beat
    /// tracking, chroma/MFCC and loop points.
    /// </summary>
    public class WasapiLoopbackCaptureService : IAudioCaptureService
    {
        private readonly object _lock = new object();

        private WasapiLoopbackCapture _capture;

        private WaveFileWriter _writer;

        private WaveFormat _writeFormat;

        private bool _convertFloatToPcm16;

        private TaskCompletionSource<object> _stoppedCompletion;

        private string _trackId;

        private string _wavPath;

        private Exception _captureError;

        public bool IsCapturing
        {
            get
            {
                lock (_lock)
                {
                    return _capture != null;
                }
            }
        }

        public void StartCapture(string trackId)
        {
            if (string.IsNullOrEmpty(trackId))
                throw new ArgumentNullException(nameof(trackId));

            lock (_lock)
            {
                if (_capture != null)
                    throw new InvalidOperationException("A capture is already in progress.");

                var wavPath = PredictionPaths.GetAudioCachePath(trackId);
                PredictionPaths.EnsureDirectory(wavPath);

                var capture = CreateLoopbackCapture();
                var captureFormat = capture.WaveFormat;

                if (captureFormat.Encoding == WaveFormatEncoding.IeeeFloat && captureFormat.BitsPerSample == 32)
                {
                    _convertFloatToPcm16 = true;
                    _writeFormat = new WaveFormat(captureFormat.SampleRate, 16, captureFormat.Channels);
                }
                else
                {
                    _convertFloatToPcm16 = false;
                    _writeFormat = captureFormat;
                }

                _writer = new WaveFileWriter(wavPath, _writeFormat);
                _trackId = trackId;
                _wavPath = wavPath;
                _captureError = null;
                _stoppedCompletion = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);

                capture.DataAvailable += OnDataAvailable;
                capture.RecordingStopped += OnRecordingStopped;

                _capture = capture;

                capture.StartRecording();
            }
        }

        public async Task<string> StopCaptureAsync()
        {
            WasapiLoopbackCapture capture;
            TaskCompletionSource<object> stoppedCompletion;
            string trackId;
            string wavPath;

            lock (_lock)
            {
                capture = _capture;
                stoppedCompletion = _stoppedCompletion;
                trackId = _trackId;
                wavPath = _wavPath;
            }

            if (capture == null)
                return null;

            var format = _writeFormat ?? capture.WaveFormat;

            capture.StopRecording();

            await stoppedCompletion.Task;

            if (_captureError != null)
                throw new InvalidOperationException($"Audio capture failed: {_captureError.Message}", _captureError);

            WriteMetadata(trackId, wavPath, format);

            return wavPath;
        }

        public async Task AbortCaptureAsync()
        {
            WasapiLoopbackCapture capture;
            TaskCompletionSource<object> stoppedCompletion;
            string wavPath;

            lock (_lock)
            {
                capture = _capture;
                stoppedCompletion = _stoppedCompletion;
                wavPath = _wavPath;
            }

            if (capture == null)
                return;

            capture.StopRecording();

            try
            {
                await stoppedCompletion.Task;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Capture abort: {ex.Message}");
            }

            try
            {
                if (wavPath != null && File.Exists(wavPath))
                    File.Delete(wavPath);
            }
            catch (IOException ex)
            {
                Console.WriteLine($"Could not delete aborted capture file: {ex.Message}");
            }
        }

        private static WasapiLoopbackCapture CreateLoopbackCapture()
        {
            // Event-driven capture on the default multimedia render endpoint — fewer dropouts than
            // the default poll-based ctor and uses the device's native mix format (often 48 kHz).
            using (var enumerator = new MMDeviceEnumerator())
            {
                var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                return new WasapiLoopbackCapture(device);
            }
        }

        private void OnDataAvailable(object sender, WaveInEventArgs e)
        {
            lock (_lock)
            {
                if (_writer == null)
                    return;

                if (_convertFloatToPcm16)
                {
                    var floatCount = e.BytesRecorded / 4;
                    var pcm16 = new byte[floatCount * 2];

                    for (var i = 0; i < floatCount; i++)
                    {
                        var sample = BitConverter.ToSingle(e.Buffer, i * 4);
                        sample = Math.Max(-1f, Math.Min(1f, sample));
                        var value = (short)(sample * 32767f);
                        pcm16[i * 2] = (byte)(value & 0xFF);
                        pcm16[i * 2 + 1] = (byte)((value >> 8) & 0xFF);
                    }

                    _writer.Write(pcm16, 0, pcm16.Length);
                    return;
                }

                _writer.Write(e.Buffer, 0, e.BytesRecorded);
            }
        }

        private void OnRecordingStopped(object sender, StoppedEventArgs e)
        {
            TaskCompletionSource<object> stoppedCompletion;

            lock (_lock)
            {
                _captureError = e.Exception;

                _writer?.Dispose();
                _writer = null;

                _capture?.Dispose();
                _capture = null;

                stoppedCompletion = _stoppedCompletion;
                _stoppedCompletion = null;
            }

            stoppedCompletion?.TrySetResult(null);
        }

        private static void WriteMetadata(string trackId, string wavPath, WaveFormat format)
        {
            try
            {
                var metadataPath = PredictionPaths.GetAudioCacheMetadataPath(trackId);

                var metadata = new CaptureMetadata
                {
                    TrackId = trackId,
                    WavPath = Path.GetFileName(wavPath),
                    SampleRate = format.SampleRate,
                    Channels = format.Channels,
                    BitsPerSample = format.BitsPerSample,
                    Encoding = format.Encoding.ToString(),
                    CapturedAtUtc = DateTime.UtcNow
                };

                File.WriteAllText(metadataPath,
                    JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to write capture metadata: {ex.Message}");
            }
        }

        private class CaptureMetadata
        {
            [JsonPropertyName("trackId")]
            public string TrackId { get; set; }

            [JsonPropertyName("wavFile")]
            public string WavPath { get; set; }

            [JsonPropertyName("sampleRate")]
            public int SampleRate { get; set; }

            [JsonPropertyName("channels")]
            public int Channels { get; set; }

            [JsonPropertyName("bitsPerSample")]
            public int BitsPerSample { get; set; }

            [JsonPropertyName("encoding")]
            public string Encoding { get; set; }

            [JsonPropertyName("capturedAtUtc")]
            public DateTime CapturedAtUtc { get; set; }
        }
    }
}
