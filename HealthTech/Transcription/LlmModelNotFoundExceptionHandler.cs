using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using SemanticKernel;

namespace HealthTech.Transcription
{
    // The medical-correction GGUF model is placed manually; report the expected path as 503 instead of a bare 500.
    public class LlmModelNotFoundExceptionHandler : IExceptionHandler
    {
        private readonly IProblemDetailsService _problemDetailsService;

        public LlmModelNotFoundExceptionHandler(IProblemDetailsService problemDetailsService)
        {
            _problemDetailsService = problemDetailsService;
        }

        public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
        {
            if (exception is not LlmModelNotFoundException modelException)
            {
                return false;
            }

            httpContext.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return await _problemDetailsService.TryWriteAsync(new ProblemDetailsContext
            {
                HttpContext = httpContext,
                Exception = exception,
                ProblemDetails = new ProblemDetails
                {
                    Status = httpContext.Response.StatusCode,
                    Title = "LlmModelNotFound",
                    Detail = modelException.Message
                }
            });
        }
    }
}
