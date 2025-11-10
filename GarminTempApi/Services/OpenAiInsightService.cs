using System;
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

    private const string DefaultSystemPrompt = "You are a supportive Garmin coach. Use the provided metrics to deliver concise, actionable insights and recommendations. Be honest about missing data and avoid inventing numbers.";

    private readonly InsightDataBuilder _dataBuilder;
    private readonly IOptionsMonitor<OpenAiOptions> _optionsMonitor;
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<OpenAiInsightService> _logger;

    public OpenAiInsightService(
        InsightDataBuilder dataBuilder,
        IOptionsMonitor<OpenAiOptions> optionsMonitor,
        HttpClient httpClient,
        IConfiguration configuration,
        ILogger<OpenAiInsightService> logger)
    {
        _dataBuilder = dataBuilder;
        _optionsMonitor = optionsMonitor;
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<string> GenerateDailyRecommendationAsync(DateTime targetDate, CancellationToken cancellationToken)
    {
        var options = GetValidatedOptions();

        var digest = await _dataBuilder.BuildUserDataDigestAsync(targetDate.Date.AddDays(-13), targetDate.Date, cancellationToken);
        var recommendationContext = _dataBuilder.BuildDailyRecommendationContext(targetDate, digest);

        var payload = new
        {
            type = "daily-recommendation",
            targetDate = targetDate.Date,
            dailyContext = recommendationContext,
            digest
        };

        var userContent = new StringBuilder();
        userContent.AppendLine("Please craft a short, motivating plan for the user.");
        userContent.AppendLine("Keep the answer under 140 words, use markdown with a heading and bullet points.");
        userContent.AppendLine("Focus on a balance between training, recovery, and lifestyle.");
        userContent.AppendLine("Context:");
        userContent.AppendLine(JsonSerializer.Serialize(payload, SerializerOptions));

        return await SendChatCompletionAsync(options, userContent.ToString(), cancellationToken);
    }

    public async Task<string> RunChatQueryAsync(string prompt, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            throw new ArgumentException("Prompt must not be empty.", nameof(prompt));
        }

        var options = GetValidatedOptions();
        var today = DateTime.UtcNow.Date;
        var digest = await _dataBuilder.BuildUserDataDigestAsync(today.AddDays(-13), today, cancellationToken);

        var payload = new
        {
            type = "chat-query",
            question = prompt,
            digest
        };

        var userContent = new StringBuilder();
        userContent.AppendLine("Answer the user's question using the available data.");
        userContent.AppendLine("Be transparent if something is unknown and keep the answer under 180 words.");
        userContent.AppendLine("Use markdown paragraphs or bullet points when appropriate.");
        userContent.AppendLine("Context:");
        userContent.AppendLine(JsonSerializer.Serialize(payload, SerializerOptions));

        return await SendChatCompletionAsync(options, userContent.ToString(), cancellationToken);
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

    private async Task<string> SendChatCompletionAsync(OpenAiOptions options, string userContent, CancellationToken cancellationToken)
    {
        var requestUri = ResolveEndpoint(options.BaseUrl);
        using var request = new HttpRequestMessage(HttpMethod.Post, requestUri);

        ApplyAuthenticationHeaders(request, options);

        var requestBody = new
        {
            model = options.Model,
            temperature = options.Temperature,
            max_tokens = options.MaxTokens,
            messages = new object[]
            {
                new { role = "system", content = DefaultSystemPrompt },
                new { role = "user", content = userContent }
            }
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
