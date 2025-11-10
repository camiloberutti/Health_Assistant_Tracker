using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GarminTempApi.Configuration;
using GarminTempApi.Data;
using GarminTempApi.Models;
using GarminTempApi.Services;
using GarminTempApi.Tests.TestUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GarminTempApi.Tests;

public class OpenAiInsightServiceTests
{
    [Fact]
    public async Task GenerateDailyRecommendationAsync_ReturnsAssistantMessage()
    {
        await using var context = CreateContext();
        await SeedSampleDataAsync(context);

        var handler = new RecordingHandler();
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.example.com")
        };

        var options = new OpenAiOptions
        {
            ApiKey = "test-key",
            BaseUrl = "https://api.example.com/v1",
            Model = "test-model",
            Temperature = 0.2,
            MaxTokens = 256
        };

        var configuration = new ConfigurationBuilder().Build();

        var service = new OpenAiInsightService(
            new InsightDataBuilder(context, NullLogger<InsightDataBuilder>.Instance),
            new StaticOptionsMonitor<OpenAiOptions>(options),
            httpClient,
            configuration,
            NullLogger<OpenAiInsightService>.Instance);

        handler.ResponseContent =
            "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"Daily plan\"}}]}";

        var result = await service.GenerateDailyRecommendationAsync(new DateTime(2025, 11, 9), CancellationToken.None);

        Assert.Equal("Daily plan", result);
        Assert.NotNull(handler.LastRequest);
        Assert.Equal("Bearer", handler.LastRequest!.Headers.Authorization?.Scheme);
        Assert.Equal("test-key", handler.LastRequest.Headers.Authorization?.Parameter);
        Assert.Equal("https://api.example.com/v1/chat/completions", handler.LastRequest.RequestUri!.ToString());

        using (var document = JsonDocument.Parse(handler.LastRequestBody))
        {
            var userContent = document.RootElement.GetProperty("messages")[1].GetProperty("content").GetString();
            Assert.NotNull(userContent);
            Assert.Contains("\"type\":\"daily-recommendation\"", userContent!);
        }
    }

    [Fact]
    public async Task RunChatQueryAsync_UsesAzureApiKeyHeader()
    {
        await using var context = CreateContext();
        await SeedSampleDataAsync(context);

        var handler = new RecordingHandler();
        var httpClient = new HttpClient(handler);

        var options = new OpenAiOptions
        {
            ApiKey = "azure-key",
            BaseUrl = "https://example.openai.azure.com/openai/deployments/gpt35/chat/completions?api-version=2024-05-01-preview",
            Model = "gpt35",
            Temperature = 0.3,
            MaxTokens = 256
        };

        var configuration = new ConfigurationBuilder().Build();

        var service = new OpenAiInsightService(
            new InsightDataBuilder(context, NullLogger<InsightDataBuilder>.Instance),
            new StaticOptionsMonitor<OpenAiOptions>(options),
            httpClient,
            configuration,
            NullLogger<OpenAiInsightService>.Instance);

        handler.ResponseContent =
            "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"Hello\"}}]}";

        var result = await service.RunChatQueryAsync("How was my week?", CancellationToken.None);

        Assert.Equal("Hello", result);
        Assert.NotNull(handler.LastRequest);
        Assert.False(handler.LastRequest!.Headers.Contains("Authorization"));
        Assert.Equal("azure-key", handler.LastRequest.Headers.GetValues("api-key").Single());
        Assert.Equal(options.BaseUrl.TrimEnd('/'), handler.LastRequest.RequestUri!.ToString());

        using (var document = JsonDocument.Parse(handler.LastRequestBody))
        {
            var userContent = document.RootElement.GetProperty("messages")[1].GetProperty("content").GetString();
            Assert.NotNull(userContent);
            Assert.Contains("\"type\":\"chat-query\"", userContent!);
        }
    }

    private static async Task SeedSampleDataAsync(AppDbContext context)
    {
        var day = new DateTime(2025, 11, 9);
        context.StepSummaries.Add(new StepSummary { Date = day, TotalSteps = 10000 });
        context.SleepSummaries.Add(new SleepSummary { Date = day, TotalSleepSeconds = 30000, RestingHeartRate = 48 });
        context.Activities.Add(new Activity
        {
            StartTime = day.AddHours(7),
            DistanceMeters = 10000,
            Duration = TimeSpan.FromMinutes(50),
            ActivityType = "Run"
        });
        await context.SaveChangesAsync();
    }

    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"OpenAiInsightServiceTests_{Guid.NewGuid()}")
            .Options;
        return new AppDbContext(options);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string LastRequestBody { get; private set; } = string.Empty;
        public string ResponseContent { get; set; } = string.Empty;
        public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.OK;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            if (request.Content is not null)
            {
                LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            var response = new HttpResponseMessage(StatusCode)
            {
                Content = new StringContent(string.IsNullOrEmpty(ResponseContent) ?
                    "{\"choices\":[]}" : ResponseContent)
            };
            return response;
        }
    }
}
