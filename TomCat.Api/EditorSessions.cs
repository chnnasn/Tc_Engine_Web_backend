using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;

namespace TomCat.Api;

// Process-local by design: a restart invalidates every editor lease and pending command.
public sealed class EditorSessions
{
    public sealed class Command(string id, string name, JsonElement arguments)
    {
        public string Id { get; } = id;
        public string Name { get; } = name;
        public JsonElement Arguments { get; } = arguments.Clone();
        public bool Delivered { get; set; }
        public TaskCompletionSource<JsonElement> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public sealed class Session(string owner, string project, string commit)
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public string Token { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        public string Owner { get; } = owner;
        public string Project { get; } = project;
        public string EngineCommit { get; } = commit;
        public DateTimeOffset Expires { get; } = DateTimeOffset.UtcNow.AddHours(2);
        public DateTimeOffset LastPoll { get; set; } = DateTimeOffset.UtcNow;
        public Channel<Command> Queue { get; } = Channel.CreateUnbounded<Command>();
        public Dictionary<string, Command> Commands { get; } = [];
        public SemaphoreSlim RunLock { get; } = new(1);
        public SemaphoreSlim PollLock { get; } = new(1);
        public CancellationTokenSource Closed { get; } = new();
    }

    private readonly ConcurrentDictionary<string, Session> sessions = new();
    public static readonly HashSet<string> Tools = ["editor_get_status", "scene_get_tree", "entity_get", "component_get_schema",
        "entity_create", "entity_delete", "entity_reparent", "component_add", "component_remove", "component_set",
        "editor_play", "editor_pause", "editor_stop", "history_undo", "history_redo", "project_get_sync_status"];

    private Session? Find(string id, Database db)
    {
        if (!sessions.TryGetValue(id, out var session)) return null;
        using var connection = db.Open();
        if (session.Expires < DateTimeOffset.UtcNow || Database.GetProject(connection, session.Owner, session.Project) is null)
        { Close(id); return null; }
        return session;
    }

    private void Close(string id)
    {
        if (!sessions.TryRemove(id, out var session)) return;
        session.Closed.Cancel();
        session.Queue.Writer.TryComplete();
    }

    private Session? Browser(string id, HttpContext context, Database db)
    {
        var session = Find(id, db);
        return session?.Owner == context.User.FindFirstValue(ClaimTypes.NameIdentifier) ? session : null;
    }

    private Session? Delegated(HttpContext context, Database db)
    {
        // This credential is minted by the API and never returned to browsers or included in model messages.
        if (context.Request.Headers.ContainsKey("Origin")) return null;
        var auth = context.Request.Headers.Authorization.ToString();
        if (!auth.StartsWith("Bearer ", StringComparison.Ordinal)) return null;
        var token = auth[7..];
        var session = sessions.Values.FirstOrDefault(s => s.Token == token);
        return session is null ? null : Find(session.Id, db);
    }

    public sealed record Registration(string ProjectId, string EngineCommit);
    public sealed record ToolCall(string RequestId, string Name, JsonElement Arguments, bool IsRetry = false);
    public sealed record AgentInput(string Prompt);

