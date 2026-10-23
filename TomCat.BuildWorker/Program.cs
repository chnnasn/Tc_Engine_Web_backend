using TomCat.Build;
using TomCat.BuildWorker;

var builder = WebApplication.CreateBuilder(args);
if (Environment.GetEnvironmentVariable("PORT") is { Length: > 0 } port)
{
    if (!int.TryParse(port, out var number) || number is < 1 or > 65535) throw new InvalidOperationException("Invalid PORT.");
    builder.WebHost.UseUrls($"http://[::]:{number}");
}
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole();
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 16384);
builder.Services.AddSingleton<WorkerSettings>();
builder.Services.AddSingleton<BuildRunner>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<BuildRunner>());
builder.Services.AddHttpClient("build-api", client => client.Timeout = Timeout.InfiniteTimeSpan)
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false });
var app = builder.Build();
app.MapGet("/health", () => Results.Ok(new { status = "ok", role = "build-worker" }));
app.MapPost("/internal/builds/events", (BuildRequested requested, WorkerSettings settings, BuildRunner runner, HttpContext context) =>
{
    if (!BuildProtocol.Authorized(context.Request, settings.Secret)) return Results.Unauthorized();
    if (requested.Protocol != BuildProtocol.Version || !BuildProtocol.Id(requested.JobId) || !BuildProtocol.Hash(requested.LeaseToken) ||
        requested.LeaseSeconds is < 5 or > 300 || requested.TimeoutSeconds is < 30 or > 7200) return Results.BadRequest();
    return runner.Enqueue(requested) ? Results.Accepted() : Results.StatusCode(503);
});
app.Run();
