using TwitchArchivist.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseWindowsService();

builder.Services.AddRouting(options => options.LowercaseUrls = true);
builder.Services.AddTwitchArchivistPersistence();

var app = builder.Build();

app.MapGet("/", () => Results.Json(new
{
    service = "TwitchArchivist",
    status = "ok",
    environment = app.Environment.EnvironmentName
}));

app.MapGet("/healthz", () => Results.Ok(new
{
    status = "healthy",
    service = "TwitchArchivist"
}));

app.Run();

public partial class Program;
