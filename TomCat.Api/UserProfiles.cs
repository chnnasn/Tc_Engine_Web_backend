using System.Buffers.Binary;
using System.Security.Claims;

namespace TomCat.Api;

public static class UserProfiles
{
    public sealed record ProfileInput(string? DisplayName);
    public static string Name(string id, string name) => string.IsNullOrWhiteSpace(name) ? $"创作者 {id[..Math.Min(id.Length, 8)]}" : name;
    public static string? AvatarUrl(string id, string version) => version.Length == 0 ? null : $"/v1/users/{Uri.EscapeDataString(id)}/avatar?v={version}";
    static string Owner(HttpContext context) => context.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
    const int AvatarLimit = 512 * 1024;
    static bool Png(byte[] bytes) => bytes.Length >= 45 &&
        bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) &&
        BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(8, 4)) == 13 &&
        bytes.AsSpan(12, 4).SequenceEqual("IHDR"u8) &&
        BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16, 4)) is > 0 and <= 512 &&
        BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20, 4)) is > 0 and <= 512 &&
        bytes.AsSpan(bytes.Length - 12, 12).SequenceEqual(new byte[] { 0, 0, 0, 0, 73, 69, 78, 68, 174, 66, 96, 130 });
    public static void Map(WebApplication app)
    {
        app.MapGet("/v1/users/{id}", (string id, Database db) =>
        {
            using var connection = db.Open();
            using var query = Database.Command(connection, """
                SELECT id,display_name,avatar_version,is_admin,created_at,
                (SELECT COUNT(*) FROM community_topics WHERE owner_id=u.id),
                (SELECT COUNT(*) FROM publications g JOIN projects p ON p.id=g.project_id WHERE p.owner_id=u.id AND g.status='published' AND NOT EXISTS(SELECT 1 FROM publication_moderation m WHERE m.project_id=g.project_id))
                FROM users u WHERE id=$id AND email_verified_at IS NOT NULL
                """, null, ("$id", id));
            using var reader = query.ExecuteReader();
            return !reader.Read() ? Results.NotFound(new { error = "用户不存在。" }) : Results.Ok(new
            {
                id = reader.GetString(0), displayName = Name(id, reader.GetString(1)), avatarUrl = AvatarUrl(id, reader.GetString(2)),
                isAdmin = reader.GetBoolean(3), createdAt = reader.GetString(4), topics = reader.GetInt64(5), publishedGames = reader.GetInt64(6)
            });
        });
        app.MapGet("/v1/users/{id}/topics", (string id, int? offset, Database db) =>
        {
            using var connection = db.Open();
            using var count = Database.Command(connection, "SELECT COUNT(*) FROM community_topics WHERE owner_id=$id", null, ("$id", id));
            return Results.Ok(new { total = Convert.ToInt64(count.ExecuteScalar()), items = Community.Topics(connection, " WHERE t.owner_id=$id ORDER BY t.created_at DESC,t.id DESC LIMIT 50 OFFSET $offset", ("$id", id), ("$offset", Community.Offset(offset))) });
        });
        app.MapPut("/v1/users/me/profile", (ProfileInput input, Database db, HttpContext context) =>
        {
            var name = input.DisplayName?.Trim();
            if (name?.Length is not (> 0 and <= 32) || name.Any(char.IsControl)) return Results.BadRequest(new { error = "昵称须为 1–32 个字符，不能包含换行或控制字符。" });
            using var connection = db.Open();
            using var update = Database.Command(connection, "UPDATE users SET display_name=$name WHERE id=$id", null, ("$name", name), ("$id", Owner(context)));
            update.ExecuteNonQuery();
            return Results.Ok(new { displayName = name });
        }).RequireAuthorization();
        app.MapPut("/v1/users/me/avatar", async (Database db, HttpContext context) =>
        {
            if (context.Request.ContentType?.Split(';')[0].Trim() != "image/png") return Results.BadRequest(new { error = "请上传 PNG 头像。" });
            if (context.Request.ContentLength > AvatarLimit) return Results.StatusCode(413);
            using var memory = new MemoryStream();
            var buffer = new byte[8192];
            while (true)
            {
                var size = await context.Request.Body.ReadAsync(buffer, context.RequestAborted);
                if (size == 0) break;
                if (memory.Length + size > AvatarLimit) return Results.StatusCode(413);
                memory.Write(buffer, 0, size);
            }
            var bytes = memory.ToArray();
            if (!Png(bytes)) return Results.BadRequest(new { error = "头像须为有效的 PNG 图片，尺寸不超过 512×512，文件不超过 512 KB。" });
            using var connection = db.Open();
            var id = Owner(context); var version = Database.Id();
            using var update = Database.Command(connection, "UPDATE users SET avatar=$avatar,avatar_version=$version WHERE id=$id", null, ("$avatar", bytes), ("$version", version), ("$id", id));
            update.ExecuteNonQuery();
            return Results.Ok(new { avatarUrl = AvatarUrl(id, version) });
        }).RequireAuthorization().RequireRateLimiting("community-write");
        app.MapDelete("/v1/users/me/avatar", (Database db, HttpContext context) =>
        {
            using var connection = db.Open();
            using var update = Database.Command(connection, "UPDATE users SET avatar=NULL,avatar_version='' WHERE id=$id", null, ("$id", Owner(context)));
            update.ExecuteNonQuery(); return Results.NoContent();
        }).RequireAuthorization();
        app.MapGet("/v1/users/{id}/avatar", (string id, string? v, Database db, HttpContext context) =>
        {
            using var connection = db.Open();
            using var query = Database.Command(connection, "SELECT avatar,avatar_version FROM users WHERE id=$id AND email_verified_at IS NOT NULL AND avatar IS NOT NULL", null, ("$id", id));
            using var reader = query.ExecuteReader();
            if (!reader.Read() || v is not null && v != reader.GetString(1)) return Results.NotFound();
            context.Response.Headers.XContentTypeOptions = "nosniff";
            context.Response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
            context.Response.Headers.CacheControl = "public, max-age=0, must-revalidate";
            context.Response.Headers.ETag = $"\"{reader.GetString(1)}\"";
            if (context.Request.Headers.IfNoneMatch.ToString() == context.Response.Headers.ETag.ToString()) return Results.StatusCode(304);
            return Results.File((byte[])reader[0], "image/png");
        });
    }
}
