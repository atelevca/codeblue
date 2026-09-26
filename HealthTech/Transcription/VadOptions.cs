using Microsoft.Extensions.Options;

namespace HealthTech.Transcription
{
    public class VadOptions
    {
        public const string SectionName = "Vad";

        // Silero VAD model. Relative paths are resolved against the application's content root (see Program.cs).
        public string ModelPath { get; set; } = "models/silero_vad.onnx";

        // Speech probability above which a frame counts as speech.
        public float Threshold { get; set; } = 0.5f;

        // Silence (s) needed to end a speech region; shorter speech (s) is dropped.
        public float MinSilenceDuration { get; set; } = 0.5f;
        public float MinSpeechDuration { get; set; } = 0.25f;

        // Passed to the Silero detector: past this length it looks harder for a pause to end a region (not a hard cap).
        public float MaxSpeechDuration { get; set; } = 20f;

        // Chunks sent to Whisper. Adjacent speech regions are grouped, cutting at pauses, aiming for
        // TargetChunkMin..TargetChunkMax seconds; a short chunk may grow up to MaxChunkDuration to reach the target.
        // Speech longer than TargetChunkMax without a pause is cut at the quietest 100 ms in the target range.
        // Chunks are transcribed independently, so a Whisper repetition loop can't spread past one chunk.
        public float TargetChunkMin { get; set; } = 15f;
        public float TargetChunkMax { get; set; } = 25f;
        public float MaxChunkDuration { get; set; } = 30f;

        // Each chunk is padded by half of this on both sides, so neighbouring chunks share ~this many seconds
        // and words at a cut aren't clipped. The padding counts towards MaxChunkDuration.
        public float ChunkOverlap { get; set; } = 0.5f;
    }

    // Runs at startup (ValidateOnStart), so a missing model fails the app instead of the first request.
    public class VadOptionsValidator : IValidateOptions<VadOptions>
    {
        public ValidateOptionsResult Validate(string? name, VadOptions options)
        {
            var failures = new List<string>();

            if (!File.Exists(options.ModelPath))
            {
                failures.Add($"VAD model not found at '{options.ModelPath}'. Set '{VadOptions.SectionName}:ModelPath' to an existing silero_vad.onnx file.");
            }
            if (options.Threshold is <= 0 or >= 1)
            {
                failures.Add($"'{VadOptions.SectionName}:Threshold' must be between 0 and 1.");
            }
            if (options.MaxSpeechDuration <= 0 || options.MaxSpeechDuration > 30)
            {
                failures.Add($"'{VadOptions.SectionName}:MaxSpeechDuration' must be in (0, 30] seconds (Whisper's window).");
            }
            if (options.TargetChunkMin <= 0 || options.TargetChunkMin >= options.TargetChunkMax)
            {
                failures.Add($"'{VadOptions.SectionName}:TargetChunkMin' must be > 0 and < 'TargetChunkMax'.");
            }
            if (options.ChunkOverlap < 0 || options.ChunkOverlap >= options.TargetChunkMin)
            {
                failures.Add($"'{VadOptions.SectionName}:ChunkOverlap' must be >= 0 and < 'TargetChunkMin'.");
            }
            if (options.MaxChunkDuration > 30 || options.TargetChunkMax + options.ChunkOverlap > options.MaxChunkDuration)
            {
                failures.Add($"'{VadOptions.SectionName}:MaxChunkDuration' must be <= 30 seconds (Whisper's window) and >= 'TargetChunkMax' + 'ChunkOverlap'.");
            }

            return failures.Count > 0 ? ValidateOptionsResult.Fail(failures) : ValidateOptionsResult.Success;
        }
    }
}
