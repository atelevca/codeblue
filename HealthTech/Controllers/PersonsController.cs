using HealthTech.Speakers;
using Microsoft.AspNetCore.Mvc;

namespace HealthTech.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class PersonsController : ControllerBase
    {
        [HttpGet]
        public Task<IReadOnlyList<Person>> List(
            [FromServices] ISpeakerBindingService speakers, CancellationToken cancellationToken) =>
            speakers.ListPersonsAsync(cancellationToken);
    }
}
