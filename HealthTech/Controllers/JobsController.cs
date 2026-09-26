using HealthTech.Jobs;
using Microsoft.AspNetCore.Mvc;

namespace HealthTech.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class JobsController : ControllerBase
    {
        [HttpPost]
        public Task<Job> Save(
            [FromBody] SaveRecordRequest request,
            [FromServices] IJobService jobService,
            CancellationToken cancellationToken) =>
            jobService.SaveRecordAsync(request, cancellationToken);

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
}
