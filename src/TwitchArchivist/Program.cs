using TwitchArchivist.Models;
using TwitchArchivist.Persistence;
using TwitchArchivist.Services;
using TwitchArchivist.Services.Twitch;
using TwitchLib.EventSub.Websockets.Extensions;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseWindowsService();

builder.Services.Configure<TwitchOptions>(builder.Configuration.GetSection(TwitchOptions.SectionName));
builder.Services.Configure<DownloaderOptions>(builder.Configuration.GetSection(DownloaderOptions.SectionName));
builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection(StorageOptions.SectionName));

builder.Services.AddRouting(options => options.LowercaseUrls = true);
builder.Services.AddRazorPages();
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
    eventSubConnectionState = runtimeStatusStore.EventSubConnectionState
}));

app.MapRazorPages();

app.Run();

public partial class Program;
