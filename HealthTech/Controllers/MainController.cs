using Microsoft.AspNetCore.Mvc;

namespace HealthTech.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class MainController : ControllerBase
    {
        [HttpGet("run")]
        public IActionResult Run()
        {
            return Ok();
        }
    }
}
