using System.Security.Claims;
using Microsoft.Data.Sqlite;

namespace TomCat.Api;

public static class Community
{
    public sealed record TopicInput(string? Title, string? Content, string? Category);
    public sealed record ReplyInput(string? Content);
    static readonly string[] Categories = ["综合讨论", "作品分享", "开发日志", "创作求助", "经验分享"];
    internal const string TopicSelect = """
        SELECT t.id,t.title,t.content,t.category,t.owner_id AS ownerId,t.pinned,t.created_at AS time,
        (SELECT COUNT(*) FROM community_replies r WHERE r.topic_id=t.id) AS replies
        FROM community_topics t
        """;
    internal static List<object> Topics(SqliteConnection connection, string suffix, params (string Key, object? Value)[] parameters)
    {
        using var command = Database.Command(connection, TopicSelect + suffix, null, parameters);
        using var reader = command.ExecuteReader();
        var result = new List<object>();
        while (reader.Read()) result.Add(new
        {
            id = reader.GetString(0), title = reader.GetString(1), content = reader.GetString(2), category = reader.GetString(3),
            author = Author(reader.GetString(4)), pinned = reader.GetBoolean(5), time = reader.GetString(6),
            replies = reader.GetInt64(7), avatar = "T", color = "#e5ece4"
        });
        return result;
    }
    internal static string Author(string id) => $"创作者 {id[..Math.Min(id.Length, 8)]}";
    internal static int Offset(int? offset) => Math.Max(0, offset ?? 0);
    public static void Map(WebApplication app)
    {
        var routes = app.MapGroup("/v1/community");
        routes.MapGet("/topics", (int? offset, string? category, string? q, Database db) =>
        {
            using var connection = db.Open();
            var filter = " WHERE ($category='' OR t.category=$category) AND ($q='' OR instr(lower(t.title || ' ' || t.content),lower($q))>0)";
            (string Key, object? Value)[] parameters = [("$category", category ?? ""), ("$q", (q ?? "")[..Math.Min(q?.Length ?? 0, 100)]), ("$offset", Offset(offset))];
            using var count = Database.Command(connection, "SELECT COUNT(*) FROM community_topics t" + filter, null, parameters);
            return Results.Ok(new { total = Convert.ToInt64(count.ExecuteScalar()), items = Topics(connection, filter + " ORDER BY t.pinned DESC,t.created_at DESC,t.id DESC LIMIT 50 OFFSET $offset", parameters) });
        });
        routes.MapGet("/topics/{id}", (string id, Database db) =>
        {
            using var connection = db.Open();
            var topics = Topics(connection, " WHERE t.id=$id", ("$id", id));
            return topics.Count == 0 ? Results.NotFound(new { error = "话题不存在或已被移除。" }) : Results.Ok(topics[0]);
        });
        routes.MapPost("/topics", (TopicInput input, Database db, HttpContext context) =>
        {
            if (input.Title?.Trim().Length is not (> 0 and <= 80) || input.Content?.Trim().Length is not (> 0 and <= 5000) || !Categories.Contains(input.Category))
                return Results.BadRequest(new { error = "标题须为 1–80 字，正文为 1–5000 字，请选择有效分类。" });
            using var connection = db.Open();
            var id = Database.Id();
            using var insert = Database.Command(connection, "INSERT INTO community_topics(id,owner_id,title,content,category,created_at) VALUES($id,$owner,$title,$content,$category,$now)", null,
                ("$id", id), ("$owner", context.User.FindFirstValue(ClaimTypes.NameIdentifier)), ("$title", input.Title.Trim()), ("$content", input.Content.Trim()), ("$category", input.Category), ("$now", Database.Now()));
            insert.ExecuteNonQuery();
            return Results.Created($"/v1/community/topics/{id}", Topics(connection, " WHERE t.id=$id", ("$id", id))[0]);
        }).RequireAuthorization().RequireRateLimiting("community-write");
        routes.MapGet("/topics/{id}/replies", (string id, int? offset, Database db) =>
        {
            using var connection = db.Open();
            using var exists = Database.Command(connection, "SELECT COUNT(*) FROM community_topics WHERE id=$id", null, ("$id", id));
            if (Convert.ToInt64(exists.ExecuteScalar()) == 0) return Results.NotFound();
            using var count = Database.Command(connection, "SELECT COUNT(*) FROM community_replies WHERE topic_id=$id", null, ("$id", id));
            var total = Convert.ToInt64(count.ExecuteScalar());
            using var command = Database.Command(connection, "SELECT id,content,owner_id,created_at FROM community_replies WHERE topic_id=$id ORDER BY created_at,id LIMIT 50 OFFSET $offset", null, ("$id", id), ("$offset", Offset(offset)));
            using var reader = command.ExecuteReader();
            var items = new List<object>();
            while (reader.Read()) items.Add(new { id = reader.GetString(0), text = reader.GetString(1), author = Author(reader.GetString(2)), time = reader.GetString(3) });
            return Results.Ok(new { total, items });
        });
        routes.MapPost("/topics/{id}/replies", (string id, ReplyInput input, Database db, HttpContext context) =>
        {
            if (input.Content?.Trim().Length is not (> 0 and <= 1000)) return Results.BadRequest(new { error = "回复须为 1–1000 字。" });
            using var connection = db.Open();
            using var transaction = connection.BeginTransaction(deferred: false);
            using var exists = Database.Command(connection, "SELECT COUNT(*) FROM community_topics WHERE id=$id", transaction, ("$id", id));
            if (Convert.ToInt64(exists.ExecuteScalar()) == 0) return Results.NotFound();
            var reply = Database.Id(); var now = Database.Now(); var owner = context.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
            using var insert = Database.Command(connection, "INSERT INTO community_replies VALUES($reply,$topic,$owner,$content,$now)", transaction,
                ("$reply", reply), ("$topic", id), ("$owner", owner), ("$content", input.Content.Trim()), ("$now", now));
            insert.ExecuteNonQuery(); transaction.Commit();
            return Results.Created($"/v1/community/topics/{id}/replies", new { id = reply, text = input.Content.Trim(), author = Author(owner), time = now });
        }).RequireAuthorization().RequireRateLimiting("community-write");
    }
}
