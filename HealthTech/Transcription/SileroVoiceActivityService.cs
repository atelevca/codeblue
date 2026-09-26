using HealthTech.Audio;
using Microsoft.Extensions.Options;
using SherpaOnnx;

namespace HealthTech.Transcription
{
    /// <summary>A contiguous span of audio to transcribe on its own. Times are in seconds from the start of the audio.</summary>
    public record SpeechChunk(double Start, double End);

    public interface IVoiceActivityService
    {
        /// <summary>
        /// Finds speech in 16 kHz mono float samples and groups adjacent speech regions into chunks of about
        /// <see cref="VadOptions.TargetChunkMin"/>..<see cref="VadOptions.TargetChunkMax"/> seconds (never over
        /// <see cref="VadOptions.MaxChunkDuration"/>), preferring pauses as boundaries. Chunks are padded by
        /// <see cref="VadOptions.ChunkOverlap"/>, so neighbours may overlap; long silence between chunks is left out.
        /// </summary>
        List<SpeechChunk> DetectChunks(float[] samples);
    }

    // The native detector is stateful (buffers audio), so a fresh one is created per call; the model is tiny.
    public class SileroVoiceActivityService : IVoiceActivityService
    {
        private readonly VadOptions _options;
        private readonly ILogger<SileroVoiceActivityService> _logger;

        public SileroVoiceActivityService(IOptions<VadOptions> options, ILogger<SileroVoiceActivityService> logger)
        {
            _options = options.Value;
            _logger = logger;
        }

        public List<SpeechChunk> DetectChunks(float[] samples)
        {
            var regions = DetectSpeech(samples)
                .SelectMany(r => SplitLongRegion(r, samples))
                .ToList();

            // A region joins the current chunk while the chunk stays within the target, or while the chunk is still
            // shorter than the target minimum and stays within the hard maximum. Otherwise the pause before it is the cut.
            var maxUnpadded = _options.MaxChunkDuration - _options.ChunkOverlap;
            var grouped = new List<SpeechChunk>();
            foreach (var region in regions)
            {
                if (grouped.Count > 0)
                {
                    var current = grouped[^1];
                    var merged = region.End - current.Start;
                    if (merged <= _options.TargetChunkMax
                        || (current.End - current.Start < _options.TargetChunkMin && merged <= maxUnpadded))
                    {
                        grouped[^1] = current with { End = region.End };
                        continue;
                    }
                }
                grouped.Add(region);
            }

            var duration = (double)samples.Length / AudioSampleReader.TargetSampleRate;
            var pad = _options.ChunkOverlap / 2;
            var chunks = grouped
                .Select(c => new SpeechChunk(Math.Max(0, c.Start - pad), Math.Min(duration, c.End + pad)))
                .ToList();

            _logger.LogInformation("VAD found {RegionCount} speech region(s), grouped into {ChunkCount} chunk(s) of {Durations} s",
                regions.Count, chunks.Count, string.Join(", ", chunks.Select(c => (c.End - c.Start).ToString("F1"))));
            return chunks;
        }

        // sherpa's MaxSpeechDuration only raises the threshold to look for a pause; it doesn't guarantee one, so
        // continuous talk can come back as a single region far over Whisper's 30 s window. Such regions are cut at
        // the quietest 100 ms frame between TargetChunkMin and TargetChunkMax from the piece start, to avoid cutting mid-word.
        private IEnumerable<SpeechChunk> SplitLongRegion(SpeechChunk region, float[] samples)
        {
            const int sampleRate = AudioSampleReader.TargetSampleRate;
            const int frame = sampleRate / 10;

            var start = region.Start;
            while (region.End - start > _options.TargetChunkMax)
            {
                var searchFrom = (int)((start + _options.TargetChunkMin) * sampleRate);
                var searchTo = Math.Min((int)((start + _options.TargetChunkMax) * sampleRate), samples.Length) - frame;

                var cut = searchTo;
                var minEnergy = double.MaxValue;
                for (var offset = searchFrom; offset <= searchTo; offset += frame / 2)
                {
                    var energy = 0.0;
                    for (var i = offset; i < offset + frame; i++)
                    {
                        energy += samples[i] * samples[i];
                    }
                    if (energy < minEnergy)
                    {
                        minEnergy = energy;
                        cut = offset + frame / 2;
                    }
                }

                var cutTime = (double)cut / sampleRate;
                yield return new SpeechChunk(start, cutTime);
                start = cutTime;
            }

            yield return new SpeechChunk(start, region.End);
        }

        private List<SpeechChunk> DetectSpeech(float[] samples)
        {
            const int sampleRate = AudioSampleReader.TargetSampleRate;

            var config = new VadModelConfig();
            config.SileroVad.Model = _options.ModelPath;
            config.SileroVad.Threshold = _options.Threshold;
            config.SileroVad.MinSilenceDuration = _options.MinSilenceDuration;
            config.SileroVad.MinSpeechDuration = _options.MinSpeechDuration;
            config.SileroVad.MaxSpeechDuration = _options.MaxSpeechDuration;
            config.SampleRate = sampleRate;

            var regions = new List<SpeechChunk>();
            try
            {
                using var vad = new VoiceActivityDetector(config, bufferSizeInSeconds: (float)samples.Length / sampleRate + 1);
                var windowSize = config.SileroVad.WindowSize;

                void Drain()
                {
                    while (!vad.IsEmpty())
                    {
                        var segment = vad.Front();
                        var start = (double)segment.Start / sampleRate;
                        regions.Add(new SpeechChunk(start, start + (double)segment.Samples.Length / sampleRate));
                        vad.Pop();
                    }
                }

                for (var offset = 0; offset + windowSize <= samples.Length; offset += windowSize)
                {
                    vad.AcceptWaveform(samples[offset..(offset + windowSize)]);
                    Drain();
                }
                vad.Flush();
                Drain();
            }
            catch (Exception ex)
            {
                throw new AudioProcessingException(AudioProcessingError.ModelFailed, $"Voice activity detection failed: {ex.Message}", ex);
            }

            return regions;
        }
    }
}
