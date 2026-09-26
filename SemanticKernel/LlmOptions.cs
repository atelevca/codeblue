namespace SemanticKernel
{
    /// <summary>Settings of the local LLM (section <c>Llm</c>, defaults in <c>appsettings.llm.json</c>).</summary>
    public class LlmOptions
    {
        public const string SectionName = "Llm";

        /// <summary>
        /// Folder with the GGUF model. Relative: looked up from the app's base directory and each of its parents
        /// (so <c>../models</c> finds <c>&lt;SolutionRoot&gt;/models</c> from the project folder and from <c>bin/</c>).
        /// Absolute: used as-is.
        /// </summary>
        public string ModelsDirectory { get; set; } = "../models";

        /// <summary>GGUF file name inside <see cref="ModelsDirectory"/>, or an absolute path to the file.</summary>
        public string ModelFile { get; set; } = "qwen2.5-7b-instruct-q4_k_m.gguf";

        public uint ContextSize { get; set; } = 8192;

        /// <summary>Layers offloaded to the GPU; 0 with the CPU backend.</summary>
        public int GpuLayerCount { get; set; }

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
        public int MaxGlossaryCharacters { get; set; } = 2000;

        /// <summary>Pieces from the end of the previous batch sent as read-only context.</summary>
        public int ContextSegments { get; set; } = 3;

        /// <summary>Extra attempts per batch when the model output can't be parsed or has wrong ids.</summary>
        public int MaxRetries { get; set; } = 1;

        /// <summary>
        /// Full path of the model file. Walks up from <paramref name="baseDirectory"/> until the models folder is found;
        /// throws <see cref="LlmModelNotFoundException"/> naming the expected path if the folder or the file is missing.
        /// Never downloads anything.
        /// </summary>
        public string ResolveModelPath(string baseDirectory)
        {
            if (Path.IsPathRooted(ModelFile))
            {
                return File.Exists(ModelFile) ? ModelFile : throw new LlmModelNotFoundException(ModelFile);
            }

            var modelsDirectory = FindModelsDirectory(baseDirectory);
            var modelPath = Path.Combine(modelsDirectory, ModelFile);
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
                   $"(see Llm:ModelsDirectory / Llm:ModelFile); it is never downloaded.", expectedPath)
        {
        }
    }
}
