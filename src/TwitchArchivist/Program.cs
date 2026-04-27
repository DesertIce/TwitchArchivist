using TwitchArchivist.Models;
using TwitchArchivist.Persistence;
using TwitchArchivist.Services;
using TwitchArchivist.Services.Logging;
using TwitchArchivist.Services.Twitch;
using TwitchLib.EventSub.Websockets.Extensions;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseWindowsService();

builder.Services.Configure<TwitchOptions>(builder.Configuration.GetSection(TwitchOptions.SectionName));
builder.Services.Configure<DownloaderOptions>(builder.Configuration.GetSection(DownloaderOptions.SectionName));
builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection(StorageOptions.SectionName));

builder.Services.AddRouting(options => options.LowercaseUrls = true);
builder.Services.AddRazorPages();
builder.Services.AddSingleton(new RecentLogStore(capacity: 500));
builder.Services.AddSingleton<ILoggerProvider, RecentLogLoggerProvider>();
builder.Services.AddHttpClient(nameof(TwitchAccessTokenProvider));
builder.Services.AddHttpClient(nameof(TwitchHelixClient), client =>
{
    client.BaseAddress = new Uri("https://api.twitch.tv/helix/");
});
builder.Services.AddSingleton<RuntimeStatusStore>();
builder.Services.AddSingleton<IArchiveJobQueue, ArchiveJobQueue>();
builder.Services.AddSingleton<ITwitchAccessTokenProvider, TwitchAccessTokenProvider>();
builder.Services.AddSingleton<ITwitchHelixClient, TwitchHelixClient>();
builder.Services.AddSingleton<ITwitchDownloaderRunner, TwitchDownloaderRunner>();
builder.Services.AddTwitchLibEventSubWebsockets();
builder.Services.AddTwitchArchivistPersistence(builder.Configuration, builder.Environment.ContentRootPath);
builder.Services.AddHostedService<TwitchAccessTokenRefreshService>();
builder.Services.AddHostedService<DatabaseInitializationHostedService>();
builder.Services.AddHostedService<ConfigurationDiagnosticsHostedService>();
builder.Services.AddHostedService<TwitchEventSubHostedService>();
builder.Services.AddHostedService<ArchiveJobWorker>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

app.UseStaticFiles();
app.UseRouting();

app.MapGet("/healthz", (RuntimeStatusStore runtimeStatusStore) => Results.Ok(new
{
    status = "healthy",
    service = "TwitchArchivist",
    databaseReady = runtimeStatusStore.DatabaseReady,
    downloaderExecutableValid = runtimeStatusStore.DownloaderExecutableValid,
    eventSubConnectionState = runtimeStatusStore.EventSubConnectionState,
    twitchUserAuthorizationConfigured = runtimeStatusStore.TwitchUserAuthorizationConfigured,
    twitchUserAuthorizationLogin = runtimeStatusStore.TwitchUserAuthorizationLogin,
    twitchUserAuthorizationExpiresUtc = runtimeStatusStore.TwitchUserAuthorizationExpiresUtc
}));

app.MapGet("/api/runtime-status", async (ITwitchAccessTokenProvider accessTokenProvider, RuntimeStatusStore runtimeStatusStore, CancellationToken cancellationToken) =>
{
    var authorizationState = await accessTokenProvider.ValidateUserAuthorizationAsync(cancellationToken);
    runtimeStatusStore.UpdateTwitchUserAuthorization(
        authorizationState.IsConfigured,
        authorizationState.IsValid,
        authorizationState.Validity,
        authorizationState.Detail,
        authorizationState.TwitchUserLogin,
        authorizationState.ExpiresUtc,
        authorizationState.LastValidatedUtc);

    return Results.Ok(new
    {
        databaseReady = runtimeStatusStore.DatabaseReady,
        downloaderExecutableValid = runtimeStatusStore.DownloaderExecutableValid,
        downloaderExecutablePath = runtimeStatusStore.DownloaderExecutablePath,
        diagnosticsMessage = runtimeStatusStore.DiagnosticsMessage,
        eventSubConnectionState = runtimeStatusStore.EventSubConnectionState,
        twitchUserAuthorizationConfigured = runtimeStatusStore.TwitchUserAuthorizationConfigured,
        twitchUserAuthorizationIsValid = runtimeStatusStore.TwitchUserAuthorizationIsValid,
        twitchUserAuthorizationValidity = runtimeStatusStore.TwitchUserAuthorizationValidity,
        twitchUserAuthorizationDetail = runtimeStatusStore.TwitchUserAuthorizationDetail,
        twitchUserAuthorizationLogin = runtimeStatusStore.TwitchUserAuthorizationLogin,
        twitchUserAuthorizationExpiresUtc = runtimeStatusStore.TwitchUserAuthorizationExpiresUtc,
        twitchUserAuthorizationLastValidatedUtc = runtimeStatusStore.TwitchUserAuthorizationLastValidatedUtc,
        updatedUtc = runtimeStatusStore.UpdatedUtc
    });
});

