using System.Security.Claims;
using Microsoft.Data.Sqlite;

namespace TomCat.Api;

// Durable conversation IDs are deliberately separate from short-lived editor
// leases and their delegated credentials. Only this service selects model history.
public static class AiConversations
{
    public sealed record SessionRow(string SessionId, string ProjectId, string Title, string CreatedAt, string UpdatedAt);
    public sealed record TurnRow(long Id, string RunId, string Prompt, string State, string? Output, string? Error, string CreatedAt, string UpdatedAt);
    public sealed record ChatMessage(string Role, string Content);
    public sealed class UnavailableException : Exception;
    public sealed class BusyException : Exception;

    private static SessionRow? Find(SqliteConnection connection, string owner, string project, string session, SqliteTransaction? transaction = null)
    {
        using var query = Database.Command(connection, """
            SELECT s.id,s.project_id,s.title,s.created_at,s.updated_at FROM ai_sessions s
            JOIN projects p ON p.id=s.project_id WHERE s.id=$session AND p.id=$project AND p.owner_id=$owner;
            """, transaction, ("$session", session), ("$project", project), ("$owner", owner));
        using var reader = query.ExecuteReader();
        return reader.Read() ? ReadSession(reader) : null;
    }
    private static SessionRow ReadSession(SqliteDataReader reader) => new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4));
    private static TurnRow ReadTurn(SqliteDataReader reader) => new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5), reader.GetString(6), reader.GetString(7));

    public static IReadOnlyList<ChatMessage> Begin(Database db, string owner, string project, string session, string run, string prompt)
    {
        using var connection = db.Open();
        using var transaction = connection.BeginTransaction();
        if (Find(connection, owner, project, session, transaction) is null) throw new UnavailableException();
        // Resubmissions on the original lease are handled before this method.
        // Never execute a durable run again through a different editor lease.
        using (var busy = Database.Command(connection, "SELECT 1 FROM ai_turns WHERE session_id=$session AND (state='running' OR run_id=$run) LIMIT 1;", transaction, ("$session", session), ("$run", run)))
            if (busy.ExecuteScalar() is not null) throw new BusyException();
        var pairs = new List<(string Prompt, string Output)>();
        using (var query = Database.Command(connection, "SELECT prompt,output FROM ai_turns WHERE session_id=$session AND state='succeeded' ORDER BY id DESC LIMIT 20;", transaction, ("$session", session)))
        using (var reader = query.ExecuteReader())
        {
            var count = 0;
            while (reader.Read())
            {
                var user = reader.GetString(0);
                var answer = reader.IsDBNull(1) ? "" : reader.GetString(1);
                if (answer.Length > 16000) answer = answer[..15960] + "\n[Earlier response truncated]";
                if (count + user.Length + answer.Length > 60000) break;
                count += user.Length + answer.Length;
                pairs.Add((user, answer));
            }
        }
        var now = Database.Now();
        using (var insert = Database.Command(connection, "INSERT INTO ai_turns(session_id,run_id,prompt,state,created_at,updated_at) VALUES($session,$run,$prompt,'running',$now,$now);", transaction,
            ("$session", session), ("$run", run), ("$prompt", prompt), ("$now", now))) insert.ExecuteNonQuery();
        using (var update = Database.Command(connection, """
            UPDATE ai_sessions SET updated_at=$now,
            title=CASE WHEN (SELECT COUNT(*) FROM ai_turns WHERE session_id=$session)=1 THEN $title ELSE title END
            WHERE id=$session;
            """, transaction, ("$session", session), ("$now", now), ("$title", prompt.Trim()[..Math.Min(48, prompt.Trim().Length)]))) update.ExecuteNonQuery();
        transaction.Commit();
        pairs.Reverse();
        return pairs.SelectMany(pair => new[] { new ChatMessage("user", pair.Prompt), new ChatMessage("assistant", pair.Output) }).ToArray();
    }
    public static void Complete(Database db, string session, EditorSessions.RunSnapshot result)
    {
        using var connection = db.Open();
        using var transaction = connection.BeginTransaction();
        var now = Database.Now();
        using (var update = Database.Command(connection, "UPDATE ai_turns SET state=$state,output=$output,error=$error,updated_at=$now WHERE session_id=$session AND run_id=$run AND state='running';", transaction,
            ("$session", session), ("$run", result.RunId), ("$state", result.State), ("$output", result.Output), ("$error", result.Error), ("$now", now))) update.ExecuteNonQuery();
        using (var update = Database.Command(connection, "UPDATE ai_sessions SET updated_at=$now WHERE id=$session;", transaction, ("$session", session), ("$now", now))) update.ExecuteNonQuery();
        transaction.Commit();
    }
    public static void Recover(Database db)
    {
        using var connection = db.Open();
        using var update = Database.Command(connection, "UPDATE ai_turns SET state='failed',error='服务已重启，任务已中断。已执行的修改可能保留，请先检查场景。',updated_at=$now WHERE state='running';", null, ("$now", Database.Now()));
        update.ExecuteNonQuery();
    }

    public static void Map(WebApplication app)
    {
        var routes = app.MapGroup("/v1/projects/{projectId}/ai-sessions").RequireAuthorization();
        routes.MapGet("/", (string projectId, HttpContext context, Database db) =>
        {
            var owner = context.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
            using var connection = db.Open();
            if (Database.GetProject(connection, owner, projectId) is null) return Results.NotFound();
            using var query = Database.Command(connection, "SELECT id,project_id,title,created_at,updated_at FROM ai_sessions WHERE project_id=$project ORDER BY updated_at DESC,id DESC;", null, ("$project", projectId));
            using var reader = query.ExecuteReader();
            var rows = new List<SessionRow>();
            while (reader.Read()) rows.Add(ReadSession(reader));
            return Results.Ok(rows);
        });
        routes.MapPost("/", (string projectId, HttpContext context, Database db) =>
        {
            var owner = context.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
            using var connection = db.Open();
            using var transaction = connection.BeginTransaction();
            if (Database.GetProject(connection, owner, projectId, transaction) is null) return Results.NotFound();
            var now = Database.Now();
            var session = new SessionRow(Database.Id(), projectId, "新对话", now, now);
            using var insert = Database.Command(connection, "INSERT INTO ai_sessions VALUES($id,$project,$title,$now,$now);", transaction,
                ("$id", session.SessionId), ("$project", projectId), ("$title", session.Title), ("$now", now));
            insert.ExecuteNonQuery(); transaction.Commit();
            return Results.Ok(session);
        });
        routes.MapGet("/{sessionId}", (string projectId, string sessionId, long? before, HttpContext context, Database db) =>
        {
            var owner = context.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
            using var connection = db.Open();
            var session = Find(connection, owner, projectId, sessionId);
            if (session is null) return Results.NotFound();
            using var query = Database.Command(connection, "SELECT id,run_id,prompt,state,output,error,created_at,updated_at FROM ai_turns WHERE session_id=$session AND id<$before ORDER BY id DESC LIMIT 31;", null,
                ("$session", sessionId), ("$before", before ?? long.MaxValue));
            using var reader = query.ExecuteReader();
            var turns = new List<TurnRow>();
            while (reader.Read()) turns.Add(ReadTurn(reader));
            var hasMore = turns.Count > 30;
            if (hasMore) turns.RemoveAt(turns.Count - 1);
            turns.Reverse();
            return Results.Ok(new { session, turns, hasMore, nextBefore = turns.FirstOrDefault()?.Id });
        });
    }
}
