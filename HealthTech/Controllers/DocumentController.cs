using HealthTech.Documents;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace HealthTech.Controllers;

[ApiController]
[Route("document")]
public sealed class DocumentController(IDocumentService documents) : ControllerBase
{
    [HttpGet("get/{jobId:guid}")]
    public Task<SavedDocument> Get(Guid jobId, CancellationToken cancellationToken) =>
        documents.GetAsync(jobId, cancellationToken);

    /// <summary>Without a body: generate and save. With a Quill Delta: verify and save the edited document.</summary>
    [HttpPost("save/{jobId:guid}")]
    [RequestSizeLimit(128 * 1024)]
    public Task<SavedDocument> Save(Guid jobId,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] SaveDocumentRequest? request,
        CancellationToken cancellationToken) => documents.SaveAsync(jobId, request, cancellationToken);

    [HttpGet("downloadpdf/{jobId:guid}")]
    [ProducesResponseType<Stream>(StatusCodes.Status200OK, "application/pdf")]
    public async Task<FileContentResult> DownloadPdf(Guid jobId, CancellationToken cancellationToken)
    {
        var result = await documents.DownloadPdfAsync(jobId, cancellationToken);
        return File(result.Content, "application/pdf", result.FileName);
    }
}
