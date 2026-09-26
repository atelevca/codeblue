using Microsoft.Extensions.Options;

namespace HealthTech.Transcription
{
    public class DiarizationOptions
    {
        public const string SectionName = "Diarization";

        // Relative paths are resolved against the application's content root (see Program.cs).
        public string SegmentationModelPath { get; set; } = "models/pyannote-segmentation-3.0.onnx";
        public string EmbeddingModelPath { get; set; } = "models/3dspeaker_speech_eres2net_base_sv_zh-cn_3dspeaker_16k.onnx";

        // Known number of speakers, or -1 to let clustering decide (using ClusterThreshold).
        public int NumSpeakers { get; set; } = -1;

        // Clustering (cosine) distance threshold used when NumSpeakers is -1. Smaller values yield more speakers.
        public float ClusterThreshold { get; set; } = 0.9f;

        // Bounds for auto clustering: if the threshold yields a speaker count outside [MinSpeakers, MaxSpeakers],
        // diarization is re-run with the count clamped into the range. Ignored when NumSpeakers is set.
        public int MinSpeakers { get; set; } = 2;
        public int MaxSpeakers { get; set; } = 6;

        // Speech shorter than MinDurationOn (s) is dropped; gaps shorter than MinDurationOff (s) are bridged.
        // Very short fragments give noisy embeddings that tend to become spurious speakers.
        public float MinDurationOn { get; set; } = 0.5f;
        public float MinDurationOff { get; set; } = 0.5f;

        // ONNX Runtime threads per model (segmentation and embedding). Using every logical core oversubscribes
        // the CPU (hyper-threads/E-cores, other processes, debugger) and can make diarization many times slower.
        public int NumThreads { get; set; } = 4;

        // Makes speaker turns non-overlapping: where turns overlap, the shorter (more specific) turn wins and the
        // longer one is cut around it. Without this, a long turn bridged across the other speaker's short turns
        // swallows them when transcript segments are assigned by overlap.
        public bool ExclusiveSegments { get; set; } = true;
    }

    // Runs at startup (ValidateOnStart), so a missing model fails the app instead of the first request.
    public class DiarizationOptionsValidator : IValidateOptions<DiarizationOptions>
    {
        public ValidateOptionsResult Validate(string? name, DiarizationOptions options)
        {
            var failures = new List<string>();

            if (!File.Exists(options.SegmentationModelPath))
            {
                failures.Add($"Speaker segmentation model not found at '{options.SegmentationModelPath}'. " +
                             $"Set '{DiarizationOptions.SectionName}:SegmentationModelPath' to an existing pyannote .onnx file.");
            }
            if (!File.Exists(options.EmbeddingModelPath))
            {
                failures.Add($"Speaker embedding model not found at '{options.EmbeddingModelPath}'. " +
                             $"Set '{DiarizationOptions.SectionName}:EmbeddingModelPath' to an existing .onnx file.");
            }
            if (options.NumSpeakers is 0 or < -1)
            {
                failures.Add($"'{DiarizationOptions.SectionName}:NumSpeakers' must be -1 (auto) or a positive number.");
            }
            if (options.MinSpeakers < 1 || options.MaxSpeakers < options.MinSpeakers)
            {
                failures.Add($"'{DiarizationOptions.SectionName}:MinSpeakers' must be >= 1 and <= 'MaxSpeakers'.");
            }
            if (options.MinDurationOn < 0 || options.MinDurationOff < 0)
            {
                failures.Add($"'{DiarizationOptions.SectionName}:MinDurationOn/MinDurationOff' must not be negative.");
            }
            if (options.NumThreads < 1)
            {
                failures.Add($"'{DiarizationOptions.SectionName}:NumThreads' must be at least 1.");
            }
            if (options.ClusterThreshold <= 0)
            {
                failures.Add($"'{DiarizationOptions.SectionName}:ClusterThreshold' must be greater than 0.");
            }

            return failures.Count > 0 ? ValidateOptionsResult.Fail(failures) : ValidateOptionsResult.Success;
        }
    }
}