    public static void Map(WebApplication app)
    {
        var routes = app.MapGroup("/v1/editor-sessions").RequireAuthorization();
        routes.MapPost("/", (Registration input, EditorSessions broker, HttpContext context, Database db) =>
        {
            var owner = context.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
            using var connection = db.Open();
            if (Database.GetProject(connection, owner, input.ProjectId) is null) return Results.NotFound();
            if (input.EngineCommit is null || !System.Text.RegularExpressions.Regex.IsMatch(input.EngineCommit, "^[0-9a-f]{40}$")) return Results.BadRequest();
            foreach (var expired in broker.sessions.Values.Where(s => s.Expires < DateTimeOffset.UtcNow || s.LastPoll < DateTimeOffset.UtcNow.AddMinutes(-2))) broker.Close(expired.Id);
            if (broker.sessions.Count >= 256 || broker.sessions.Values.Count(s => s.Owner == owner) >= 4) return Results.StatusCode(429);
            var session = new Session(owner, input.ProjectId, input.EngineCommit);
            broker.sessions[session.Id] = session;
            return Results.Ok(new { editorSessionId = session.Id, expiresAt = session.Expires });
        });
        routes.MapDelete("/{id}", (string id, EditorSessions broker, HttpContext context, Database db) =>
        {
            if (broker.Browser(id, context, db) is null) return Results.NotFound();
            broker.Close(id);
            return Results.NoContent();
        });
        routes.MapGet("/{id}/commands", async (string id, EditorSessions broker, HttpContext context, Database db) =>
        {
            var session = broker.Browser(id, context, db);
            if (session is null) return Results.NotFound();
            if (!await session.PollLock.WaitAsync(0)) return Results.Conflict();
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, session.Closed.Token);
            cancellation.CancelAfter(TimeSpan.FromSeconds(20));
            try
            {
                session.LastPoll = DateTimeOffset.UtcNow;
                var command = await session.Queue.Reader.ReadAsync(cancellation.Token);
                lock (session.Commands) command.Delivered = true;
                return Results.Ok(new { requestId = command.Id, name = command.Name, arguments = command.Arguments });
            }
            catch (OperationCanceledException) { return Results.NoContent(); }
            finally { session.PollLock.Release(); }
        });
        routes.MapPost("/{id}/commands/{commandId}/result", (string id, string commandId, JsonElement result, EditorSessions broker, HttpContext context, Database db) =>
        {
            var session = broker.Browser(id, context, db);
            if (session is null) return Results.NotFound();
            if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("ok", out var ok) || ok.ValueKind is not (JsonValueKind.True or JsonValueKind.False) || result.GetRawText().Length > 2 * 1024 * 1024) return Results.BadRequest();
            lock (session.Commands)
            {
                if (!session.Commands.TryGetValue(commandId, out var command) || !command.Delivered) return Results.NotFound();
                if (command.Result.Task.IsCompleted) return Results.Conflict();
                command.Result.TrySetResult(result.Clone());
            }
            return Results.NoContent();
        });
        routes.MapPost("/{id}/agent", async (string id, AgentInput input, EditorSessions broker, HttpContext context, Database db, IConfiguration config, IHttpClientFactory clients) =>
        {
            var session = broker.Browser(id, context, db);
            if (session is null) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(input.Prompt) || input.Prompt.Length > 8000) return Results.BadRequest();
            var endpoint = config["Agent:Url"];
            var secret = config["Agent:Secret"];
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https") || string.IsNullOrEmpty(secret) || secret.Length < 32)
                return Results.Json(new { error = "尚未配置 LangChain Agent 服务。" }, statusCode: 503);
            if (!await session.RunLock.WaitAsync(0)) return Results.Conflict();
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, session.Closed.Token);
            cancellation.CancelAfter(TimeSpan.FromSeconds(190));
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(url, "/agent/run"));
                request.Headers.Authorization = new("Bearer", secret);
                request.Content = JsonContent.Create(new { prompt = input.Prompt, sessionToken = session.Token });
                using var client = clients.CreateClient("agent");
                using var response = await client.SendAsync(request, cancellation.Token);
                var content = await response.Content.ReadAsStringAsync(cancellation.Token);
                return Results.Content(content, "application/json", statusCode: (int)response.StatusCode);
            }
            catch (OperationCanceledException)
            {
                broker.Close(id); // Revoke future tool calls after cancellation; an in-flight engine write may already have completed.
                return Results.Json(new { error = "任务已停止或超时，请检查场景后重新连接。" }, statusCode: 504);
            }
            catch (HttpRequestException)
            {
                broker.Close(id);
                return Results.Json(new { error = "Agent 连接中断，请检查场景后重新连接。" }, statusCode: 502);
            }
            finally { session.RunLock.Release(); }
        });

        app.MapGet("/internal/editor-session", (EditorSessions broker, HttpContext context, Database db) =>
        {
            var session = broker.Delegated(context, db);
            return session is null ? Results.Unauthorized() : Results.Ok(new { editorSessionId = session.Id, projectId = session.Project, engineCommit = session.EngineCommit });
        });
        app.MapPost("/internal/editor-session/call", async (ToolCall input, EditorSessions broker, HttpContext context, Database db) =>
        {
            var session = broker.Delegated(context, db);
            if (session is null) return Results.Unauthorized();
            if (!Tools.Contains(input.Name ?? "") || input.RequestId is null || !System.Text.RegularExpressions.Regex.IsMatch(input.RequestId, "^[a-zA-Z0-9_-]{1,64}$") || input.Arguments.ValueKind != JsonValueKind.Object || input.Arguments.GetRawText().Length > 65536) return Results.BadRequest();
            Command command;
            lock (session.Commands)
            {
                if (session.Commands.TryGetValue(input.RequestId, out var old))
                {
                    if (old.Name != input.Name || old.Arguments.GetRawText() != input.Arguments.GetRawText())
                        return Results.Json(new { ok = false, error = new { code = "REQUEST_ID_REUSED", message = "Retry must preserve the original arguments." } });
                    command = old;
                }
                else
                {
                    if (input.IsRetry) return Results.Json(new { ok = false, error = new { code = "REQUEST_EXPIRED", message = "Unknown retry ID. Inspect the scene; do not blindly replay." } });
                    // No eviction: an old request ID cannot silently become a new write.
                    if (session.Commands.Count >= 512) return Results.Json(new { ok = false, error = new { code = "SESSION_LIMIT", message = "Reconnect and inspect the scene before continuing." } });
                    if (session.LastPoll < DateTimeOffset.UtcNow.AddSeconds(-45)) return Results.Json(new { ok = false, error = new { code = "EDITOR_OFFLINE", message = "The editor is not connected." } });
                    command = new(input.RequestId, input.Name!, input.Arguments);
                    session.Commands.Add(command.Id, command);
                    session.Queue.Writer.TryWrite(command);
                }
            }
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, session.Closed.Token);
            cancellation.CancelAfter(TimeSpan.FromSeconds(30));
            try { return Results.Json(await command.Result.Task.WaitAsync(cancellation.Token)); }
            catch (OperationCanceledException)
            {
                return Results.Json(new { ok = false, request_id = command.Id, error = new { code = "OUTCOME_UNKNOWN", message = "Inspect the scene or retry this same request ID and arguments. Do not repeat with a fresh ID." } });
            }
        });
    }
}
