using Microsoft.AspNetCore.Mvc.RazorPages;
using TwitchArchivist.Services;

namespace TwitchArchivist.Pages.Diagnostics;

public class IndexModel(RuntimeStatusStore runtimeStatusStore) : PageModel
{
    public RuntimeStatusStore RuntimeStatus => runtimeStatusStore;

    public void OnGet()
    {
    }
}
