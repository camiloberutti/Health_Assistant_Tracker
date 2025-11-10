using System.ComponentModel.DataAnnotations;

namespace GarminTempApi.Configuration;

public class OpenAiOptions
{
    public const string SectionName = "OpenAI";

    [Required]
    public string ApiKey { get; set; } = string.Empty;

    [Required]
    public string BaseUrl { get; set; } = "https://api.openai.com/v1";

    [Required]
    public string Model { get; set; } = "gpt-4o-mini";

    [Range(0, 2)]
    public double Temperature { get; set; } = 0.4;

    [Range(32, 2048)]
    public int MaxTokens { get; set; } = 400;
}
