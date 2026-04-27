using Microsoft.AspNetCore.Mvc.RazorPages;

namespace TwitchArchivist.Pages;

public class ErrorModel : PageModel
{
    public string? RequestId { get; private set; }

    public void OnGet()
    {
        RequestId = HttpContext.TraceIdentifier;
    }
}
