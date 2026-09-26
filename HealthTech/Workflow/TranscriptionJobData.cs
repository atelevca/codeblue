namespace HealthTech.Workflow
{
    /// <summary>Отрезок речи в секундах от начала записи. Сериализуется вместе с данными workflow.</summary>
    public class SpeechChunkDto
    {
        public double Start { get; set; }
        public double End { get; set; }
    }

    /// <summary>
    /// Данные задания, которые движок сериализует в хранилище на каждом переходе между шагами.
    /// Поэтому здесь только пути и идентификаторы: массивы сэмплов сюда класть нельзя.
    /// </summary>
    public class TranscriptionJobData
    {
        public Guid JobId { get; set; }
        public string ProfileKey { get; set; } = "";
        public string SourcePath { get; set; } = "";
        public string NormalizedPath { get; set; } = "";
        public string Wav16kPath { get; set; } = "";
        public List<SpeechChunkDto> Chunks { get; set; } = [];
        public string TranscriptPath { get; set; } = "";
        public string DiarizationPath { get; set; } = "";
        public string SpeakersPath { get; set; } = "";
    }
}
