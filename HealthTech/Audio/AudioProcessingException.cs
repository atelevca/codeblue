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
        ModelFailed
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
