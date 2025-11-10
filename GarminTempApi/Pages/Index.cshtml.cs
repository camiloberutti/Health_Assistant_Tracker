using System.Threading;
using System.Threading.Tasks;
using GarminTempApi.Services;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace GarminTempApi.Pages;

public class IndexModel : PageModel
{
    private readonly DataStatusService _statusService;

    public IndexModel(DataStatusService statusService)
    {
        _statusService = statusService;
    }

    public DataStatus? Status { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Status = await _statusService.GetStatusAsync(cancellationToken);
    }
}
