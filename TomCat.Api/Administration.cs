using System.Security.Claims;
using Microsoft.Data.Sqlite;

namespace TomCat.Api;

public static class Administration
{
    public sealed record ModerationInput(string? Reason, bool? Suspended, bool? Blocked, bool? Pinned);
    public static bool IsBootstrapEmail(IConfiguration config, string email) =>
        !string.IsNullOrWhiteSpace(config["Admin:Email"]) && string.Equals(config["Admin:Email"]!.Trim(), email, StringComparison.OrdinalIgnoreCase);
    public static void Bootstrap(Database db, IConfiguration config)
    {
        using var connection = db.Open();
        using var command = Database.Command(connection, "UPDATE users SET is_admin=1 WHERE email=$email COLLATE NOCASE AND email_verified_at IS NOT NULL AND is_admin=0", null,
            ("$email", config["Admin:Email"]?.Trim() ?? ""));
        command.ExecuteNonQuery();
    }
    static bool Valid(ModerationInput input) => input.Reason?.Trim().Length is > 0 and <= 500;
    static IResult Invalid() => Results.BadRequest(new { error = "请填写 1–500 字的操作原因。" });
    static void Audit(SqliteConnection connection, SqliteTransaction transaction, HttpContext context, string action, string target, string reason)
    {
        using var command = Database.Command(connection, "INSERT INTO admin_audit VALUES($id,$actor,$action,$target,$reason,$now)", transaction,
            ("$id", Database.Id()), ("$actor", context.User.FindFirstValue(ClaimTypes.NameIdentifier)), ("$action", action), ("$target", target), ("$reason", reason.Trim()), ("$now", Database.Now()));
        command.ExecuteNonQuery();
    }
    static List<Dictionary<string, object?>> Rows(SqliteConnection connection, string sql, params (string Key, object? Value)[] parameters)
    {
        using var command = Database.Command(connection, sql, null, parameters);
        using var reader = command.ExecuteReader();
        var rows = new List<Dictionary<string, object?>>();
        while (reader.Read())
        {
            var row = new Dictionary<string, object?>();
            for (var i = 0; i < reader.FieldCount; i++) row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }
    static IResult Page(Database db, string select, string from, string order, int? offset, string? q)
    {
        using var connection = db.Open();
        (string Key, object? Value)[] parameters = [("$offset", Community.Offset(offset)), ("$q", (q ?? "")[..Math.Min(q?.Length ?? 0, 100)])];
        using var count = Database.Command(connection, "SELECT COUNT(*) " + from, null, parameters);
        return Results.Ok(new { total = Convert.ToInt64(count.ExecuteScalar()), items = Rows(connection, select + " " + from + " " + order + " LIMIT 50 OFFSET $offset", parameters) });
    }
    public static void Map(WebApplication app)
    {
        var routes = app.MapGroup("/v1/admin").RequireAuthorization("admin");
        routes.MapGet("/overview", (Database db) =>
        {
            using var connection = db.Open();
            var today = Activity.Day(Activity.Now);
            var week = Activity.Day(Activity.Now - 6 * 86400);
            return Results.Ok(Rows(connection, """
                SELECT (SELECT COUNT(*) FROM users) AS users,
                (SELECT COUNT(*) FROM projects) AS projects,
                (SELECT COUNT(*) FROM publications p WHERE p.status='published' AND NOT EXISTS(SELECT 1 FROM publication_moderation m WHERE m.project_id=p.project_id)) AS publishedGames,
                (SELECT COUNT(*) FROM users WHERE is_suspended=1) AS suspendedUsers,
                (SELECT COUNT(*) FROM users WHERE date(created_at,'+8 hours')=$today) AS newUsersToday,
                (SELECT COUNT(*) FROM activity_daily WHERE day=$today) AS dailyActiveUsers,
                (SELECT COUNT(DISTINCT owner_id) FROM activity_daily WHERE day>=$week) AS weeklyActiveUsers,
                (SELECT COUNT(DISTINCT a.owner_id) FROM activity_presence a JOIN users u ON u.id=a.owner_id WHERE a.last_heartbeat>$cutoff AND u.is_suspended=0) AS onlineUsers,
                (SELECT COALESCE(SUM(online_seconds),0) FROM users) AS totalOnlineSeconds,
                (SELECT COALESCE(SUM(online_seconds),0) FROM activity_daily WHERE day=$today) AS todayOnlineSeconds
                """, ("$today", today), ("$week", week), ("$cutoff", Activity.Now - 60))[0]);
        });
        routes.MapGet("/activity", (Database db) =>
        {
            using var connection = db.Open();
            return Results.Ok(Rows(connection, """
                WITH RECURSIVE days(day) AS (SELECT date($today,'-13 days') UNION ALL SELECT date(day,'+1 day') FROM days WHERE day<$today)
                SELECT day,(SELECT COUNT(*) FROM users WHERE date(created_at,'+8 hours')=days.day) AS registrations,
                (SELECT COUNT(*) FROM activity_daily a WHERE a.day=days.day) AS activeUsers,
                (SELECT COALESCE(SUM(online_seconds),0) FROM activity_daily a WHERE a.day=days.day) AS onlineSeconds FROM days
                """, ("$today", Activity.Day(Activity.Now))));
        });
        routes.MapGet("/users", (int? offset, string? q, string? state, Database db) => Page(db,
            "SELECT u.id,u.email,u.display_name AS displayName,u.avatar_version AS avatarVersion,u.created_at AS createdAt,u.is_admin AS isAdmin,u.is_suspended AS suspended,u.online_seconds AS onlineSeconds,u.last_seen_at AS lastSeenAt,CASE WHEN EXISTS(SELECT 1 FROM activity_presence a WHERE a.owner_id=u.id AND a.last_heartbeat>unixepoch()-60) AND u.is_suspended=0 THEN 1 ELSE 0 END AS online",
            "FROM users u WHERE ($q='' OR instr(lower(COALESCE(u.email,'') || ' ' || u.display_name || ' ' || u.id),lower($q))>0)" + (state == "suspended" ? " AND u.is_suspended=1" : state == "active" ? " AND u.is_suspended=0" : ""), "ORDER BY u.created_at DESC,u.id DESC", offset, q));
        routes.MapGet("/topics", (int? offset, string? q, Database db) => Page(db,
            "SELECT t.id,t.title,t.content,t.category,t.pinned,t.created_at AS createdAt,u.email AS author,u.id AS ownerId,u.display_name AS authorName,u.is_admin AS authorIsAdmin,(SELECT COUNT(*) FROM community_replies r WHERE r.topic_id=t.id) AS replies",
            "FROM community_topics t JOIN users u ON u.id=t.owner_id WHERE ($q='' OR instr(lower(t.title || ' ' || t.content),lower($q))>0)", "ORDER BY t.pinned DESC,t.created_at DESC,t.id DESC", offset, q));
        routes.MapGet("/replies", (int? offset, string? q, Database db) => Page(db,
            "SELECT r.id,r.topic_id AS topicId,t.title AS topicTitle,r.content,u.email AS author,r.created_at AS createdAt",
            "FROM community_replies r JOIN users u ON u.id=r.owner_id JOIN community_topics t ON t.id=r.topic_id WHERE ($q='' OR instr(lower(r.content),lower($q))>0)", "ORDER BY r.created_at DESC,r.id DESC", offset, q));
        routes.MapGet("/publications", (int? offset, string? q, Database db) => Page(db,
            "SELECT p.id,COALESCE(g.title,p.name) AS title,g.description,g.status,u.email AS author,u.id AS ownerId,u.display_name AS authorName,u.is_admin AS authorIsAdmin,CASE WHEN m.project_id IS NULL THEN 0 ELSE 1 END AS blocked,m.reason",
            "FROM projects p JOIN users u ON u.id=p.owner_id LEFT JOIN publications g ON g.project_id=p.id LEFT JOIN publication_moderation m ON m.project_id=p.id WHERE (g.project_id IS NOT NULL OR m.project_id IS NOT NULL) AND ($q='' OR instr(lower(COALESCE(g.title,p.name)),lower($q))>0)", "ORDER BY p.updated_at DESC,p.id DESC", offset, q));
        routes.MapGet("/audit", (int? offset, string? q, Database db) => Page(db,
            "SELECT a.id,u.email AS actor,u.id AS ownerId,u.display_name AS authorName,u.is_admin AS authorIsAdmin,a.action,a.target_id AS targetId,a.reason,a.created_at AS createdAt",
            "FROM admin_audit a JOIN users u ON u.id=a.actor_id WHERE ($q='' OR instr(lower(a.reason || ' ' || a.action || ' ' || a.target_id),lower($q))>0)", "ORDER BY a.created_at DESC,a.id DESC", offset, q));
        routes.MapPut("/users/{id}/suspension", (string id, ModerationInput input, Database db, HttpContext context) =>
        {
            if (!Valid(input) || input.Suspended is null) return Invalid();
            using var connection = db.Open(); using var transaction = connection.BeginTransaction(deferred: false);
            using var query = Database.Command(connection, "SELECT is_admin FROM users WHERE id=$id", transaction, ("$id", id));
            var admin = query.ExecuteScalar();
            if (admin is null) return Results.NotFound();
            if (Convert.ToInt64(admin) == 1) return Results.Conflict(new { error = "不能封禁管理员账号。" });
            Activity.Clear(connection, transaction, id);
            using var update = Database.Command(connection, "UPDATE users SET is_suspended=$suspended,session_version=session_version+1 WHERE id=$id", transaction, ("$id", id), ("$suspended", input.Suspended.Value));
            update.ExecuteNonQuery(); Audit(connection, transaction, context, input.Suspended.Value ? "suspend_user" : "restore_user", id, input.Reason!); transaction.Commit();
            return Results.NoContent();
        });
        routes.MapPut("/topics/{id}/pin", (string id, ModerationInput input, Database db, HttpContext context) =>
        {
            if (!Valid(input) || input.Pinned is null) return Invalid();
            using var connection = db.Open(); using var transaction = connection.BeginTransaction(deferred: false);
            using var update = Database.Command(connection, "UPDATE community_topics SET pinned=$pinned WHERE id=$id", transaction, ("$id", id), ("$pinned", input.Pinned.Value));
            if (update.ExecuteNonQuery() == 0) return Results.NotFound();
            Audit(connection, transaction, context, input.Pinned.Value ? "pin_topic" : "unpin_topic", id, input.Reason!); transaction.Commit();
            return Results.NoContent();
        });
        foreach (var kind in new[] { "topics", "replies" })
        {
            var table = kind == "topics" ? "community_topics" : "community_replies";
            routes.MapDelete($"/{kind}/{{id}}", (string id, [Microsoft.AspNetCore.Mvc.FromBody] ModerationInput input, Database db, HttpContext context) =>
            {
                if (!Valid(input)) return Invalid();
                using var connection = db.Open(); using var transaction = connection.BeginTransaction(deferred: false);
                using var delete = Database.Command(connection, $"DELETE FROM {table} WHERE id=$id", transaction, ("$id", id));
                if (delete.ExecuteNonQuery() == 0) return Results.NotFound();
                Audit(connection, transaction, context, "delete_" + kind, id, input.Reason!); transaction.Commit();
                return Results.NoContent();
            });
        }
        routes.MapPut("/publications/{id}/moderation", (string id, ModerationInput input, Database db, HttpContext context) =>
        {
            if (!Valid(input) || input.Blocked is null) return Invalid();
            using var connection = db.Open(); using var transaction = connection.BeginTransaction(deferred: false);
            using var exists = Database.Command(connection, "SELECT COUNT(*) FROM projects WHERE id=$id", transaction, ("$id", id));
            if (Convert.ToInt64(exists.ExecuteScalar()) == 0) return Results.NotFound();
            using var change = Database.Command(connection, input.Blocked.Value
                ? "INSERT INTO publication_moderation VALUES($id,$reason,$now) ON CONFLICT(project_id) DO UPDATE SET reason=$reason,created_at=$now"
                : "DELETE FROM publication_moderation WHERE project_id=$id", transaction, ("$id", id), ("$reason", input.Reason!.Trim()), ("$now", Database.Now()));
            change.ExecuteNonQuery(); Audit(connection, transaction, context, input.Blocked.Value ? "block_game" : "restore_game", id, input.Reason!); transaction.Commit();
            return Results.NoContent();
        });
    }
}
