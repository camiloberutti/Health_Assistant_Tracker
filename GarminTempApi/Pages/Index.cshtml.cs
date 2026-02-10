using System;
using System.Threading;
using System.Threading.Tasks;
using GarminTempApi.Services;
using GarminTempApi.Utilities;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging;

namespace GarminTempApi.Pages;

public class IndexModel : PageModel
{
    private readonly DataStatusService _statusService;
    private readonly DashboardSummaryService _summaryService;
    private readonly IOpenAiInsightService _insightService;
    private readonly ILogger<IndexModel> _logger;

    public IndexModel(
        DataStatusService statusService,
        DashboardSummaryService summaryService,
        IOpenAiInsightService insightService,
        ILogger<IndexModel> logger)
    {
        _statusService = statusService;
        _summaryService = summaryService;
        _insightService = insightService;
        _logger = logger;
    }

    public DataStatus? Status { get; private set; }
    public DashboardSummary? Summary { get; private set; }
    public string? DailyRecommendation { get; private set; }
    public string? DailyRecommendationError { get; private set; }
    public DailyRecommendationSections? DailyRecommendationSections { get; private set; }
    public DailyRecommendationHtmlSections? DailyRecommendationHtmlSections { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Status = await _statusService.GetStatusAsync(cancellationToken);
        Summary = await _summaryService.GetSummaryAsync(cancellationToken);

        try
        {
            DailyRecommendation = await _insightService.GenerateDailyRecommendationAsync(DateTime.UtcNow, cancellationToken);
            if (RecommendationParser.TryParseSections(DailyRecommendation, out var sections) && sections is not null)
            {
                DailyRecommendationSections = sections;
                DailyRecommendationHtmlSections = RecommendationParser.ToHtmlSections(sections);
            }
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Daily recommendation unavailable.");
            DailyRecommendationError = ex.Message;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate daily recommendation.");
            DailyRecommendationError = "Daily recommendations are unavailable right now.";
        }
    }
}
