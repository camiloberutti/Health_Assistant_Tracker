using System;
using System.Threading;
using System.Threading.Tasks;
using GarminTempApi.Services;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging;

namespace GarminTempApi.Pages;

public class IndexModel : PageModel
{
    private readonly DataStatusService _statusService;
    private readonly DashboardSummaryService _summaryService;
    private readonly ILogger<IndexModel> _logger;

    public IndexModel(
        DataStatusService statusService,
        DashboardSummaryService summaryService,
        ILogger<IndexModel> logger)
    {
        _statusService = statusService;
        _summaryService = summaryService;
        _logger = logger;
    }

    public DataStatus? Status { get; private set; }
    public DashboardSummary? Summary { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Status = await _statusService.GetStatusAsync(cancellationToken);
        Summary = await _summaryService.GetSummaryAsync(cancellationToken);
    }
}
