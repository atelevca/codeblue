using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using SemanticKernel;

namespace HealthTech.Transcription
{
    // A missing GGUF model or a context too small for the prompts: 503 with the reason, not a bare 500.
    public class LlmModelNotFoundExceptionHandler : IExceptionHandler
    {
        private readonly IProblemDetailsService _problemDetailsService;

        public LlmModelNotFoundExceptionHandler(IProblemDetailsService problemDetailsService)
        {
            _problemDetailsService = problemDetailsService;
        }

        public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
        {
            if (exception is not (LlmModelNotFoundException or LlmConfigurationException))
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
                    Title = exception is LlmConfigurationException ? "LlmConfiguration" : "LlmModelNotFound",
                    Detail = exception.Message
                }
            });
        }
    }
}
