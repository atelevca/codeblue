using HealthTech.Transcription;
using Microsoft.AspNetCore.Mvc;

namespace HealthTech.Controllers
{
    // Services are injected per action so each endpoint only loads the models it uses.
    // Работа с записями идёт через /jobs; здесь остаётся только повторная коррекция
    // уже готового транскрипта, чтобы крутить глоссарии без нового прогона Whisper.
    [ApiController]
    [Route("[controller]")]
    public class AudioController : ControllerBase
    {
        [HttpPost("correctTranscript")]
        public Task<TranscriptCorrectionResult> CorrectTranscript(
            [FromQuery] string fileName,
            [FromQuery] string profile,
            [FromServices] ITranscriptCorrectionService transcriptCorrectionService, CancellationToken cancellationToken) =>
            transcriptCorrectionService.CorrectTranscriptAsync(fileName, profile, cancellationToken);
    }
}
