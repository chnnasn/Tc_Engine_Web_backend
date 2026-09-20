using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TomCat.Api;

public sealed record UploadRow(string UploadId, string ContentHash, long Size);
public sealed record RevisionFile(string Path, string UploadId, string ContentHash, long Size);

public static class ProjectFiles
{
    public const int MaxUpload = 8 * 1024 * 1024;
    public const int MaxProject = 36 * 1024 * 1024;
    public static bool Hash(string value) => Regex.IsMatch(value, "^[a-f0-9]{64}$");
    public static bool SafePath(string path)
    {
        if (path == "Project.tcproj") return true;
        if (Regex.IsMatch(path, "^ProjectSettings/[A-Za-z0-9_-]+\\.json$")) return true;
        return path.Length <= 240 && path.StartsWith("Assets/", StringComparison.Ordinal) &&
            path.Split('/').All(part => Regex.IsMatch(part, "^[A-Za-z0-9][A-Za-z0-9_.-]*$") && !part.EndsWith('.'));
    }
    public static bool TryManifest(JsonElement payload, out List<RevisionFile> files)
    {
        files = [];
        if (!payload.TryGetProperty("engineCommit", out var commit) || commit.ValueKind != JsonValueKind.String ||
            !Regex.IsMatch(commit.GetString()!, "^[a-f0-9]{40}$") ||
            !payload.TryGetProperty("sceneHandle", out var scene) || scene.ValueKind != JsonValueKind.String ||
            !Regex.IsMatch(scene.GetString()!, "^[1-9][0-9]*$") || !ulong.TryParse(scene.GetString(), out _) ||
            !payload.TryGetProperty("archive", out var archive) || archive.ValueKind != JsonValueKind.String ||
            Encoding.UTF8.GetByteCount(archive.GetString()!) is < 1 or > 4 * 1024 * 1024 ||
            !payload.TryGetProperty("files", out var entries) || entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() > 512) return false;
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var entry in entries.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object ||
                !entry.TryGetProperty("path", out var path) || path.ValueKind != JsonValueKind.String || !SafePath(path.GetString()!) || !paths.Add(path.GetString()!) ||
                !entry.TryGetProperty("uploadId", out var id) || id.ValueKind != JsonValueKind.String || !Regex.IsMatch(id.GetString()!, "^[a-f0-9]{32}$") ||
                !entry.TryGetProperty("contentHash", out var hash) || hash.ValueKind != JsonValueKind.String || !Hash(hash.GetString()!) ||
                !entry.TryGetProperty("size", out var size) || size.ValueKind != JsonValueKind.Number || !size.TryGetInt64(out var length) || length is < 0 or > MaxUpload) return false;
            total += length;
            files.Add(new(path.GetString()!, id.GetString()!, hash.GetString()!, length));
        }
        if (total > MaxProject) return false;
        string[] required = ["Project.tcproj", "ProjectSettings/BuildSettings.json", "ProjectSettings/ProjectSettings.json", "ProjectSettings/PlayerSettings.json"];
        // Canonical spelling matters to MEMFS, even on Windows clients.
        foreach (var path in required) if (!files.Exists(file => file.Path == path)) return false;
        foreach (var file in files)
        {
            if (file.Path.EndsWith(".tcmeta", StringComparison.Ordinal) && !files.Exists(other => other.Path == file.Path[..^7])) return false;
            if (new[] { ".png", ".jpg", ".jpeg", ".tga" }.Contains(System.IO.Path.GetExtension(file.Path).ToLowerInvariant()) &&
                !files.Exists(other => other.Path == file.Path + ".tcmeta")) return false;
        }
        return true;
    }

    public static void Map(RouteGroupBuilder projects)
    {
        projects.MapPut("/{id}/uploads/{contentHash}", Upload);
        projects.MapGet("/{id}/uploads/{uploadId}", (string id, string uploadId, Database db, HttpContext context) =>
        {
            using var connection = db.Open();
            using var command = Database.Command(connection,
                "SELECT u.content, u.content_hash FROM uploads u JOIN projects p ON p.id=u.project_id WHERE u.id=$upload AND p.id=$project AND p.owner_id=$owner;", null,
                ("$upload", uploadId), ("$project", id), ("$owner", context.User.FindFirstValue(ClaimTypes.NameIdentifier)));
            using var reader = command.ExecuteReader();
            if (!reader.Read()) return Results.NotFound();
            context.Response.Headers.ETag = $"\"{reader.GetString(1)}\"";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            return Results.File((byte[])reader[0], "application/octet-stream");
        });
    }

    private static async Task<IResult> Upload(string id, string contentHash, Database db, HttpContext context)
    {
        var owner = context.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        using var connection = db.Open();
        if (Database.GetProject(connection, owner, id) is null) return Results.NotFound();
        if (!Hash(contentHash)) return Results.BadRequest(new { error = "无效的 SHA-256。" });
        if (context.Request.ContentLength > MaxUpload) return Results.StatusCode(413);
        using var buffer = new MemoryStream();
        var chunk = new byte[65536];
        int read;
        while ((read = await context.Request.Body.ReadAsync(chunk, context.RequestAborted)) > 0)
        {
            if (buffer.Length + read > MaxUpload) return Results.StatusCode(413);
            buffer.Write(chunk, 0, read);
        }
        var bytes = buffer.ToArray();
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != contentHash)
            return Results.BadRequest(new { error = "上传内容与 SHA-256 不一致。" });
        using var transaction = connection.BeginTransaction(deferred: false);
        if (Database.GetProject(connection, owner, id, transaction) is null) return Results.NotFound();
        using (var cleanup = Database.Command(connection,
            "DELETE FROM uploads WHERE project_id=$project AND created_at < $cutoff AND NOT EXISTS(SELECT 1 FROM revision_files WHERE upload_id=uploads.id);", transaction,
            ("$project", id), ("$cutoff", DateTimeOffset.UtcNow.AddHours(-24).ToString("O")))) cleanup.ExecuteNonQuery();
        using (var existing = Database.Command(connection, "SELECT id, byte_length FROM uploads WHERE project_id=$project AND content_hash=$hash;", transaction,
            ("$project", id), ("$hash", contentHash)))
        {
            using var reader = existing.ExecuteReader();
            if (reader.Read()) return Results.Ok(new UploadRow(reader.GetString(0), contentHash, reader.GetInt64(1)));
        }
        using (var quota = Database.Command(connection,
            "SELECT COALESCE(SUM(u.byte_length),0), COALESCE(SUM(CASE WHEN u.project_id=$project THEN u.byte_length ELSE 0 END),0) FROM uploads u JOIN projects p ON p.id=u.project_id WHERE p.owner_id=$owner;", transaction,
            ("$owner", owner), ("$project", id)))
        {
            using var reader = quota.ExecuteReader(); reader.Read();
            if (reader.GetInt64(0) + bytes.Length > 1024L * 1024 * 1024 || reader.GetInt64(1) + bytes.Length > 256L * 1024 * 1024)
                return Results.Json(new { error = "资源存储额度已用完。" }, statusCode: 413);
        }
        var upload = new UploadRow(Database.Id(), contentHash, bytes.Length);
        using (var insert = Database.Command(connection, "INSERT INTO uploads VALUES($id,$project,$hash,$size,$content,$now);", transaction,
            ("$id", upload.UploadId), ("$project", id), ("$hash", contentHash), ("$size", bytes.Length), ("$content", bytes), ("$now", Database.Now()))) insert.ExecuteNonQuery();
        transaction.Commit();
        return Results.Created($"/v1/projects/{id}/uploads/{upload.UploadId}", upload);
    }
}
