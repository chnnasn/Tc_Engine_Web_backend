using System.Security.Claims;
using System.Text.Json;
using StackExchange.Redis;

namespace TomCat.Api;

// Like the SQLite volume, this coordinator is single API instance only.
// Every writer (including explicit saves and deletion) shares the same gate.
public sealed class WorkingStates(Database db, IConfiguration config, ILogger<WorkingStates> logger) : BackgroundService
{
    public bool Enabled => !string.IsNullOrWhiteSpace(config["Redis:ConnectionString"]);
    private readonly SemaphoreSlim gate = new(1, 1);
    private ConnectionMultiplexer? connection;
    private string Prefix => config["Redis:KeyPrefix"] ?? "tomcat:";
    private RedisKey Key(string id) => Prefix + "project:" + id + ":state";
    private RedisKey Dirty => Prefix + "dirty";
    private sealed record State(string Owner, string Project, string Revision, string? BaseRevision, string Payload, string CreatedAt);

    private async Task<IDatabase> Redis()
    {
        if (connection is null)
        {
            var options = ConfigurationOptions.Parse(config["Redis:ConnectionString"]!);
            options.AbortOnConnectFail = false;
            options.AsyncTimeout = 5000;
            options.ConnectTimeout = 5000;
            connection = await ConnectionMultiplexer.ConnectAsync(options);
        }
        return connection.GetDatabase();
    }
    private async Task<State?> Read(string id)
    {
        var value = await (await Redis()).StringGetAsync(Key(id));
        return value.IsNull ? null : JsonSerializer.Deserialize<State>((string)value!);
    }
    private async Task Put(State state)
    {
        await (await Redis()).ScriptEvaluateAsync(
            "redis.call('SET', KEYS[1], ARGV[1]); redis.call('SADD', KEYS[2], ARGV[2]); return 1",
            [Key(state.Project), Dirty], [JsonSerializer.Serialize(state), state.Project]);
    }
    private async Task Remove(string id)
    {
        await (await Redis()).ScriptEvaluateAsync(
            "redis.call('DEL', KEYS[1]); redis.call('SREM', KEYS[2], ARGV[1]); return 1",
            [Key(id), Dirty], [id]);
    }
    private static string Owner(HttpContext context) => context.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
    private static string Etag(string revision) => $"\"{revision}\"";
    private static bool Matches(HttpContext context, string? revision)
    {
        var match = context.Request.Headers.IfMatch.ToString();
        var none = context.Request.Headers.IfNoneMatch.ToString();
        return revision is null ? match == "" && none == "*" : match == Etag(revision) && none == "";
    }
    private static bool ValidFiles(Microsoft.Data.Sqlite.SqliteConnection sql, string id, List<RevisionFile> files,
        Microsoft.Data.Sqlite.SqliteTransaction? tx = null)
    {
        foreach (var file in files)
        {
            using var check = Database.Command(sql,
                "SELECT COUNT(*) FROM uploads WHERE id=$upload AND project_id=$project AND content_hash=$hash AND byte_length=$size;", tx,
                ("$upload", file.UploadId), ("$project", id), ("$hash", file.ContentHash), ("$size", file.Size));
            if (Convert.ToInt32(check.ExecuteScalar()) != 1) return false;
        }
        return true;
    }
    public async Task<IResult> Write(string id, JsonElement payload, List<RevisionFile> files, HttpContext context, bool flush)
    {
        if (!Enabled) return Results.Json(new { error = "服务器未启用自动同步。" }, statusCode: 503);
        await gate.WaitAsync(context.RequestAborted);
        try
        {
            using var sql = db.Open();
            var project = Database.GetProject(sql, Owner(context), id);
            if (project is null) return Results.NotFound();
            var previous = await Read(id);
            var revision = previous?.Revision ?? project.CurrentRevisionId;
            if (revision is not null) context.Response.Headers.ETag = Etag(revision);
            if (context.Request.Headers.IfMatch.Count == 0 && context.Request.Headers.IfNoneMatch.Count == 0) return Results.StatusCode(428);
            if (!Matches(context, revision)) return Results.StatusCode(412);
            if (!ValidFiles(sql, id, files)) return Results.BadRequest(new { error = "修订资源缺失或不属于此项目。" });
            // A commit may have succeeded before a Redis cleanup failure. Preserve that baseline.
            var baseline = previous is not null && previous.Revision != project.CurrentRevisionId ? previous.BaseRevision : project.CurrentRevisionId;
            if (baseline != project.CurrentRevisionId) return Results.StatusCode(412);
            var state = new State(Owner(context), id, Database.Id(), baseline, payload.GetRawText(), Database.Now());
            await Put(state);
            if (flush) await Persist(state);
            context.Response.Headers.ETag = Etag(state.Revision);
            return Results.Json(new { projectId = id, revisionId = state.Revision, etag = Etag(state.Revision), createdAt = state.CreatedAt, persisted = flush }, statusCode: flush ? 201 : 202);
        }
        finally { gate.Release(); }
    }
    // Idempotent after a crash between SQLite COMMIT and Redis cleanup.
    private async Task Persist(State state)
    {
        using var sql = db.Open();
        using var tx = sql.BeginTransaction(deferred: false);
        var project = Database.GetProject(sql, state.Owner, state.Project, tx);
        if (project is null) { tx.Rollback(); await Remove(state.Project); return; }
        if (project.CurrentRevisionId == state.Revision) { tx.Rollback(); await Remove(state.Project); return; }
        if (project.CurrentRevisionId != state.BaseRevision) throw new InvalidOperationException("Working state baseline conflict; snapshot retained in Redis.");
        using var document = JsonDocument.Parse(state.Payload);
        var files = new List<RevisionFile>();
        if (document.RootElement.GetProperty("schemaVersion").GetInt32() == 2 &&
            (!ProjectFiles.TryManifest(document.RootElement, out files) || !ValidFiles(sql, state.Project, files, tx)))
            throw new InvalidOperationException("Working state has missing resources; snapshot retained in Redis.");
        using (var insert = Database.Command(sql, "INSERT INTO revisions VALUES($id,$project,$payload,$now);", tx,
            ("$id", state.Revision), ("$project", state.Project), ("$payload", state.Payload), ("$now", state.CreatedAt))) insert.ExecuteNonQuery();
        foreach (var file in files)
        {
            using var insert = Database.Command(sql, "INSERT INTO revision_files VALUES($revision,$path,$upload);", tx,
                ("$revision", state.Revision), ("$path", file.Path), ("$upload", file.UploadId));
            insert.ExecuteNonQuery();
        }
        using (var update = Database.Command(sql, "UPDATE projects SET current_revision_id=$revision,updated_at=$now WHERE id=$project;", tx,
            ("$revision", state.Revision), ("$now", state.CreatedAt), ("$project", state.Project))) update.ExecuteNonQuery();
        tx.Commit();
        await Remove(state.Project);
    }
    public async Task<ProjectRow> Latest(ProjectRow project)
    {
        if (!Enabled) return project;
        await gate.WaitAsync();
        try
        {
            var state = await Read(project.Id);
            return state is null ? project : project with { CurrentRevisionId = state.Revision, UpdatedAt = state.CreatedAt };
        }
        finally { gate.Release(); }
    }
    public async Task<IResult> Restore(string id, HttpContext context)
    {
        await gate.WaitAsync(context.RequestAborted);
        try
        {
            using var sql = db.Open();
            var project = Database.GetProject(sql, Owner(context), id);
            if (project is null) return Results.NotFound();
            var state = Enabled ? await Read(id) : null;
            var revision = state?.Revision ?? project.CurrentRevisionId;
            if (revision is null) return Results.NotFound();
            context.Response.Headers.ETag = Etag(revision);
            if (state is not null) return Results.Content(state.Payload, "application/json");
            using var read = Database.Command(sql, "SELECT payload FROM revisions WHERE id=$id AND project_id=$project;", null,
                ("$id", revision), ("$project", id));
            return Results.Content((string)read.ExecuteScalar()!, "application/json");
        }
        finally { gate.Release(); }
    }
    public async Task<IResult> Status(string id, HttpContext context)
    {
        await gate.WaitAsync(context.RequestAborted);
        try
        {
            using var sql = db.Open();
            var project = Database.GetProject(sql, Owner(context), id);
            if (project is null) return Results.NotFound();
            var state = Enabled ? await Read(id) : null;
            var revision = state?.Revision ?? project.CurrentRevisionId;
            return Results.Ok(new { etag = revision is null ? null : Etag(revision), persisted = state is null || state.Revision == project.CurrentRevisionId });
        }
        finally { gate.Release(); }
    }
    public async Task<IResult> Delete(string id, HttpContext context)
    {
        await gate.WaitAsync(context.RequestAborted);
        try
        {
            using var sql = db.Open();
            if (Database.GetProject(sql, Owner(context), id) is null) return Results.NotFound();
            // Delete the DB first: a surviving Redis entry can never resurrect a deleted project.
            using var delete = Database.Command(sql, "DELETE FROM projects WHERE id=$id;", null, ("$id", id));
            delete.ExecuteNonQuery();
            if (Enabled) await Remove(id);
            return Results.NoContent();
        }
        finally { gate.Release(); }
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Enabled) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Clamp(config.GetValue("Redis:FlushIntervalSeconds", 30), 1, 3600)));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await gate.WaitAsync(stoppingToken);
            try
            {
                foreach (var id in await (await Redis()).SetMembersAsync(Dirty))
                {
                    try
                    {
                        var state = await Read(id.ToString());
                        if (state is not null) await Persist(state);
                        else await (await Redis()).SetRemoveAsync(Dirty, id);
                    }
                    catch (Exception error) { logger.LogError(error, "Could not persist project {Project}; will retry", id.ToString()); }
                }
            }
            catch (RedisException error) { logger.LogWarning(error, "Redis unavailable; pending snapshots will be retried"); }
            finally { gate.Release(); }
        }
    }
    public override void Dispose() { connection?.Dispose(); base.Dispose(); }
}
