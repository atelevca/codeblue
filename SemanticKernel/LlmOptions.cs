namespace SemanticKernel
{
    /// <summary>Which pipeline stage a model serves. The two stages may run different GGUF files.</summary>
    public enum LlmModelRole
    {
        /// <summary>Term correction: many short, narrow requests - a small model is enough.</summary>
        Correction,

        /// <summary>Minutes extraction and verification: one long request each - the bigger model.</summary>
        Minutes
    }

    /// <summary>What <see cref="KernelFactory"/> needs to load one model.</summary>
    public sealed record LlmModelSettings(string ModelFile, uint ContextSize, int GpuLayerCount);

    /// <summary>
    /// Overrides for the minutes model (section <c>Llm:Minutes</c>). Every null field falls back to the
    /// correction model's value, so an empty section means "the same model for both stages".
    /// </summary>
    public class LlmModelOverrides
    {
        public string? ModelFile { get; set; }
        public uint? ContextSize { get; set; }
        public int? GpuLayerCount { get; set; }
    }

    /// <summary>Settings of the local LLM (section <c>Llm</c>, defaults in <c>appsettings.llm.json</c>).</summary>
    public class LlmOptions
    {
        public const string SectionName = "Llm";

        /// <summary>
        /// Folder with the GGUF models. Relative: looked up from the app's base directory and each of its parents
        /// (so <c>../models</c> finds <c>&lt;SolutionRoot&gt;/models</c> from the project folder and from <c>bin/</c>).
        /// Absolute: used as-is.
        /// </summary>
        public string ModelsDirectory { get; set; } = "../models";

        /// <summary>GGUF file of the correction model inside <see cref="ModelsDirectory"/>, or an absolute path.</summary>
        public string ModelFile { get; set; } = "qwen2.5-3b-instruct-q4_k_m.gguf";

        public uint ContextSize { get; set; } = 8192;

        /// <summary>
        /// Layers offloaded to the GPU (Vulkan backend); 0 keeps the model on the CPU. The GPU is shared with
        /// Whisper large-v3, and on the 4 GB Arc A370M 12 layers of the 3B made Whisper 2.6x slower (the process
        /// spilled into shared memory), which cost more than the correction step gained - hence 0 by default.
        /// </summary>
        public int GpuLayerCount { get; set; }

        /// <summary>Model of the minutes stages; null or empty = the correction model, loaded once and shared.</summary>
        public LlmModelOverrides? Minutes { get; set; }

        public double Temperature { get; set; }

        public int MaxTokens { get; set; } = 2048;

        /// <summary>Max segments per LLM request.</summary>
        public int BatchSize { get; set; } = 15;

        /// <summary>
        /// Max total text length per LLM request; a batch is closed earlier when it would exceed this,
        /// so the corrected JSON fits into <see cref="MaxTokens"/>. A single longer segment is sent alone.
        /// </summary>
        public int MaxBatchCharacters { get; set; } = 4000;

        /// <summary>
        /// Longer segment texts are split into pieces of whole sentences up to this length; each piece is sent,
        /// validated and logged on its own and the pieces are glued back into the segment.
        /// </summary>
        public int MaxPieceCharacters { get; set; } = 300;

        /// <summary>Max length of the glossary lines added to one request, per glossary file.</summary>
        public int MaxGlossaryCharacters { get; set; } = 1000;

        /// <summary>Pieces from the end of the previous batch sent as read-only context.</summary>
        public int ContextSegments { get; set; } = 3;

        /// <summary>Extra attempts per batch when the model output can't be parsed or has wrong ids.</summary>
        public int MaxRetries { get; set; } = 1;

        /// <summary>The model settings of a stage, with the minutes overrides applied.</summary>
        public LlmModelSettings ModelFor(LlmModelRole role) => role switch
        {
            LlmModelRole.Minutes => new LlmModelSettings(
                string.IsNullOrWhiteSpace(Minutes?.ModelFile) ? ModelFile : Minutes.ModelFile,
                Minutes?.ContextSize ?? ContextSize,
                Minutes?.GpuLayerCount ?? GpuLayerCount),
            _ => new LlmModelSettings(ModelFile, ContextSize, GpuLayerCount)
        };

        /// <summary>
        /// Full path of a model file. Walks up from <paramref name="baseDirectory"/> until the models folder is found;
        /// throws <see cref="LlmModelNotFoundException"/> naming the expected path if the folder or the file is missing.
        /// Never downloads anything.
        /// </summary>
        public string ResolveModelPath(string baseDirectory, string modelFile)
        {
            if (Path.IsPathRooted(modelFile))
            {
                return File.Exists(modelFile) ? modelFile : throw new LlmModelNotFoundException(modelFile);
            }

            var modelsDirectory = FindModelsDirectory(baseDirectory);
            var modelPath = Path.Combine(modelsDirectory, modelFile);
            return File.Exists(modelPath) ? modelPath : throw new LlmModelNotFoundException(modelPath);
        }

        private string FindModelsDirectory(string baseDirectory)
        {
            if (Path.IsPathRooted(ModelsDirectory))
            {
                return ModelsDirectory;
            }

            for (var directory = new DirectoryInfo(baseDirectory); directory != null; directory = directory.Parent)
            {
                var candidate = Path.GetFullPath(ModelsDirectory, directory.FullName);
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }
            }

            // No models folder anywhere up the tree: report where it is expected relative to the base directory.
            return Path.GetFullPath(ModelsDirectory, baseDirectory);
        }
    }

    /// <summary>The GGUF model file is not on disk. It has to be placed there manually; it is never downloaded.</summary>
    public class LlmModelNotFoundException : FileNotFoundException
    {
        public LlmModelNotFoundException(string expectedPath)
            : base($"LLM model file not found: '{expectedPath}'. Place the GGUF file there manually " +
                   $"(see Llm:ModelsDirectory / Llm:ModelFile / Llm:Minutes:ModelFile); it is never downloaded.", expectedPath)
        {
        }
    }
}
