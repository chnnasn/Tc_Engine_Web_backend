using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Data.Sqlite;
using TomCat.Api;

var builder = WebApplication.CreateBuilder(args);
if (Environment.GetEnvironmentVariable("PORT") is { Length: > 0 } port)
{
    if (!int.TryParse(port, out var railwayPort) || railwayPort is < 1 or > 65535)
        throw new InvalidOperationException("PORT must be a valid TCP port.");
    builder.WebHost.UseUrls($"http://0.0.0.0:{railwayPort}");
}
// Console logging works for local development and container hosts without Windows Event Log privileges.
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole();
var dataDirectory = Path.GetFullPath(builder.Configuration["Storage:Directory"] ?? "App_Data", builder.Environment.ContentRootPath);
Directory.CreateDirectory(dataDirectory);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 16 * 1024 * 1024);
builder.Services.AddSingleton(new Database(dataDirectory));
builder.Services.AddSingleton<IPasswordHasher<UserRow>, PasswordHasher<UserRow>>();
builder.Services.AddDataProtection().SetApplicationName("TomCat.Api")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDirectory, "keys")));
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
{
    options.Cookie.Name = "tomcat.session";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
    options.SlidingExpiration = false;
    options.Events.OnRedirectToLogin = context => { context.Response.StatusCode = 401; return Task.CompletedTask; };
    options.Events.OnRedirectToAccessDenied = context => { context.Response.StatusCode = 403; return Task.CompletedTask; };
});
builder.Services.AddAuthorization();
builder.Services.AddProblemDetails();
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    options.AddPolicy("auth", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
        { PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});

var app = builder.Build();
app.Services.GetRequiredService<Database>().Initialize();
app.UseForwardedHeaders();
app.UseExceptionHandler();
app.Use(async (context, next) =>
{
    // A custom header + no cross-origin CORS prevents cookie-authenticated cross-site writes.
    if (context.Request.Path.StartsWithSegments("/v1") &&
        !HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method) &&
        context.Request.Headers["X-TomCat-Request"] != "1")
    {
        context.Response.StatusCode = 403;
        await context.Response.WriteAsJsonAsync(new { error = "Missing X-TomCat-Request header." });
        return;
    }
    context.Response.Headers.CacheControl = "no-store";
    await next(context);
});
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapPost("/v1/auth/register", async (Credentials input, Database db, IPasswordHasher<UserRow> hasher, HttpContext context) =>
{
    if (input.Username is null || !Regex.IsMatch(input.Username, "^[a-zA-Z0-9_]{3,32}$") ||
        input.Password is null || input.Password.Length is < 12 or > 128)
        return Results.BadRequest(new { error = "用户名须为 3–32 个字母、数字或下划线；密码须为 12–128 个字符。" });
    var user = new UserRow(Database.Id(), input.Username, "");
    user = user with { PasswordHash = hasher.HashPassword(user, input.Password) };
    try { db.CreateUser(user); }
    catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
    { return Results.Conflict(new { error = "用户名已存在。" }); }
    await SignIn(context, user);
    return Results.Created("/v1/auth/me", new { user.Id, user.Username });
}).RequireRateLimiting("auth");

app.MapPost("/v1/auth/login", async (Credentials input, Database db, IPasswordHasher<UserRow> hasher, HttpContext context) =>
{
    if (string.IsNullOrWhiteSpace(input.Username) || input.Username.Length > 32 || input.Password is null || input.Password.Length > 128)
        return Results.Unauthorized();
    var user = db.FindUser(input.Username);
    if (user is null || hasher.VerifyHashedPassword(user, user.PasswordHash, input.Password) == PasswordVerificationResult.Failed)
        return Results.Unauthorized();
    await SignIn(context, user);
    return Results.Ok(new { user.Id, user.Username });
}).RequireRateLimiting("auth");

app.MapGet("/v1/auth/me", (HttpContext context) => Results.Ok(new { id = Owner(context), username = context.User.Identity!.Name }))
    .RequireAuthorization();
