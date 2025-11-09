using Microsoft.AspNetCore.Mvc;

namespace GarminTempApi.Controllers
{
    [ApiController]
    [Route("api/insights")]
    public class InsightsController : ControllerBase
    {
        public record QueryRequest(string prompt, object? context);

        [HttpPost("query")]
        public IActionResult Query([FromBody] QueryRequest req)
        {
            // Minimal stub: replace with real OpenAI/Azure call server-side later.
            var reply = $"(stub) Received prompt of length {req.prompt?.Length ?? 0}.\nYou asked: {req.prompt}";
            return Ok(new { reply });
        }
    }
}
