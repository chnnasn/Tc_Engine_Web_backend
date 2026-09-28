using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Routing;

namespace TomCat.Api;

// Publishing turns the latest saved revision of a project into a publicly playable game
// package. These endpoints only manage the publication row; the cook itself runs in
// CookWorker and fills in package/sha256 when it succeeds.
public static class PublishEndpoints
{
    public sealed record PublishInput(string? Title, string? Description);

    public static void Map(RouteGroupBuilder projects, WebApplication app)
    {
        projects.MapPost("/{id}/publish", async (string id, PublishInput input, Database db, WorkingStates states,
            IConfiguration config, HttpContext context) =>
        {
            var cli = config["Cook:CliPath"];
            if (string.IsNullOrWhiteSpace(cli) || !File.Exists(cli))
                return Results.Json(new { error = "服务器未配置打包工具（Cook:CliPath），暂时无法发布。" }, statusCode: 503);
            var title = input.Title?.Trim() ?? "";
            if (title.Length is 0 or > 64 || (input.Description?.Length ?? 0) > 1000)
                return Results.BadRequest(new { error = "标题须为 1–64 字，描述不超过 1000 字。" });
            var owner = context.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
            using (var connection = db.Open())
            {
                if (Database.GetProject(connection, owner, id) is null) return Results.NotFound();
            }
            try { await states.Flush(id, context); }
            catch (InvalidOperationException)
            {
                return Results.Conflict(new { error = "自动同步快照与云端基线冲突，请先在编辑器中重新保存，再发布。" });
            }
            string revision;
            string engineCommit;
            using (var connection = db.Open())
            {
                var project = Database.GetProject(connection, owner, id);
                if (project?.CurrentRevisionId is not { Length: > 0 } current)
                    return Results.BadRequest(new { error = "请先在编辑器中保存一次完整项目，再发布。" });
                using var command = Database.Command(connection,
                    "SELECT payload FROM revisions WHERE id=$revision AND project_id=$project;", null,
                    ("$revision", current), ("$project", id));
                if (command.ExecuteScalar() is not string payload)
                    return Results.BadRequest(new { error = "请先在编辑器中保存一次完整项目，再发布。" });
                using var document = JsonDocument.Parse(payload);
                if (!document.RootElement.TryGetProperty("engineCommit", out var commit) || commit.ValueKind != JsonValueKind.String)
                    return Results.BadRequest(new { error = "此修订缺少引擎版本信息，请在编辑器中重新保存后再发布。" });
                revision = current;
                engineCommit = commit.GetString()!;
            }
            using (var connection = db.Open())
            using (var transaction = connection.BeginTransaction())
            {
                using var pending = Database.Command(connection,
                    "SELECT COUNT(*) FROM publications WHERE project_id=$project AND status='pending';", transaction, ("$project", id));
                if (Convert.ToInt32(pending.ExecuteScalar()) > 0)
                    return Results.Conflict(new { error = "此项目已有发布任务在进行中，请稍候后再试。" });
                var now = Database.Now();
                using var upsert = Database.Command(connection,
                    """
                    INSERT INTO publications (project_id, revision_id, engine_commit, title, description, status, error, package, byte_length, sha256, requested_at, published_at)
                    VALUES ($project, $revision, $commit, $title, $description, 'pending', '', NULL, 0, '', $now, '')
                    ON CONFLICT(project_id) DO UPDATE SET revision_id=$revision, engine_commit=$commit, title=$title, description=$description,
                        status='pending', error='', package=NULL, byte_length=0, sha256='', requested_at=$now, published_at=''
                    """, transaction,
                    ("$project", id), ("$revision", revision), ("$commit", engineCommit), ("$title", title),
                    ("$description", input.Description?.Trim() ?? ""), ("$now", now));
                upsert.ExecuteNonQuery();
                transaction.Commit();
            }
            return Results.Accepted($"/v1/projects/{id}/publish", new
            {
                projectId = id,
                revisionId = revision,
                engineCommit,
                title,
                description = input.Description?.Trim() ?? "",
                status = "pending",
                error = "",
                byteLength = 0L,
                etag = (string?)null,
                requestedAt = Database.Now(),
                publishedAt = "",
            });
        });

        projects.MapGet("/{id}/publish", (string id, Database db, HttpContext context) =>
        {
            using var connection = db.Open();
            if (Database.GetProject(connection, context.User.FindFirstValue(ClaimTypes.NameIdentifier)!, id) is null)
                return Results.NotFound();
            using var command = Database.Command(connection,
                "SELECT revision_id, engine_commit, title, description, status, error, byte_length, sha256, requested_at, published_at FROM publications WHERE project_id=$project;",
                null, ("$project", id));
            using var reader = command.ExecuteReader();
            return reader.Read()
                ? Results.Ok(Read(reader, id))
                : Results.NotFound();
        });

        projects.MapDelete("/{id}/publish", (string id, Database db, HttpContext context) =>
        {
            using var connection = db.Open();
            if (Database.GetProject(connection, context.User.FindFirstValue(ClaimTypes.NameIdentifier)!, id) is null)
                return Results.NotFound();
            using var command = Database.Command(connection, "DELETE FROM publications WHERE project_id=$project;", null, ("$project", id));
            return command.ExecuteNonQuery() == 0 ? Results.NotFound() : Results.NoContent();
        });

        // Published games are public content: guests can list and play them without an account.
        app.MapGet("/v1/games/published", (Database db) =>
        {
            using var connection = db.Open();
            using var command = Database.Command(connection,
                "SELECT project_id, title, description, byte_length, sha256, published_at, engine_commit FROM publications WHERE status='published' ORDER BY published_at DESC;");
            using var reader = command.ExecuteReader();
            var games = new List<object>();
            while (reader.Read()) games.Add(PublicGame(reader));
            return Results.Ok(games);
        });

        app.MapGet("/v1/games/published/{id}", (string id, Database db) =>
        {
            using var connection = db.Open();
            using var command = Database.Command(connection,
                "SELECT project_id, title, description, byte_length, sha256, published_at, engine_commit FROM publications WHERE status='published' AND project_id=$project;",
                null, ("$project", id));
            using var reader = command.ExecuteReader();
            return reader.Read() ? Results.Ok(PublicGame(reader)) : Results.NotFound(new { error = "作品不存在或已取消发布。" });
        });

        app.MapGet("/v1/games/published/{id}/package", (string id, Database db, HttpContext context) =>
        {
            using var connection = db.Open();
            using var command = Database.Command(connection,
                "SELECT package, sha256 FROM publications WHERE status='published' AND project_id=$project;", null, ("$project", id));
            using var reader = command.ExecuteReader();
            if (!reader.Read() || reader.IsDBNull(0)) return Results.NotFound(new { error = "作品不存在或已取消发布。" });
            var bytes = (byte[])reader[0];
            var etag = $"\"{reader.GetString(1)}\"";
            context.Response.Headers.ETag = etag;
            // The package is immutable content; override the global no-store like the built-in games do.
            context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            if (context.Request.Headers.IfNoneMatch.ToString() == etag) return Results.StatusCode(304);
            return Results.File(bytes, "application/octet-stream", enableRangeProcessing: true);
        });
    }

    private static object PublicGame(System.Data.Common.DbDataReader reader) => new
    {
        id = reader.GetString(0),
        title = reader.GetString(1),
        description = reader.GetString(2),
        byteLength = reader.GetInt64(3),
        etag = $"\"{reader.GetString(4)}\"",
        publishedAt = reader.GetString(5),
        engineCommit = reader.GetString(6),
    };

    private static object Read(System.Data.Common.DbDataReader reader, string projectId)
    {
        var sha = reader.GetString(7);
        return new
        {
            projectId,
            revisionId = reader.GetString(0),
            engineCommit = reader.GetString(1),
            title = reader.GetString(2),
            description = reader.GetString(3),
            status = reader.GetString(4),
            error = reader.GetString(5),
            byteLength = reader.GetInt64(6),
            etag = sha.Length == 64 ? $"\"{sha}\"" : null,
            requestedAt = reader.GetString(8),
            publishedAt = reader.GetString(9),
        };
    }
}
