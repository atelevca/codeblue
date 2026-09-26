using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace HealthTech.Audio
{
    // Maps AudioProcessingException to a ProblemDetails response so controllers don't have to catch it.
    public class AudioProcessingExceptionHandler : IExceptionHandler
    {
        private readonly IProblemDetailsService _problemDetailsService;

        public AudioProcessingExceptionHandler(IProblemDetailsService problemDetailsService)
        {
            _problemDetailsService = problemDetailsService;
        }

        public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
        {
            if (exception is not AudioProcessingException audioException)
            {
                return false;
            }

            httpContext.Response.StatusCode = audioException.Error switch
            {
                AudioProcessingError.InputNotFound => StatusCodes.Status404NotFound,
                AudioProcessingError.UnknownProfile => StatusCodes.Status400BadRequest,
                AudioProcessingError.NotAudio or AudioProcessingError.CorruptedAudio or AudioProcessingError.InvalidTranscript => StatusCodes.Status422UnprocessableEntity,
                AudioProcessingError.FfmpegUnavailable => StatusCodes.Status503ServiceUnavailable,
                _ => StatusCodes.Status500InternalServerError
            };

            return await _problemDetailsService.TryWriteAsync(new ProblemDetailsContext
            {
                HttpContext = httpContext,
                Exception = exception,
                ProblemDetails = new ProblemDetails
                {
                    Status = httpContext.Response.StatusCode,
                    Title = audioException.Error.ToString(),
                    Detail = audioException.Message
                }
            });
        }
    }
}
