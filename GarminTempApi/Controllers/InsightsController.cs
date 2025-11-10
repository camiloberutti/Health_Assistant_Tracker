using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using GarminTempApi.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace GarminTempApi.Controllers
{
    [ApiController]
    [Route("api/insights")]
    public class InsightsController : ControllerBase
    {
        private readonly IOpenAiInsightService _insightService;
        private readonly ILogger<InsightsController> _logger;

        public InsightsController(IOpenAiInsightService insightService, ILogger<InsightsController> logger)
        {
            _insightService = insightService;
            _logger = logger;
        }

        public record QueryRequest(
            [property: JsonPropertyName("prompt")] string Prompt,
            [property: JsonPropertyName("context")] JsonElement? Context);

        [HttpGet("daily")]
        public async Task<IActionResult> GetDailyRecommendation(CancellationToken cancellationToken)
        {
            try
            {
                var recommendation = await _insightService.GenerateDailyRecommendationAsync(DateTime.UtcNow, cancellationToken);
                return Ok(new { recommendation });
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(ex, "OpenAI daily recommendation unavailable.");
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected failure while generating daily recommendation.");
                return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Failed to generate daily recommendation." });
            }
        }

        [HttpPost("query")]
        public async Task<IActionResult> QueryAsync([FromBody] QueryRequest request, CancellationToken cancellationToken)
        {
            if (request is null || string.IsNullOrWhiteSpace(request.Prompt))
            {
                return BadRequest(new { error = "Prompt is required." });
            }

            try
            {
                var reply = await _insightService.RunChatQueryAsync(request.Prompt, cancellationToken);
                return Ok(new { reply });
            }
            catch (ArgumentException ex)
            {
                _logger.LogWarning(ex, "Invalid insight query request.");
                return BadRequest(new { error = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(ex, "OpenAI query unavailable.");
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected failure while processing insight query.");
                return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Failed to process query." });
            }
        }
    }
}