app.MapGet("/auth/twitch/start", (HttpContext httpContext, ITwitchAccessTokenProvider accessTokenProvider) =>
{
    var state = Convert.ToHexString(Guid.NewGuid().ToByteArray());
    var redirectUri = BuildTwitchOAuthRedirectUri(httpContext.Request);
    var authorizationUrl = accessTokenProvider.BuildUserAuthorizationUrl(state, redirectUri);
    if (string.IsNullOrWhiteSpace(authorizationUrl))
    {
        return Results.Problem("Twitch client credentials are not configured.");
    }

    var cookieOptions = new CookieOptions
    {
        HttpOnly = true,
        IsEssential = true,
        SameSite = SameSiteMode.Lax,
        Secure = string.Equals(httpContext.Request.Scheme, "https", StringComparison.OrdinalIgnoreCase)
    };

    httpContext.Response.Cookies.Append("twitch_oauth_state", state, cookieOptions);
    httpContext.Response.Cookies.Append("twitch_oauth_redirect_uri", redirectUri, cookieOptions);
    return Results.Redirect(authorizationUrl);
});

app.MapGet("/auth/twitch/callback", async (HttpContext httpContext, ITwitchAccessTokenProvider accessTokenProvider, RuntimeStatusStore runtimeStatusStore, CancellationToken cancellationToken) =>
{
    var error = httpContext.Request.Query["error"].ToString();
    if (!string.IsNullOrWhiteSpace(error))
    {
        var description = httpContext.Request.Query["error_description"].ToString();
        return Results.Content(
            $"<html><body><h1>Twitch authorization failed</h1><p>{System.Net.WebUtility.HtmlEncode(description)}</p><p><a href=\"/diagnostics\">Return to diagnostics</a></p></body></html>",
            "text/html");
    }

    var expectedState = httpContext.Request.Cookies["twitch_oauth_state"];
    var redirectUri = httpContext.Request.Cookies["twitch_oauth_redirect_uri"];
    var returnedState = httpContext.Request.Query["state"].ToString();
    var code = httpContext.Request.Query["code"].ToString();

    httpContext.Response.Cookies.Delete("twitch_oauth_state");
    httpContext.Response.Cookies.Delete("twitch_oauth_redirect_uri");

    if (string.IsNullOrWhiteSpace(expectedState) ||
        string.IsNullOrWhiteSpace(returnedState) ||
        !string.Equals(expectedState, returnedState, StringComparison.Ordinal))
    {
        return Results.BadRequest("The OAuth state did not match the current authorization request.");
    }

    if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(redirectUri))
    {
        return Results.BadRequest("The Twitch authorization callback is missing the required code or redirect URI context.");
    }

    await accessTokenProvider.ExchangeAuthorizationCodeAsync(code, redirectUri, cancellationToken);
    var authorizationState = await accessTokenProvider.GetUserAuthorizationStateAsync(cancellationToken);
    runtimeStatusStore.UpdateTwitchUserAuthorization(
        authorizationState.IsConfigured,
        authorizationState.IsValid,
        authorizationState.Validity,
        authorizationState.Detail,
        authorizationState.TwitchUserLogin,
        authorizationState.ExpiresUtc,
        authorizationState.LastValidatedUtc);

    return Results.Content(
        "<html><body><h1>Twitch authorization complete</h1><p>The Twitch user token was stored successfully.</p><p><a href=\"/diagnostics\">Return to diagnostics</a></p></body></html>",
        "text/html");
});

app.MapRazorPages();

app.Run();

static string BuildTwitchOAuthRedirectUri(HttpRequest request)
{
    var host = request.Host.Host;
    if (string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(host, "::1", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(host, "[::1]", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
    {
        host = "localhost";
    }

    var port = request.Host.Port.HasValue ? $":{request.Host.Port.Value}" : string.Empty;
    return $"{request.Scheme}://{host}{port}/auth/twitch/callback";
}

public partial class Program;
