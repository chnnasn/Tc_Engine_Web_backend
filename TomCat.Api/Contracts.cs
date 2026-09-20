namespace TomCat.Api;

public sealed record Credentials(string? Username, string? Password);
public sealed record ProjectInput(string? Name, string? Description, string? Template);
public sealed record UserRow(string Id, string Username, string PasswordHash);
public sealed record ProjectRow(string Id, string Name, string Description, string Template,
    string? CurrentRevisionId, string CreatedAt, string UpdatedAt)
{
    public string? Etag => CurrentRevisionId is null ? null : $"\"{CurrentRevisionId}\"";
}
public sealed record SavedRevision(string ProjectId, string RevisionId, string Etag, string CreatedAt,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] AiCheckpoint? AiCheckpoint = null);

public sealed record AiCheckpoint(string RunId, string Phase, string SceneVersion)
{
    public static AiCheckpoint? Read(System.Text.Json.JsonElement payload)
    {
        if (!payload.TryGetProperty("aiCheckpoint", out var item) || item.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
        if (!item.TryGetProperty("runId", out var run) || run.ValueKind != System.Text.Json.JsonValueKind.String ||
            !System.Text.RegularExpressions.Regex.IsMatch(run.GetString()!, "^[a-f0-9]{32}$") ||
            !item.TryGetProperty("phase", out var phase) || phase.ValueKind != System.Text.Json.JsonValueKind.String || phase.GetString() is not ("start" or "end") ||
            !item.TryGetProperty("sceneVersion", out var version) || version.ValueKind != System.Text.Json.JsonValueKind.String ||
            version.GetString()!.Length > 64 || !System.Text.RegularExpressions.Regex.IsMatch(version.GetString()!, "^[0-9]+:[0-9]+$")) return null;
        return new(run.GetString()!, phase.GetString()!, version.GetString()!);
    }
}
