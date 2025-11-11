using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GarminTempApi.Controllers;
using GarminTempApi.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GarminTempApi.Tests;

public class InsightsControllerTests
{
    [Fact]
    public async Task QueryAsync_ReturnsBadRequest_WhenPromptMissing()
    {
        var controller = new InsightsController(new StubInsightService(), NullLogger<InsightsController>.Instance);

        var result = await controller.QueryAsync(new InsightsController.QueryRequest(null, null, null), CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("Prompt", badRequest.Value!.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task QueryAsync_ReturnsOk_WhenServiceSucceeds()
    {
        var stub = new StubInsightService
        {
            QueryHandler = (_, _) => Task.FromResult("Here is an insight.")
        };

        var controller = new InsightsController(stub, NullLogger<InsightsController>.Instance);
        var messages = new List<InsightsController.ChatMessageDto>
        {
            new("user", "How was my sleep?")
        };
        var result = await controller.QueryAsync(new InsightsController.QueryRequest(null, messages, null), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Contains("insight", ok.Value!.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetDailyRecommendation_ReturnsServiceUnavailable_WhenServiceFails()
    {
        var stub = new StubInsightService
        {
            DailyHandler = (_, _) => throw new InvalidOperationException("not configured")
        };

        var controller = new InsightsController(stub, NullLogger<InsightsController>.Instance);
        var result = await controller.GetDailyRecommendation(CancellationToken.None);

        var serviceUnavailable = Assert.IsType<ObjectResult>(result);
        Assert.Equal(503, serviceUnavailable.StatusCode);
    }

    [Fact]
    public async Task GetDailyRecommendation_ReturnsOk_WhenServiceSucceeds()
    {
        var stub = new StubInsightService
        {
            DailyHandler = (_, _) => Task.FromResult("Train easy today")
        };

        var controller = new InsightsController(stub, NullLogger<InsightsController>.Instance);
        var result = await controller.GetDailyRecommendation(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Contains("Train", ok.Value!.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private sealed class StubInsightService : IOpenAiInsightService
    {
        public Func<DateTime, CancellationToken, Task<string>>? DailyHandler { get; set; }
        public Func<IReadOnlyList<InsightChatMessage>, CancellationToken, Task<string>>? QueryHandler { get; set; }

        public Task<string> GenerateDailyRecommendationAsync(DateTime targetDate, CancellationToken cancellationToken)
        {
            if (DailyHandler is null)
            {
                return Task.FromResult("daily");
            }

            return DailyHandler(targetDate, cancellationToken);
        }

        public Task<string> RunChatQueryAsync(IReadOnlyList<InsightChatMessage> messages, CancellationToken cancellationToken)
        {
            if (QueryHandler is null)
            {
                return Task.FromResult("reply");
            }

            return QueryHandler(messages, cancellationToken);
        }
    }
}
