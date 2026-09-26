using HealthTech.Jobs;
using Microsoft.AspNetCore.Mvc;

namespace HealthTech.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class FilesController : ControllerBase
    {
        [HttpPost]
        public async Task<UploadedFile> Upload(
            IFormFile file,
            [FromServices] IJobService jobService,
            CancellationToken cancellationToken)
        {
            await using var stream = file.OpenReadStream();
            return await jobService.UploadAsync(stream, file.FileName, cancellationToken);
        }
    }
}
