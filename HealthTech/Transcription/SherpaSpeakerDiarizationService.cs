using HealthTech.Audio;
using Microsoft.Extensions.Options;
using SherpaOnnx;

namespace HealthTech.Transcription
{
    public interface ISpeakerDiarizationService
    {
        /// <summary>Splits 16 kHz mono float samples in [-1, 1] into speaker turns labelled "Speaker 1", "Speaker 2", ... in order of first appearance.</summary>
        Task<List<SpeakerSegment>> DiarizeAsync(float[] samples, CancellationToken cancellationToken = default);
    }

    // Singleton: the pyannote segmentation and speaker embedding models are loaded once.
    public sealed class SherpaSpeakerDiarizationService : ISpeakerDiarizationService, IDisposable
    {
        private readonly OfflineSpeakerDiarization _diarization;
        private readonly OfflineSpeakerDiarizationConfig _config;
        private readonly DiarizationOptions _options;
        private readonly ILogger<SherpaSpeakerDiarizationService> _logger;

        // The native diarization object is not documented as thread-safe.
        private readonly SemaphoreSlim _gate = new(1, 1);

        public SherpaSpeakerDiarizationService(IOptions<DiarizationOptions> options, ILogger<SherpaSpeakerDiarizationService> logger)
        {
            var o = options.Value;
            _options = o;
            _logger = logger;

            var config = new OfflineSpeakerDiarizationConfig();
            config.Segmentation.Pyannote.Model = o.SegmentationModelPath;
            config.Segmentation.NumThreads = o.NumThreads;
            config.Embedding.Model = o.EmbeddingModelPath;
            config.Embedding.NumThreads = o.NumThreads;
            config.Clustering.NumClusters = o.NumSpeakers;
            config.Clustering.Threshold = o.ClusterThreshold;
            config.MinDurationOn = o.MinDurationOn;
            config.MinDurationOff = o.MinDurationOff;
            _config = config;

            _logger.LogInformation("Loading diarization models {SegmentationModel} and {EmbeddingModel}",
                o.SegmentationModelPath, o.EmbeddingModelPath);
            try
            {
                _diarization = new OfflineSpeakerDiarization(config);
            }
            catch (Exception ex)
            {
                throw new AudioProcessingException(AudioProcessingError.ModelFailed,
                    $"Failed to load diarization models '{o.SegmentationModelPath}' / '{o.EmbeddingModelPath}': {ex.Message}", ex);
            }

            if (_diarization.SampleRate != AudioSampleReader.TargetSampleRate)
            {
                throw new AudioProcessingException(AudioProcessingError.ModelFailed,
                    $"Diarization models expect {_diarization.SampleRate} Hz audio, but the pipeline provides {AudioSampleReader.TargetSampleRate} Hz.");
            }
        }

        public async Task<List<SpeakerSegment>> DiarizeAsync(float[] samples, CancellationToken cancellationToken = default)
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                // Process is a blocking native call and cannot be interrupted once started.
                var segments = await Task.Run(() => _diarization.Process(samples), cancellationToken);

                if (_options.NumSpeakers == -1)
                {
                    var found = segments.Select(s => s.Speaker).Distinct().Count();
                    var clamped = Math.Clamp(found, _options.MinSpeakers, _options.MaxSpeakers);
                    if (found > 0 && clamped != found)
                    {
                        _logger.LogInformation("Auto clustering found {Found} speaker(s), outside [{Min}, {Max}]; re-running with {Clamped}",
                            found, _options.MinSpeakers, _options.MaxSpeakers, clamped);
                        segments = await Task.Run(() => ProcessWithFixedSpeakers(samples, clamped), cancellationToken);
                    }
                }

                // sherpa's cluster indices are arbitrary; number speakers by first appearance instead.
                var ordered = segments.OrderBy(s => s.Start).ToList();
                var labels = new Dictionary<int, string>();
                foreach (var segment in ordered)
                {
                    labels.TryAdd(segment.Speaker, $"Speaker {labels.Count + 1}");
                }

                var result = ordered
                    .Select(s => new SpeakerSegment(s.Start, s.End, labels[s.Speaker]))
                    .ToList();

                return _options.ExclusiveSegments ? MakeExclusive(result) : result;
            }
            catch (Exception ex) when (ex is not (OperationCanceledException or AudioProcessingException))
            {
                throw new AudioProcessingException(AudioProcessingError.ModelFailed, $"Speaker diarization failed: {ex.Message}", ex);
            }
            finally
            {
                _gate.Release();
            }
        }

        // Splits the timeline at every turn boundary; each piece goes to the shortest turn covering it, and
        // contiguous pieces of the same speaker are joined back. Silence (no turn) stays uncovered.
        private static List<SpeakerSegment> MakeExclusive(List<SpeakerSegment> segments)
        {
            var boundaries = segments.SelectMany(s => new[] { s.Start, s.End }).Distinct().Order().ToList();
            var result = new List<SpeakerSegment>();

            for (var i = 0; i < boundaries.Count - 1; i++)
            {
                var (from, to) = (boundaries[i], boundaries[i + 1]);
                var owner = segments
                    .Where(s => s.Start <= from && s.End >= to)
                    .MinBy(s => s.End - s.Start);
                if (owner is null)
                {
                    continue;
                }

                if (result.Count > 0 && result[^1].Speaker == owner.Speaker && result[^1].End == from)
                {
                    result[^1] = result[^1] with { End = to };
                }
                else
                {
                    result.Add(new SpeakerSegment(from, to, owner.Speaker));
                }
            }

            return result;
        }

        private OfflineSpeakerDiarizationSegment[] ProcessWithFixedSpeakers(float[] samples, int numSpeakers)
        {
            // SetConfig only updates clustering; the loaded models are kept.
            var fixedConfig = _config;
            fixedConfig.Clustering.NumClusters = numSpeakers;
            _diarization.SetConfig(fixedConfig);
            try
            {
                return _diarization.Process(samples);
            }
            finally
            {
                _diarization.SetConfig(_config);
            }
        }

        public void Dispose()
        {
            _diarization.Dispose();
            _gate.Dispose();
        }
    }
}
