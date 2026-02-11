using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GarminTempApi.Configuration;
using GarminTempApi.Data;
using GarminTempApi.Models;
using GarminTempApi.Plugins;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;

namespace GarminTempApi.Services;

/// <summary>
/// Semantic Kernel-powered insight service that replaces the raw HttpClient approach.
/// The AI can now call plugin functions (tools) to query health data and modify
/// the calendar, enabling truly intelligent and actionable coaching.
/// </summary>
public class SemanticKernelInsightService : IOpenAiInsightService
{
    private const string CoachSystemPrompt = @"
You are an Elite Sports Performance Coach with deep expertise in exercise physiology, sleep science, and endurance training.
Your goal is to maximize the user's athletic performance, recovery, and long-term health.
Today's date is {TODAY}.

YOU HAVE TOOLS. Use them proactively:

**Health Data Tools:**
- `get_recent_activities` — Query recent workouts. You can filter by activity type (running, swimming, cycling, etc.).
- `get_sleep_summary` — Full sleep data: total/deep/light/REM/awake hours, sleep score, quality type, bedtime/wake time, RHR, body battery, SpO2, respiration.
- `get_step_summary` — Daily steps, distance, and calorie data.
- `get_training_load_analysis` — Fatigue vs recovery trend: RHR trend, rest day count, activity breakdown.
- `get_body_battery_trend` — Body battery recovery/drain trend for readiness assessment.
- `get_activity_details` — Deep dive into a specific activity: HR zones, splits, cadence, detail snapshot.

**Calendar Tools:**
- `get_upcoming_events` — See what's on the user's calendar (workouts, races, rest days).
- `check_schedule_conflicts` — Check a time slot for conflicts BEFORE scheduling. Handles recurring events too.
- `add_workout_to_calendar` — Schedule a workout. Always explain what you're adding first.
- `update_event_in_calendar` — Modify an existing event (title, time, notes, etc.).
- `remove_event_from_calendar` — Remove an event. Always confirm with the user first.

GUIDELINES:
1. **Be Specific & Data-Driven**: Call tools to get real numbers. Say ""Your RHR is up 5 bpm to 58; prioritize sleep extension tonight."" Never guess metrics.
2. **Avoid Platitudes**: Never say ""make sure to sleep well"" or ""listen to your body"" without backing it with data from the tools.
3. **Context Awareness**: Check the calendar for upcoming races. If a race is within 7 days, everything must support tapering and fueling.
4. **Proactive Scheduling**: When suggesting a workout, offer to add it to the calendar. If the user asks you to plan their week, actually schedule the workouts using the tool.
5. **Sleep Analysis**: Look at bedtime consistency, SpO2 trends, and sleep quality types — not just total hours.
6. **Recovery Assessment**: Use body battery trend and RHR trend together to assess true recovery status.
7. **Tone**: Professional, direct, encouraging, but firm when recovery is needed.
8. **Honesty**: If data is missing, acknowledge it. Do not hallucinate metrics — call the tools instead.
9. **Conversational**: Use Markdown for clarity (bold key numbers, use lists). Keep responses focused and under 200 words unless the user asks for more detail.";

