using HealthTech.Audio;
using HealthTech.Transcription;
using Microsoft.Extensions.Options;

namespace HealthTech.Jobs
{
    /// <summary>
    /// Каталоги артефактов одного задания. Каждое задание изолировано: две загрузки
    /// подряд не видят файлов друг друга.
    /// </summary>
    public interface IJobPaths
    {
        string InputDirectory(Guid jobId);
        string ProcessedDirectory(Guid jobId);
        string TranscriptsDirectory(Guid jobId);
    }

    public class JobPaths : IJobPaths
    {
        private readonly string _inputRoot;
        private readonly string _processedRoot;
        private readonly string _transcriptsRoot;

        public JobPaths(IOptions<AudioOptions> audio, IOptions<TranscriptsOptions> transcripts, IHostEnvironment environment)
        {
            _inputRoot = Path.GetFullPath(audio.Value.InputDirectory, environment.ContentRootPath);
            _processedRoot = Path.GetFullPath(audio.Value.OutputDirectory, environment.ContentRootPath);
            _transcriptsRoot = transcripts.Value.OutputFolder;
        }

        public string InputDirectory(Guid jobId) => Path.Combine(_inputRoot, jobId.ToString());
        public string ProcessedDirectory(Guid jobId) => Path.Combine(_processedRoot, jobId.ToString());
        public string TranscriptsDirectory(Guid jobId) => Path.Combine(_transcriptsRoot, jobId.ToString());
    }
}
