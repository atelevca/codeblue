using HealthTech.Jobs;
using Microsoft.AspNetCore.Mvc;

namespace HealthTech.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class JobsController : ControllerBase
    {
        [HttpPost]
        public async Task<JobCreated> Create(
            IFormFile file,
            [FromForm] string profile,
            [FromServices] IJobService jobService,
            CancellationToken cancellationToken)
        {
            await using var stream = file.OpenReadStream();
            var jobId = await jobService.CreateAsync(stream, file.FileName, profile, cancellationToken);
            return new JobCreated(jobId);
        }

        [HttpGet]
        public Task<IReadOnlyList<Job>> List(
            [FromServices] IJobService jobService, CancellationToken cancellationToken) =>
            jobService.ListAsync(cancellationToken);

        [HttpGet("{id:guid}")]
        public async Task<ActionResult<Job>> Get(
            Guid id, [FromServices] IJobService jobService, CancellationToken cancellationToken)
        {
            var job = await jobService.GetAsync(id, cancellationToken);
            return job is null ? NotFound() : job;
        }

        [HttpGet("{id:guid}/result")]
        public async Task<ActionResult> GetResult(
            Guid id, [FromServices] IJobService jobService, CancellationToken cancellationToken)
        {
            var result = await jobService.GetResultAsync(id, cancellationToken);
            return result is null ? NotFound() : Content(result, "application/json");
        }
    }

    public record JobCreated(Guid JobId);
}
