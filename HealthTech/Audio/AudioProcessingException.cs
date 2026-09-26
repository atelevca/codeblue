namespace HealthTech.Audio
{
    public enum AudioProcessingError
    {
        InputNotFound,
        NotAudio,
        CorruptedAudio,
        FfmpegUnavailable,
        ConversionFailed,
        FileSystemError,
        ModelFailed,
        InvalidTranscript,
        UnknownProfile,

        /// <summary>По этому файлу запись уже оформлена.</summary>
        RecordAlreadyCreated,

        /// <summary>Поле запроса не проходит проверку.</summary>
        InvalidRequest
    }

    public class AudioProcessingException : Exception
    {
        public AudioProcessingError Error { get; }

        public AudioProcessingException(AudioProcessingError error, string message, Exception? innerException = null)
            : base(message, innerException)
        {
            Error = error;
        }
    }
}
