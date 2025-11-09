using Microsoft.AspNetCore.Mvc;

namespace GarminTempApi.Controllers
{
    [ApiController]
    [Route("auth")]
    public class AuthController : ControllerBase
    {
        [HttpGet("garmin/login")]
        public IActionResult Login()
        {
            return Ok("Placeholder: implement Garmin OAuth redirect once you have client id & secret.");
        }

        [HttpGet("garmin/callback")]
        public IActionResult Callback([FromQuery] string code)
        {
            return Ok(new { code });
        }
    }
}