app.MapPost("/v1/auth/logout", async (HttpContext context) =>
{
    await context.SignOutAsync();
    return Results.NoContent();
}).RequireAuthorization();

var projects = app.MapGroup("/v1/projects").RequireAuthorization();
ProjectFiles.Map(projects);
projects.MapGet("/", (Database db, HttpContext context) => db.ListProjects(Owner(context)));
projects.MapPost("/", (ProjectInput input, Database db, HttpContext context) =>
{
    if (!ValidProject(input)) return Results.BadRequest(new { error = "项目名须为 1–64 字，描述不超过 1000 字，模板须为 2D 或空白。" });
    var project = db.CreateProject(Owner(context), input);
    return Results.Created($"/v1/projects/{project.Id}", project);
});
projects.MapGet("/{id}", (string id, Database db, HttpContext context) =>
{
    using var connection = db.Open();
    var project = Database.GetProject(connection, Owner(context), id);
    if (project?.Etag is { } etag) context.Response.Headers.ETag = etag;
    return project is null ? Results.NotFound() : Results.Ok(project);
});
projects.MapPut("/{id}", (string id, ProjectInput input, Database db, HttpContext context) =>
{
    if (!ValidProject(input)) return Results.BadRequest();
    using var connection = db.Open();
    using var command = Database.Command(connection,
        "UPDATE projects SET name = $name, description = $description, updated_at = $now WHERE id = $id AND owner_id = $owner;", null,
        ("$name", input.Name!.Trim()), ("$description", input.Description?.Trim() ?? ""),
        ("$now", Database.Now()), ("$id", id), ("$owner", Owner(context)));
    return command.ExecuteNonQuery() == 0 ? Results.NotFound() : Results.Ok(Database.GetProject(connection, Owner(context), id));
});
projects.MapDelete("/{id}", (string id, Database db, HttpContext context) =>
{
    using var connection = db.Open();
    using var command = Database.Command(connection, "DELETE FROM projects WHERE id = $id AND owner_id = $owner;", null,
        ("$id", id), ("$owner", Owner(context)));
    return command.ExecuteNonQuery() == 0 ? Results.NotFound() : Results.NoContent();
});

