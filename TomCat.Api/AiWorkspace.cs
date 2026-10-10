using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TomCat.Api;

public static class AiWorkspace
{
    public sealed record KnowledgeInput(string Key, string Content, int ExpectedVersion);
    public sealed record ExperimentInput(string Name, string BaseRevisionId);
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    static JsonElement Value(object value) => JsonSerializer.SerializeToElement(value, Json);
    static JsonElement Failure(string code, string message) => Value(new { ok = false, error = new { code, message } });
    // Large tool replies remain available to the model; durable audit storage is bounded and explicitly marked.
    static string Bounded(JsonElement value)
    {
        var raw = value.GetRawText();
        return raw.Length <= 32768 ? raw : JsonSerializer.Serialize(new { truncated = true, originalLength = raw.Length,
            sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant(), preview = raw[..8192] }, Json);
    }
    public static void Queued(Database db, EditorSessions.Session session, EditorSessions.Command command)
    {
        using var connection = db.Open();
        using var insert = Database.Command(connection, """
            INSERT INTO ai_tool_events(project_id,lease_id,run_id,request_id,tool,arguments,state,created_at,updated_at)
            VALUES($project,$lease,$run,$request,$tool,$arguments,'queued',$now,$now);
            """, null, ("$project", session.Project), ("$lease", session.Id), ("$run", session.Run?.Snapshot.RunId),
            ("$request", command.Id), ("$tool", command.Name), ("$arguments", Bounded(command.Arguments)), ("$now", Database.Now()));
        insert.ExecuteNonQuery();
    }
    public static void Result(Database db, EditorSessions.Session session, EditorSessions.Command command, JsonElement result)
    {
        using var connection = db.Open();
        using var update = Database.Command(connection, "UPDATE ai_tool_events SET state=$state,result=$result,updated_at=$now WHERE lease_id=$lease AND request_id=$request;", null,
            ("$state", result.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True ? "succeeded" : "failed"),
            ("$result", Bounded(result)), ("$now", Database.Now()), ("$lease", session.Id), ("$request", command.Id));
        update.ExecuteNonQuery();
    }
    public static void Interrupted(Database db, string? lease = null)
    {
        using var connection = db.Open();
        using var update = Database.Command(connection, "UPDATE ai_tool_events SET state='outcome_unknown',updated_at=$now WHERE state='queued' AND ($lease IS NULL OR lease_id=$lease);", null,
            ("$now", Database.Now()), ("$lease", lease));
        update.ExecuteNonQuery();
    }
    public static JsonElement Knowledge(Database db, EditorSessions.Session session, string name, JsonElement arguments)
    {
        if (name == "project_knowledge_list") return Value(new { ok = true, data = Notes(db, session.Project) });
        try
        {
            var key = arguments.GetProperty("key").GetString();
            var content = arguments.GetProperty("content").GetString();
            var expected = arguments.GetProperty("expected_version").GetInt32();
            return SaveNote(db, session.Project, key, content, expected, session.Run?.Snapshot.RunId, session.EngineCommit);
        }
        catch (Exception error) when (error is KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        { return Failure("INVALID_ARGUMENT", "Invalid knowledge note"); }
    }
    static object Notes(Database db, string project)
    {
        using var connection = db.Open();
        using var query = Database.Command(connection, "SELECT key,content,version,source_run_id,engine_commit,updated_at FROM project_knowledge WHERE project_id=$project ORDER BY key LIMIT 100;", null, ("$project", project));
        using var reader = query.ExecuteReader();
        var notes = new List<object>();
        while (reader.Read()) notes.Add(new { key = reader.GetString(0), content = reader.GetString(1), version = reader.GetInt32(2),
            sourceRunId = reader.IsDBNull(3) ? null : reader.GetString(3), engineCommit = reader.GetString(4), updatedAt = reader.GetString(5), verification = "unverified_observation" });
        return new { notes };
    }
    static JsonElement SaveNote(Database db, string project, string? key, string? content, int expected, string? run, string commit)
    {
        if (key is null || !Regex.IsMatch(key, "^[a-zA-Z0-9_-]{1,64}$") || string.IsNullOrWhiteSpace(content) || content.Length > 4000 || expected < 0 || expected == int.MaxValue)
            return Failure("INVALID_ARGUMENT", "Key, content or expected version is invalid");
        using var connection = db.Open();
        using var tx = connection.BeginTransaction();
        using var read = Database.Command(connection, "SELECT version FROM project_knowledge WHERE project_id=$project AND key=$key;", tx, ("$project", project), ("$key", key));
        var version = Convert.ToInt32(read.ExecuteScalar());
        if (version != expected) return Failure("KNOWLEDGE_CONFLICT", "Note changed; read it again before saving.");
        using var count = Database.Command(connection, "SELECT COUNT(*) FROM project_knowledge WHERE project_id=$project;", tx, ("$project", project));
        if (version == 0 && Convert.ToInt64(count.ExecuteScalar()) >= 100) return Failure("KNOWLEDGE_LIMIT", "Project has 100 notes; update an existing note.");
        using var write = Database.Command(connection, """
            INSERT INTO project_knowledge VALUES($project,$key,$content,$version,$run,$commit,$now)
            ON CONFLICT(project_id,key) DO UPDATE SET content=$content,version=$version,source_run_id=$run,engine_commit=$commit,updated_at=$now;
            """, tx, ("$project", project), ("$key", key), ("$content", content), ("$version", expected + 1), ("$run", run), ("$commit", commit), ("$now", Database.Now()));
        write.ExecuteNonQuery(); tx.Commit();
        return Value(new { ok = true, data = new { key, version = expected + 1, sourceRunId = run, verification = "unverified_observation" } });
    }
    public static void Map(WebApplication app)
    {
        var routes = app.MapGroup("/v1/projects/{projectId}").RequireAuthorization();
        routes.MapGet("/ai-knowledge", (string projectId, Database db, HttpContext context) =>
        {
            using var connection = db.Open();
            return Database.GetProject(connection, context.User.FindFirstValue(ClaimTypes.NameIdentifier)!, projectId) is null ? Results.NotFound() : Results.Ok(Notes(db, projectId));
        });
        routes.MapPut("/ai-knowledge", (string projectId, KnowledgeInput input, Database db, HttpContext context) =>
        {
            using var connection = db.Open();
            if (Database.GetProject(connection, context.User.FindFirstValue(ClaimTypes.NameIdentifier)!, projectId) is null) return Results.NotFound();
            var result = SaveNote(db, projectId, input.Key, input.Content, input.ExpectedVersion, null, "user");
            return Results.Json(result, statusCode: result.GetProperty("ok").GetBoolean() ? 200 : result.GetProperty("error").GetProperty("code").GetString() == "KNOWLEDGE_CONFLICT" ? 409 : 400);
        });
        routes.MapGet("/ai-runs/{runId}/events", (string projectId, string runId, long? after, Database db, HttpContext context) =>
        {
            using var connection = db.Open();
            if (Database.GetProject(connection, context.User.FindFirstValue(ClaimTypes.NameIdentifier)!, projectId) is null) return Results.NotFound();
            using var query = Database.Command(connection, "SELECT id,request_id,tool,arguments,state,result,created_at,updated_at FROM ai_tool_events WHERE project_id=$project AND run_id=$run AND id>$after ORDER BY id LIMIT 101;", null,
                ("$project", projectId), ("$run", runId), ("$after", after ?? 0));
            using var reader = query.ExecuteReader();
            var events = new List<object>();
            long lastId = 0;
            while (reader.Read()) {
                if (events.Count == 100) return Results.Ok(new { events, hasMore = true, nextAfter = lastId });
                lastId = reader.GetInt64(0);
                events.Add(new { id = lastId, requestId = reader.GetString(1), tool = reader.GetString(2), arguments = JsonSerializer.Deserialize<JsonElement>(reader.GetString(3)), state = reader.GetString(4),
                    result = reader.IsDBNull(5) ? (JsonElement?)null : JsonSerializer.Deserialize<JsonElement>(reader.GetString(5)), createdAt = reader.GetString(6), updatedAt = reader.GetString(7) });
            }
            return Results.Ok(new { events, hasMore = false, nextAfter = lastId });
        });
        routes.MapPost("/experiments", (string projectId, ExperimentInput input, Database db, HttpContext context) =>
        {
            var owner = context.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
            using var connection = db.Open();
            using var tx = connection.BeginTransaction();
            var source = Database.GetProject(connection, owner, projectId, tx);
            if (source is null) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(input.Name) || input.Name.Length > 64 || input.BaseRevisionId is null) return Results.BadRequest();
            using var revision = Database.Command(connection, "SELECT 1 FROM revisions WHERE id=$revision AND project_id=$project;", tx, ("$revision", input.BaseRevisionId), ("$project", projectId));
            if (revision.ExecuteScalar() is null) return Results.NotFound();
            var id = Database.Id(); var now = Database.Now();
            using var insert = Database.Command(connection, "INSERT INTO projects VALUES($id,$owner,$name,$description,$template,NULL,$now,$now);", tx,
                ("$id", id), ("$owner", owner), ("$name", input.Name.Trim()), ("$description", $"试验副本，基于 {source.Name} / {input.BaseRevisionId}"), ("$template", source.Template), ("$now", now));
            insert.ExecuteNonQuery();
            using var relation = Database.Command(connection, "INSERT INTO project_experiments VALUES($id,$source,$revision,$now);", tx,
                ("$id", id), ("$source", projectId), ("$revision", input.BaseRevisionId), ("$now", now));
            relation.ExecuteNonQuery(); tx.Commit();
            return Results.Ok(Database.GetProject(connection, owner, id));
        });
        routes.MapGet("/experiment", (string projectId, Database db, HttpContext context) =>
        {
            using var connection = db.Open();
            if (Database.GetProject(connection, context.User.FindFirstValue(ClaimTypes.NameIdentifier)!, projectId) is null) return Results.NotFound();
            using var query = Database.Command(connection, "SELECT source_project_id,base_revision_id FROM project_experiments WHERE project_id=$project;", null, ("$project", projectId));
            using var reader = query.ExecuteReader();
            return reader.Read() ? Results.Ok(new { sourceProjectId = reader.IsDBNull(0) ? null : reader.GetString(0), baseRevisionId = reader.GetString(1) }) : Results.NoContent();
        });
    }
}
