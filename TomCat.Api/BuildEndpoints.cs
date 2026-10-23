using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Data.Sqlite;
using TomCat.Build;

namespace TomCat.Api;

public static class BuildEndpoints
{
    private sealed record Job(string Project, string Revision, string Status, string Sha);

    // A lease authorizes exactly one attempt of one still-current publication. It never
    // grants general access to project uploads, and cancellation revokes it immediately.
    private static Job? ReadJob(SqliteConnection connection, string id, HttpContext context, SqliteTransaction? transaction = null)
    {
        var lease = context.Request.Headers[BuildProtocol.LeaseHeader].ToString();
        if (!BuildProtocol.Id(id) || !BuildProtocol.Hash(lease)) return null;
        using var command = Database.Command(connection, """
            SELECT j.project_id, j.revision_id, j.status, j.lease_token, j.lease_until, j.deadline, p.sha256
            FROM build_jobs j JOIN publications p ON p.build_job_id=j.id AND p.project_id=j.project_id AND p.revision_id=j.revision_id
            WHERE j.id=$id AND ((j.status='running' AND p.status='pending') OR (j.status='completed' AND p.status='published') OR (j.status='failed' AND p.status='failed'))
            AND NOT EXISTS(SELECT 1 FROM publication_moderation m WHERE m.project_id=j.project_id);
            """, transaction, ("$id", id));
        using var reader = command.ExecuteReader();
        if (!reader.Read() || !BuildProtocol.Equal(reader.GetString(3), lease)) return null;
        if (reader.GetString(2) == "running" && (reader.GetInt64(4) <= BuildDispatcher.Now || reader.GetInt64(5) <= BuildDispatcher.Now)) return null;
        return new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(6));
    }

    private static BuildInput Input(SqliteConnection connection, Job job)
    {
        using var command = Database.Command(connection, "SELECT payload FROM revisions WHERE id=$revision AND project_id=$project;",
            null, ("$revision", job.Revision), ("$project", job.Project));
        using var document = JsonDocument.Parse(command.ExecuteScalar() as string ?? throw new InvalidOperationException("构建修订不存在。"));
        var root = document.RootElement;
        if (!ProjectFiles.TryManifest(root, out var files)) throw new InvalidOperationException("构建修订缺少完整资源清单。");
        return new(root.GetProperty("engineCommit").GetString()!, root.GetProperty("sceneHandle").GetString()!,
            root.GetProperty("archive").GetString()!, files.Select(f => new BuildFile(f.Path, f.UploadId, f.ContentHash, f.Size)).ToList());
    }

    public static void Map(WebApplication app)
    {
        var routes = app.MapGroup("/internal/builds");
        routes.AddEndpointFilter(async (context, next) =>
        {
            var settings = context.HttpContext.RequestServices.GetRequiredService<BuildSettings>();
            if (!BuildProtocol.Authorized(context.HttpContext.Request, settings.Secret)) return Results.Unauthorized();
            return await next(context);
        });
        routes.MapGet("/{id}/input", (string id, Database db, HttpContext context) =>
        {
            using var connection = db.Open();
            var job = ReadJob(connection, id, context);
            return job is { Status: "running" } ? Results.Ok(Input(connection, job)) : Results.Conflict();
        });
        routes.MapGet("/{id}/files/{uploadId}", (string id, string uploadId, Database db, HttpContext context) =>
        {
            using var connection = db.Open();
            var job = ReadJob(connection, id, context);
            if (job is not { Status: "running" }) return Results.Conflict();
            using var command = Database.Command(connection, """
                SELECT u.content FROM uploads u JOIN revision_files f ON f.upload_id=u.id
                WHERE u.id=$id AND u.project_id=$project AND f.revision_id=$revision LIMIT 1;
                """, null, ("$id", uploadId), ("$project", job.Project), ("$revision", job.Revision));
            return command.ExecuteScalar() is byte[] bytes ? Results.File(bytes, "application/octet-stream") : Results.NotFound();
        });
        routes.MapPost("/{id}/heartbeat", (string id, Database db, BuildSettings settings, HttpContext context) =>
        {
            using var connection = db.Open();
            using var transaction = connection.BeginTransaction();
            if (ReadJob(connection, id, context, transaction) is not { Status: "running" }) return Results.Conflict();
            using var command = Database.Command(connection, "UPDATE build_jobs SET lease_until=MIN($until,deadline) WHERE id=$id;",
                transaction, ("$id", id), ("$until", BuildDispatcher.Now + settings.LeaseSeconds));
            command.ExecuteNonQuery();
            transaction.Commit();
            return Results.NoContent();
        });
        routes.MapPost("/{id}/failed", async (string id, Database db, HttpContext context) =>
        {
            if (context.Request.ContentLength is null or > 8192) return Results.BadRequest();
            var failure = await context.Request.ReadFromJsonAsync<BuildFailure>(context.RequestAborted);
            if (failure?.Error is null || failure.Error.Length > 4000) return Results.BadRequest();
            using var connection = db.Open();
            using var transaction = connection.BeginTransaction();
            var job = ReadJob(connection, id, context, transaction);
            if (job is { Status: "failed" }) return Results.NoContent();
            if (job is not { Status: "running" }) return Results.Conflict();
            using var command = Database.Command(connection, """
                UPDATE publications SET status='failed', error=$error WHERE build_job_id=$id AND status='pending';
                UPDATE build_jobs SET status='failed', finished_at=$now WHERE id=$id;
                """, transaction, ("$id", id), ("$error", BuildProtocol.Tail(failure.Error)), ("$now", Database.Now()));
            command.ExecuteNonQuery();
            transaction.Commit();
            return Results.NoContent();
        });
        routes.MapPost("/{id}/complete", Complete);
    }

    private static async Task<IResult> Complete(string id, Database db, HttpContext context)
    {
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit) limit.MaxRequestBodySize = BuildProtocol.MaxPackage;
        var sha = context.Request.Headers[BuildProtocol.HashHeader].ToString();
        if (!BuildProtocol.Hash(sha) || context.Request.ContentLength is not > 0 or > BuildProtocol.MaxPackage)
            return Results.BadRequest();
        using (var connection = db.Open())
        {
            var job = ReadJob(connection, id, context);
            if (job is { Status: "completed" }) return job.Sha == sha ? Results.NoContent() : Results.Conflict();
            if (job is not { Status: "running" }) return Results.Conflict();
        }
        // Stream to disk before acquiring the database write lock. Slow LAN transfers
        // neither lock SQLite nor allocate the entire (up to 256 MiB) package in the API.
        var temporary = Path.Combine(Path.GetTempPath(), "tomcat-result-" + Guid.NewGuid().ToString("N"));
        await using var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536,
            FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536];
        long length = 0;
        int count;
        while ((count = await context.Request.Body.ReadAsync(buffer, context.RequestAborted)) > 0)
        {
            length += count;
            if (length > BuildProtocol.MaxPackage) return Results.StatusCode(413);
            hash.AppendData(buffer, 0, count);
            await file.WriteAsync(buffer.AsMemory(0, count), context.RequestAborted);
        }
        if (length != context.Request.ContentLength || Convert.ToHexStringLower(hash.GetHashAndReset()) != sha) return Results.BadRequest();
        file.Position = 0;
        using (var connection = db.Open())
        using (var transaction = connection.BeginTransaction())
        {
            var job = ReadJob(connection, id, context, transaction);
            if (job is { Status: "completed" }) return job.Sha == sha ? Results.NoContent() : Results.Conflict();
            if (job is not { Status: "running" }) return Results.Conflict();
            using var reserve = Database.Command(connection,
                "UPDATE publications SET package=zeroblob($length) WHERE build_job_id=$id; SELECT rowid FROM publications WHERE build_job_id=$id;",
                transaction, ("$id", id), ("$length", length));
            var row = (long)reserve.ExecuteScalar()!;
            using (var blob = new SqliteBlob(connection, "publications", "package", row))
                await file.CopyToAsync(blob, context.RequestAborted);
            using var complete = Database.Command(connection, """
                UPDATE publications SET status='published', error='', byte_length=$length, sha256=$sha, published_at=$now WHERE build_job_id=$id;
                UPDATE build_jobs SET status='completed', finished_at=$now WHERE id=$id;
                """, transaction, ("$id", id), ("$length", length), ("$sha", sha), ("$now", Database.Now()));
            complete.ExecuteNonQuery();
            transaction.Commit();
        }
        return Results.NoContent();
    }
}
