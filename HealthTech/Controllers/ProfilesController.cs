using HealthTech.Profiles;
using Microsoft.AspNetCore.Mvc;

namespace HealthTech.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class ProfilesController : ControllerBase
    {
        [HttpGet]
        public IReadOnlyList<ProfileView> List([FromServices] IProfileCatalog catalog) =>
            catalog.All.Select(p => new ProfileView(p.Key, p.DisplayName)).ToList();
    }

    /// <summary>Пункт выпадашки при загрузке файла.</summary>
    public record ProfileView(string Key, string DisplayName);
}