    private const string DailyRecommendationPrompt = @"
Generate a daily coaching report for today ({TODAY}). Follow these steps:

1. Call `get_training_load_analysis` with 14 days to understand the fatigue/recovery trend.
2. Call `get_sleep_summary` with 3 days to see recent sleep quality (include bedtime consistency and SpO2).
3. Call `get_body_battery_trend` with 7 days to assess recovery readiness.
4. Call `get_upcoming_events` with 7 days to check the calendar.
5. Synthesize the data into a JSON response.

Return ONLY a JSON object (no markdown, no code blocks, no explanation text).

Example output format:
{""recent_summary"":""Over the past 3 days you ran 22 km total with avg RHR 52 bpm (stable). Sleep averaged 7.2h with scores of 78-82. Body battery recovery is good (+35 avg)."",""upcoming_outlook"":""Your Tuesday swim session is on the calendar. RHR is stable and body battery is well-recovered — today is a good day for a moderate-intensity session."",""suggested_workout"":""45-min tempo run at Zone 3, or rest if legs feel heavy from yesterday's 10K.""}

JSON schema:
{
  ""recent_summary"": ""2-3 sentence recap of the last few days: training load trend, sleep quality, recovery status. Include specific numbers. (MAX 80 words)"",
  ""upcoming_outlook"": ""What the user should focus on today and tomorrow based on recovery status and upcoming events. Be specific about workout type and intensity. (MAX 80 words)"",
  ""suggested_workout"": ""A specific workout suggestion for today or tomorrow, or 'Rest day recommended' if recovery is needed. Include type, duration, and intensity. (MAX 40 words)""
}";

    private readonly IServiceProvider _serviceProvider;
    private readonly IOptionsMonitor<OpenAiOptions> _optionsMonitor;
    private readonly IConfiguration _configuration;
    private readonly ILogger<SemanticKernelInsightService> _logger;

    public SemanticKernelInsightService(
        IServiceProvider serviceProvider,
        IOptionsMonitor<OpenAiOptions> optionsMonitor,
        IConfiguration configuration,
        ILogger<SemanticKernelInsightService> logger)
    {
        _serviceProvider = serviceProvider;
        _optionsMonitor = optionsMonitor;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<string> GenerateDailyRecommendationAsync(DateTime targetDate, CancellationToken cancellationToken)
    {
        var kernel = BuildKernel();
        var chatService = kernel.GetRequiredService<IChatCompletionService>();

        var today = DateTime.Now.ToString("yyyy-MM-dd (dddd)");
        var systemPrompt = CoachSystemPrompt.Replace("{TODAY}", today);
        var userPrompt = DailyRecommendationPrompt.Replace("{TODAY}", today);

        var history = new ChatHistory();
        history.AddSystemMessage(systemPrompt);
        history.AddUserMessage(userPrompt);

        var settings = new OpenAIPromptExecutionSettings
        {
            ToolCallBehavior = ToolCallBehavior.AutoInvokeKernelFunctions,
            Temperature = 0.3,
            MaxTokens = 1200
        };

        var result = await chatService.GetChatMessageContentAsync(history, settings, kernel, cancellationToken);
        return result.Content?.Trim() ?? string.Empty;
    }

    public async Task<string> RunChatQueryAsync(IReadOnlyList<InsightChatMessage> messages, CancellationToken cancellationToken)
    {
        if (messages is null || messages.Count == 0)
            throw new ArgumentException("At least one chat message is required.", nameof(messages));

        var kernel = BuildKernel();
        var chatService = kernel.GetRequiredService<IChatCompletionService>();

        var today = DateTime.Now.ToString("yyyy-MM-dd (dddd)");
        var systemPrompt = CoachSystemPrompt.Replace("{TODAY}", today);

        var history = new ChatHistory();
        history.AddSystemMessage(systemPrompt);

        // Add conversation history (last 20 messages)
        foreach (var msg in messages.TakeLast(20))
        {
            if (msg is null) continue;
            var content = msg.Content?.Trim();
            if (string.IsNullOrWhiteSpace(content)) continue;

            var role = msg.Role?.Trim().ToLowerInvariant();
            switch (role)
            {
                case "assistant":
                    history.AddAssistantMessage(content);
                    break;
                case "system":
                    history.AddSystemMessage(content);
                    break;
                default:
                    history.AddUserMessage(content);
                    break;
            }
        }

        // Ensure last message is from user
        if (history.Last().Role != AuthorRole.User)
            throw new ArgumentException("The last chat message must come from the user.", nameof(messages));

        var settings = new OpenAIPromptExecutionSettings
        {
            ToolCallBehavior = ToolCallBehavior.AutoInvokeKernelFunctions,
            Temperature = 0.4,
            MaxTokens = 1500
        };

        var result = await chatService.GetChatMessageContentAsync(history, settings, kernel, cancellationToken);
        return result.Content?.Trim() ?? string.Empty;
    }

    /// <summary>
    /// Builds a fresh Semantic Kernel instance with all plugins registered.
    /// </summary>
    private Kernel BuildKernel()
    {
        var options = GetValidatedOptions();

        var builder = Kernel.CreateBuilder();

        if (IsAzureEndpoint(options.BaseUrl))
        {
            builder.AddAzureOpenAIChatCompletion(
                deploymentName: options.Model,
                endpoint: options.BaseUrl,
                apiKey: options.ApiKey);
        }
        else
        {
            // For standard OpenAI or compatible endpoints
            if (options.BaseUrl != "https://api.openai.com/v1" &&
                !string.IsNullOrWhiteSpace(options.BaseUrl))
            {
                builder.AddOpenAIChatCompletion(
                    modelId: options.Model,
                    apiKey: options.ApiKey,
                    httpClient: CreateCustomEndpointClient(options.BaseUrl));
            }
            else
            {
                builder.AddOpenAIChatCompletion(
                    modelId: options.Model,
                    apiKey: options.ApiKey);
            }
        }

        var kernel = builder.Build();

        // Register plugins with the kernel
        var healthPlugin = new HealthDataPlugin(_serviceProvider);
        var calendarPlugin = new CalendarPlugin(
            _serviceProvider,
            _serviceProvider.GetRequiredService<ILogger<CalendarPlugin>>());

        kernel.Plugins.AddFromObject(healthPlugin, "HealthData");
        kernel.Plugins.AddFromObject(calendarPlugin, "Calendar");

        return kernel;
    }

    private System.Net.Http.HttpClient CreateCustomEndpointClient(string baseUrl)
    {
        var client = new System.Net.Http.HttpClient();
        var trimmed = baseUrl.TrimEnd('/');
        // OpenAI SDK expects the base URL without /chat/completions
        if (trimmed.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[..trimmed.LastIndexOf("/chat/completions", StringComparison.OrdinalIgnoreCase)];
        if (trimmed.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[..trimmed.LastIndexOf("/v1", StringComparison.OrdinalIgnoreCase)];

        client.BaseAddress = new Uri(trimmed.TrimEnd('/') + "/v1");
        return client;
    }

    private OpenAiOptions GetValidatedOptions()
    {
        var options = _optionsMonitor.CurrentValue;

        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            options.ApiKey = _configuration["OpenAI:ApiKey"]
                ?? _configuration["CHATGPT_API_KEY"]
                ?? _configuration["OPENAI_API_KEY"]
                ?? Environment.GetEnvironmentVariable("CHATGPT_API_KEY")
                ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY")
                ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(options.ApiKey))
            throw new InvalidOperationException("OpenAI API key is not configured.");

        if (string.IsNullOrWhiteSpace(options.BaseUrl))
        {
            options.BaseUrl = _configuration["OpenAI:BaseUrl"]
                ?? _configuration["OPENAI_BASE_URL"]
                ?? "https://api.openai.com/v1";
        }

        if (string.IsNullOrWhiteSpace(options.Model))
        {
            options.Model = _configuration["OpenAI:Model"]
                ?? _configuration["OPENAI_MODEL"]
                ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(options.Model))
            throw new InvalidOperationException("OpenAI model is not configured.");

        return options;
    }

    private static bool IsAzureEndpoint(string? baseUrl)
        => !string.IsNullOrWhiteSpace(baseUrl) && baseUrl.Contains("azure.com", StringComparison.OrdinalIgnoreCase);
}
