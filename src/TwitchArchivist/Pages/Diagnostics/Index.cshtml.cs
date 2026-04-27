using Microsoft.AspNetCore.Mvc.RazorPages;
using TwitchArchivist.Services;

namespace TwitchArchivist.Pages.Diagnostics;

public class IndexModel(RuntimeStatusStore runtimeStatusStore) : PageModel
{
    public RuntimeStatusStore RuntimeStatus => runtimeStatusStore;

    public string TwitchAuthorizationBadgeClass => runtimeStatusStore.TwitchUserAuthorizationValidity switch
    {
        "valid" => string.Empty,
        "expiring-soon" => "warn",
        _ => "danger"
    };

    public void OnGet()
    {
    }
}
