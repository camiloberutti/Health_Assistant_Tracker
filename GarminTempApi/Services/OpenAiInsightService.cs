using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using GarminTempApi.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GarminTempApi.Services;

public class OpenAiInsightService : IOpenAiInsightService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    private const string DefaultSystemPrompt = @"
You are a knowledgeable health expert. 
Your task is to provide clear, actionable, and precise health insights based on the user's recent activity, sleep, and recovery patterns. 
Do not provide generic advice such as 'sleep between 7-9 hours.' 
Instead, suggest if the user should sleep earlier or later, based on their specific sleep data and activity levels.
For example, if the user has had several low-intensity days, recommend an increase in activity, or if they've been sedentary, suggest a more active rest day. 
If the user has had consistent low sleep quality, suggest adjustments to improve recovery based on their activity level.
Always evaluate upcoming and recent races within the provided preparation, taper, race-day, and recovery windows, adjusting intensity, rest, and nutrition guidance accordingly.
Always consider the user's specific metrics, and provide insights that are actionable, precise, and avoid repetition.
Be honest about missing data and avoid inventing numbers.";

    private readonly InsightDataBuilder _dataBuilder;
    private readonly IOptionsMonitor<OpenAiOptions> _optionsMonitor;
    private readonly IOptions<CalendarRecommendationOptions> _calendarOptions;
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<OpenAiInsightService> _logger;

    public OpenAiInsightService(
        InsightDataBuilder dataBuilder,
        IOptionsMonitor<OpenAiOptions> optionsMonitor,
        IOptions<CalendarRecommendationOptions> calendarOptions,
        HttpClient httpClient,
        IConfiguration configuration,
        ILogger<OpenAiInsightService> logger)
    {
        _dataBuilder = dataBuilder;
        _optionsMonitor = optionsMonitor;
        _calendarOptions = calendarOptions;
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<string> GenerateDailyRecommendationAsync(DateTime targetDate, CancellationToken cancellationToken)
    {
        var options = GetValidatedOptions();

        // Build user data for the past week
        var digest = await _dataBuilder.BuildUserDataDigestAsync(targetDate.Date.AddDays(-13), targetDate.Date, cancellationToken);
        var recommendationContext = _dataBuilder.BuildDailyRecommendationContext(targetDate, digest);
        var weeklySnapshot = await _dataBuilder.BuildWeeklyHealthSnapshotAsync(targetDate, 7, cancellationToken);
        var calendarContext = await _dataBuilder.BuildCalendarInsightAsync(targetDate, _calendarOptions.Value, cancellationToken);

        var payload = new
        {
            type = "daily-recommendation",
            targetDate = targetDate.Date,
            dailyContext = recommendationContext,
            weeklySummary = weeklySnapshot,
            digest,
            calendar = calendarContext
        };

        var userContent = new StringBuilder();
        userContent.AppendLine("Use the provided data to create precise, athlete-aware coaching for the target day.");
        userContent.AppendLine("Always integrate calendar events, prioritizing races across preparation, taper, race day, and recovery windows defined in calendar.raceConfiguration.");
        userContent.AppendLine("If no calendar events are relevant, focus on the health metrics without adding filler advice.");
        userContent.AppendLine("Return a single JSON object with the exact keys: today_insight, action_12h, tomorrow_preparation, nutrition.");
        userContent.AppendLine("Each value must be a 45-60 word paragraph so the total stays between 180 and 220 words.");
        userContent.AppendLine("Reference timing, intensity, recovery, and nutrition strategies that respect the user's scheduled events (especially races) and recent load.");
        userContent.AppendLine("Do not output markdown, lists, or any text outside the JSON object.");
        userContent.AppendLine("Context:");
        userContent.AppendLine(JsonSerializer.Serialize(payload, SerializerOptions));

        var messages = new List<object>
    {
        BuildMessage("system", DefaultSystemPrompt),
        BuildMessage("user", userContent.ToString())
    };

        return await SendChatCompletionAsync(options, messages, cancellationToken);
    }


    public async Task<string> RunChatQueryAsync(IReadOnlyList<InsightChatMessage> messages, CancellationToken cancellationToken)
    {
        if (messages is null)
        {
            throw new ArgumentNullException(nameof(messages));
        }

        var sanitized = new List<(string Role, string Content)>(messages.Count);
        foreach (var message in messages)
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

            sanitized.Add((NormalizeRole(message.Role), content));
        }

        if (sanitized.Count == 0)
        {
            throw new ArgumentException("At least one chat message is required.", nameof(messages));
        }

        if (sanitized[^1].Role != "user")
        {
            throw new ArgumentException("The last chat message must come from the user.", nameof(messages));
        }

        var options = GetValidatedOptions();
        var today = DateTime.UtcNow.Date;
        var digest = await _dataBuilder.BuildUserDataDigestAsync(today.AddDays(-13), today, cancellationToken);
        var calendarContext = await _dataBuilder.BuildCalendarInsightAsync(today, _calendarOptions.Value, cancellationToken);

        var contextPayload = new
        {
            type = "chat-query",
            digest,
            calendar = calendarContext
        };

        var conversation = new List<object>
        {
            BuildMessage("system", DefaultSystemPrompt),
            BuildMessage("system", "Answer the user's question using the available data. Be transparent about gaps and keep replies under 180 words. Use markdown paragraphs or bullet points when helpful."),
            BuildMessage("system", $"Context:\n{JsonSerializer.Serialize(contextPayload, SerializerOptions)}")
        };

        foreach (var message in sanitized.TakeLast(20))
        {
            conversation.Add(BuildMessage(message.Role, message.Content));
        }

        return await SendChatCompletionAsync(options, conversation, cancellationToken);
    }

    private static object BuildMessage(string role, string content)
    {
        return new { role = NormalizeRole(role), content };
    }

    private static string NormalizeRole(string? role)
    {
        if (string.IsNullOrWhiteSpace(role))
        {
            return "user";
        }

        return role.Trim().ToLowerInvariant() switch
        {
            "assistant" => "assistant",
            "system" => "system",
            _ => "user"
        };
    }

    private OpenAiOptions GetValidatedOptions()
    {
        var options = _optionsMonitor.CurrentValue;
        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            var fallbackApiKey = _configuration["OpenAI:ApiKey"]
                ?? _configuration["CHATGPT_API_KEY"]
                ?? _configuration["OPENAI_API_KEY"]
                ?? Environment.GetEnvironmentVariable("CHATGPT_API_KEY")
                ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");

            if (!string.IsNullOrWhiteSpace(fallbackApiKey))
            {
                options.ApiKey = fallbackApiKey;
            }
        }

        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            throw new InvalidOperationException("OpenAI API key is not configured.");
        }

        if (string.IsNullOrWhiteSpace(options.BaseUrl))
        {
            options.BaseUrl = _configuration["OpenAI:BaseUrl"]
                ?? _configuration["OPENAI_BASE_URL"]
                ?? "https://api.openai.com/v1";
        }

        if (string.IsNullOrWhiteSpace(options.Model))
        {
            var fallbackModel = _configuration["OpenAI:Model"]
                ?? _configuration["OPENAI_MODEL"];

            if (!string.IsNullOrWhiteSpace(fallbackModel))
            {
                options.Model = fallbackModel;
            }
        }

        if (string.IsNullOrWhiteSpace(options.Model))
        {
            throw new InvalidOperationException("OpenAI model is not configured.");
        }

        options.Temperature = Math.Clamp(
            ResolveDouble(options.Temperature, "OpenAI:Temperature", "OPENAI_TEMPERATURE", 0.4),
            0d,
            2d);
        options.MaxTokens = (int)Math.Clamp(
            ResolveInt(options.MaxTokens, "OpenAI:MaxTokens", "OPENAI_MAX_TOKENS", 400),
            32,
            4096);

        return options;
    }

    private double ResolveDouble(double current, string configKey, string envKey, double fallback)
    {
        var literal = _configuration[configKey] ?? _configuration[envKey];
        if (!string.IsNullOrWhiteSpace(literal)
            && double.TryParse(literal, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }

        var envLiteral = Environment.GetEnvironmentVariable(envKey);
        if (!string.IsNullOrWhiteSpace(envLiteral)
            && double.TryParse(envLiteral, NumberStyles.Float, CultureInfo.InvariantCulture, out var envValue))
        {
            return envValue;
        }

        return current == default ? fallback : current;
    }

    private int ResolveInt(int current, string configKey, string envKey, int fallback)
    {
        var literal = _configuration[configKey] ?? _configuration[envKey];
        if (!string.IsNullOrWhiteSpace(literal)
            && int.TryParse(literal, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }

        var envLiteral = Environment.GetEnvironmentVariable(envKey);
        if (!string.IsNullOrWhiteSpace(envLiteral)
            && int.TryParse(envLiteral, NumberStyles.Integer, CultureInfo.InvariantCulture, out var envValue))
        {
            return envValue;
        }

        return current == default ? fallback : current;
    }

    private async Task<string> SendChatCompletionAsync(OpenAiOptions options, IReadOnlyList<object> messages, CancellationToken cancellationToken)
    {
        if (messages is null || messages.Count == 0)
        {
            throw new ArgumentException("At least one message is required.", nameof(messages));
        }

        var requestUri = ResolveEndpoint(options.BaseUrl);
        using var request = new HttpRequestMessage(HttpMethod.Post, requestUri);

        ApplyAuthenticationHeaders(request, options);

        var requestBody = new
        {
            model = options.Model,
            temperature = options.Temperature,
            max_tokens = options.MaxTokens,
            messages
        };

        var content = JsonSerializer.Serialize(requestBody, SerializerOptions);
        request.Content = new StringContent(content, Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("OpenAI API request failed with status {StatusCode}: {Body}", (int)response.StatusCode, responseBody);
            throw new InvalidOperationException($"OpenAI request failed with status {(int)response.StatusCode} {response.StatusCode}.");
        }

        var completion = JsonSerializer.Deserialize<ChatCompletionResponse>(responseBody, SerializerOptions);
        var message = completion?.Choices?.FirstOrDefault()?.Message?.Content;
        if (string.IsNullOrWhiteSpace(message))
        {
            throw new InvalidOperationException("OpenAI returned an empty response.");
        }

        return message.Trim();
    }

    private void ApplyAuthenticationHeaders(HttpRequestMessage request, OpenAiOptions options)
    {
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        request.Headers.UserAgent.Clear();
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("GarminTempApi", "1.0"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("(+https://github.com/camiloberutti/App_Garmin)"));

        if (IsAzureEndpoint(options.BaseUrl))
        {
            request.Headers.Remove("api-key");
            request.Headers.TryAddWithoutValidation("api-key", options.ApiKey);
        }
        else
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        }
    }

    private static bool IsAzureEndpoint(string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return false;
        }

        return baseUrl.Contains("azure.com", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveEndpoint(string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return "https://api.openai.com/v1/chat/completions";
        }

        var trimmed = baseUrl.TrimEnd('/');
        if (trimmed.Contains("/chat/completions", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("/responses", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        return trimmed + "/chat/completions";
    }

    private sealed class ChatCompletionResponse
    {
        [JsonPropertyName("choices")]
        public ChatChoice[]? Choices { get; set; }
    }

    private sealed class ChatChoice
    {
        [JsonPropertyName("message")]
        public ChatMessage? Message { get; set; }
    }

    private sealed class ChatMessage
    {
        [JsonPropertyName("role")]
        public string? Role { get; set; }

        [JsonPropertyName("content")]
        public string? Content { get; set; }
    }
}
