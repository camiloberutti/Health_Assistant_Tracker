using System;
using System.Threading;
using System.Threading.Tasks;

namespace GarminTempApi.Services;

public interface IOpenAiInsightService
{
    Task<string> GenerateDailyRecommendationAsync(DateTime targetDate, CancellationToken cancellationToken);

    Task<string> RunChatQueryAsync(string prompt, CancellationToken cancellationToken);
}
