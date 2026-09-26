using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace HealthTech.Email;

/// <summary>The local SMTP server could not be reached or did not accept the message.</summary>
public sealed class EmailException(string message, Exception? inner = null) : Exception(message, inner);

public sealed class EmailExceptionHandler(IProblemDetailsService problems) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not EmailException error)
            return false;

        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        return await problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = new ProblemDetails
            {
                Status = StatusCodes.Status503ServiceUnavailable,
                Title = "Email error",
                Detail = error.Message
            }
        });
    }
}
