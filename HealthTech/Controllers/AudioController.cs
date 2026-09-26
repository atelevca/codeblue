using HealthTech.Audio;
using HealthTech.Transcription;
using Microsoft.AspNetCore.Mvc;

namespace HealthTech.Controllers
{
    // Services are injected per action so each endpoint only loads the models it uses
    // (e.g. diarization must not pull in the Whisper model).
    [ApiController]
    [Route("[controller]")]
    public class AudioController : ControllerBase
    {
        [HttpGet("validateAndProcess")]
        public Task<AudioRunResult> Run([FromServices] IAudioBatchService audioBatchService, CancellationToken cancellationToken) =>
            audioBatchService.ProcessInputDirectoryAsync(cancellationToken);

        [HttpPost("transcribeProcessed")]
        public Task<TranscriptionResult> TranscribeProcessed(
            [FromServices] IProcessedAudioTranscriptionService transcriptionService, CancellationToken cancellationToken) =>
            transcriptionService.TranscribeFirstProcessedAsync(cancellationToken);

        [HttpPost("diarizeProcessed")]
        public Task<DiarizationResult> DiarizeProcessed(
            [FromServices] IProcessedAudioDiarizationService diarizationService, CancellationToken cancellationToken) =>
            diarizationService.DiarizeFirstProcessedAsync(cancellationToken);

        [HttpPost("transcribeWithSpeakers")]
        public Task<SpeakerTranscriptResult> TranscribeWithSpeakers(
            [FromServices] IProcessedAudioSpeakerTranscriptService speakerTranscriptService, CancellationToken cancellationToken) =>
            speakerTranscriptService.TranscribeWithSpeakersAsync(cancellationToken);
    }
}
