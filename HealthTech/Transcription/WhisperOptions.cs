using Microsoft.Extensions.Options;

namespace HealthTech.Transcription
{
    public class WhisperOptions
    {
        public const string SectionName = "Whisper";

        // Relative paths are resolved against the application's content root (see Program.cs).
        public string ModelPath { get; set; } = "models/ggml-large-v3-turbo.bin";

        // Whisper language code (e.g. "ru", "en") or "auto" for automatic detection.
        public string Language { get; set; } = "auto";

        // Optional initial prompt: a short text in the target language (with domain vocabulary) that steers
        // spelling, diacritics and terminology. Empty = no prompt.
        public string Prompt { get; set; } = "";

        // Beam search width; 0 or 1 = greedy decoding (faster, less accurate).
        public int BeamSize { get; set; } = 5;

        // Don't feed previously decoded text back as context. Prevents repetition loops / hallucinations
        // spreading across 30 s windows, at the cost of a bit of cross-window consistency.
        public bool NoContext { get; set; } = true;

        /// <summary>
        /// Слово с вероятностью ниже этого значения помечается как неуверенно распознанное.
        /// 0.5 отобрано на глаз: ниже - список раздувается обычными словами, выше - пропускает термины.
        /// </summary>
        public double LowConfidenceThreshold { get; set; } = 0.5;

        // Run on the GPU when a GPU runtime (Vulkan) is loaded; false forces CPU inference.
        public bool UseGpu { get; set; } = true;

        // Index of the Vulkan GPU to use when several are available (1 = Arc A370M on the dev laptop, 0 = Iris Xe).
        // Overridden by the WHISPER_GPU_DEVICE env var; forced to 0 when GGML_VK_VISIBLE_DEVICES is set (see ResolveGpuDevice).
        public int GpuDevice { get; set; } = 1;

        // whisper.cpp flash attention: fewer, fused attention kernels, noticeably faster on Vulkan/Metal
        // and lighter on memory. Off by default only because very old drivers may lack the kernels.
        public bool UseFlashAttention { get; set; }

        // Forwards whisper.cpp/ggml native logs (incl. "ggml_vulkan: Found N Vulkan devices" and the chosen device)
        // to the app log at Debug verbosity. Enabled in appsettings.Development.json.
        public bool NativeLogging { get; set; }

        public const string GpuDeviceEnvVar = "WHISPER_GPU_DEVICE";
        public const string VulkanVisibleDevicesEnvVar = "GGML_VK_VISIBLE_DEVICES";

        // GGML_VK_VISIBLE_DEVICES filters the Vulkan devices ggml sees and renumbers the remaining ones from 0:
        // with GGML_VK_VISIBLE_DEVICES=1 only the Arc is visible and it becomes device 0, so GpuDevice=1 would point
        // at a non-existent device. When the variable is set it therefore wins and GpuDevice is 0 (the first visible
        // device); otherwise WHISPER_GPU_DEVICE, then Whisper:GpuDevice from config.
        public void ResolveGpuDevice()
        {
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(VulkanVisibleDevicesEnvVar)))
            {
                GpuDevice = 0;
            }
            else if (int.TryParse(Environment.GetEnvironmentVariable(GpuDeviceEnvVar), out var device))
            {
                GpuDevice = device;
            }
        }
    }

    // Runs at startup (ValidateOnStart), so a missing model fails the app instead of the first request.
    public class WhisperOptionsValidator : IValidateOptions<WhisperOptions>
    {
        public ValidateOptionsResult Validate(string? name, WhisperOptions options)
        {
            if (!File.Exists(options.ModelPath))
            {
                return ValidateOptionsResult.Fail(
                    $"Whisper model not found at '{options.ModelPath}'. Set '{WhisperOptions.SectionName}:ModelPath' to an existing ggml model file.");
            }

            return string.IsNullOrWhiteSpace(options.Language)
                ? ValidateOptionsResult.Fail($"'{WhisperOptions.SectionName}:Language' must be a language code or \"auto\".")
                : ValidateOptionsResult.Success;
        }
    }
}
