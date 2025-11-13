using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using GarminTempApi.Data;
using GarminTempApi.Models;
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
        private readonly AppDbContext _dbContext;
        private readonly ILogger<InsightsController> _logger;

        public InsightsController(
            IOpenAiInsightService insightService,
            AppDbContext dbContext,
            ILogger<InsightsController> logger)
        {
            _insightService = insightService;
            _dbContext = dbContext;
            _logger = logger;
        }

        public record ChatMessageDto(
            [property: JsonPropertyName("role")] string? Role,
            [property: JsonPropertyName("content")] string? Content);

        public record QueryRequest(
            [property: JsonPropertyName("prompt")] string? Prompt,
            [property: JsonPropertyName("messages")] IReadOnlyList<ChatMessageDto>? Messages,
            [property: JsonPropertyName("context")] JsonElement? Context);

        public record FeedbackRequest(
            [property: JsonPropertyName("helpful")] bool? Helpful,
            [property: JsonPropertyName("focusArea")] string? FocusArea,
            [property: JsonPropertyName("notes")] string? Notes);

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
            if (request is null)
            {
                return BadRequest(new { error = "Prompt is required." });
            }

            var chatMessages = new List<InsightChatMessage>();
            if (request.Messages is { Count: > 0 })
            {
                foreach (var message in request.Messages)
                {
                    if (message is null)
                    {
                        continue;
                    }

                    var content = message.Content?.Trim();
                    if (string.IsNullOrWhiteSpace(content))
                    {
                        continue;
                    }

                    var role = string.IsNullOrWhiteSpace(message.Role) ? "user" : message.Role!;
                    chatMessages.Add(new InsightChatMessage(role, content));
                }
            }
            else if (!string.IsNullOrWhiteSpace(request.Prompt))
            {
                chatMessages.Add(new InsightChatMessage("user", request.Prompt.Trim()));
            }

            if (chatMessages.Count == 0)
            {
                return BadRequest(new { error = "Prompt is required." });
            }

            try
            {
                var reply = await _insightService.RunChatQueryAsync(chatMessages, cancellationToken);
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

        [HttpPost("feedback")]
        public async Task<IActionResult> SubmitFeedbackAsync([FromBody] FeedbackRequest request, CancellationToken cancellationToken)
        {
            if (request is null || request.Helpful is null)
            {
                return BadRequest(new { error = "Helpful flag is required." });
            }

            var entity = new RecommendationFeedback
            {
                SubmittedUtc = DateTime.UtcNow,
                Helpful = request.Helpful.Value,
                FocusArea = string.IsNullOrWhiteSpace(request.FocusArea) ? null : request.FocusArea.Trim(),
                Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim()
            };

            _dbContext.RecommendationFeedback.Add(entity);
            await _dbContext.SaveChangesAsync(cancellationToken);

            return Ok(new { id = entity.Id });
        }
    }
}