projects.MapPost("/{id}/revisions", (string id, JsonElement payload, Database db, HttpContext context) =>
{
    if (!ValidRevision(payload, out var files)) return Results.BadRequest(new { error = "无效或不完整的项目修订。" });
    using var connection = db.Open();
    // Acquire the writer lock before reading ETag so two writers cannot both accept the same revision.
    using var transaction = connection.BeginTransaction(deferred: false);
    var project = Database.GetProject(connection, Owner(context), id, transaction);
    if (project is null) return Results.NotFound();
    if (project.Etag is { } current) context.Response.Headers.ETag = current;
    var match = context.Request.Headers.IfMatch.ToString();
    var noneMatch = context.Request.Headers.IfNoneMatch.ToString();
    if (match.Length == 0 && noneMatch.Length == 0) return Results.StatusCode(428);
    var matches = project.Etag is null ? noneMatch == "*" && match.Length == 0 : match == project.Etag && noneMatch.Length == 0;
    if (!matches) return Results.StatusCode(412);
    foreach (var file in files)
    {
        using var check = Database.Command(connection,
            "SELECT COUNT(*) FROM uploads WHERE id=$upload AND project_id=$project AND content_hash=$hash AND byte_length=$size;", transaction,
            ("$upload", file.UploadId), ("$project", id), ("$hash", file.ContentHash), ("$size", file.Size));
        if (Convert.ToInt32(check.ExecuteScalar()) != 1) return Results.BadRequest(new { error = "修订引用了缺失、内容不匹配或不属于此项目的资源。" });
    }
    var revision = new SavedRevision(id, Database.Id(), "", Database.Now());
    revision = revision with { Etag = $"\"{revision.RevisionId}\"" };
    using (var insert = Database.Command(connection, "INSERT INTO revisions VALUES ($id, $project, $payload, $now);", transaction,
        ("$id", revision.RevisionId), ("$project", id), ("$payload", payload.GetRawText()), ("$now", revision.CreatedAt))) insert.ExecuteNonQuery();
    foreach (var file in files)
    {
        using var reference = Database.Command(connection, "INSERT INTO revision_files VALUES($revision,$path,$upload);", transaction,
            ("$revision", revision.RevisionId), ("$path", file.Path), ("$upload", file.UploadId));
        reference.ExecuteNonQuery();
    }
    using (var update = Database.Command(connection, "UPDATE projects SET current_revision_id = $revision, updated_at = $now WHERE id = $id;", transaction,
        ("$revision", revision.RevisionId), ("$now", revision.CreatedAt), ("$id", id))) update.ExecuteNonQuery();
    transaction.Commit();
    context.Response.Headers.ETag = revision.Etag;
    return Results.Created($"/v1/projects/{id}/revisions/{revision.RevisionId}", revision);
});
projects.MapGet("/{id}/revisions", (string id, Database db, HttpContext context) =>
{
    using var connection = db.Open();
    if (Database.GetProject(connection, Owner(context), id) is null) return Results.NotFound();
    using var command = Database.Command(connection, "SELECT id, created_at FROM revisions WHERE project_id = $id ORDER BY created_at DESC;", null, ("$id", id));
    using var reader = command.ExecuteReader();
    var revisions = new List<SavedRevision>();
    while (reader.Read()) revisions.Add(new(id, reader.GetString(0), $"\"{reader.GetString(0)}\"", reader.GetString(1)));
    return Results.Ok(revisions);
});
projects.MapGet("/{id}/revisions/{revisionId}", (string id, string revisionId, Database db, HttpContext context) =>
{
    using var connection = db.Open();
    using var command = Database.Command(connection,
        "SELECT r.payload FROM revisions r JOIN projects p ON p.id = r.project_id WHERE p.owner_id = $owner AND p.id = $id AND r.id = $revision;", null,
        ("$owner", Owner(context)), ("$id", id), ("$revision", revisionId));
    if (command.ExecuteScalar() is not string json) return Results.NotFound();
    context.Response.Headers.ETag = $"\"{revisionId}\"";
    return Results.Content(json, "application/json");
});
app.Run();

static string Owner(HttpContext context) => context.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
static Task SignIn(HttpContext context, UserRow user) => context.SignInAsync(new ClaimsPrincipal(new ClaimsIdentity(
    [new Claim(ClaimTypes.NameIdentifier, user.Id), new Claim(ClaimTypes.Name, user.Username)], CookieAuthenticationDefaults.AuthenticationScheme)));
static bool ValidProject(ProjectInput input) => input.Name?.Trim().Length is > 0 and <= 64 &&
    (input.Description?.Length ?? 0) <= 1000 && (input.Template is null or "2D" or "空白");
static bool ValidRevision(JsonElement payload, out List<RevisionFile> files)
{
    files = [];
    if (payload.ValueKind != JsonValueKind.Object ||
        !payload.TryGetProperty("schemaVersion", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number)) return false;
    if (number == 2) return ProjectFiles.TryManifest(payload, out files);
    if (number != 1 ||
        !payload.TryGetProperty("project", out var project) || project.ValueKind != JsonValueKind.String ||
        !payload.TryGetProperty("settings", out var settings) || !StringMap(settings) ||
        !payload.TryGetProperty("scenes", out var scenes) || !StringMap(scenes) ||
        !payload.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return false;
    // Existing v1 revisions remain readable; new resource-bearing revisions must use v2.
    return assets.GetArrayLength() == 0;
}
static bool StringMap(JsonElement element) => element.ValueKind == JsonValueKind.Object &&
    element.EnumerateObject().All(property => property.Value.ValueKind == JsonValueKind.String);
