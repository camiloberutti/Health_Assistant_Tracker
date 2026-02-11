using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace GarminTempApi.Pages.Insights
{
    public class IndexModel : PageModel
    {
        public IActionResult OnGet()
        {
            // Insights chat is now a global floating widget.
            // Redirect old bookmarks to the dashboard.
            return RedirectToPage("/Index");
        }
    }
}
