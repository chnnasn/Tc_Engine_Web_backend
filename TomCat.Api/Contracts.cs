namespace TomCat.Api;

public sealed record Credentials(string? Username, string? Password);
public sealed record ProjectInput(string? Name, string? Description, string? Template);
public sealed record UserRow(string Id, string Username, string PasswordHash);
public sealed record ProjectRow(string Id, string Name, string Description, string Template,
    string? CurrentRevisionId, string CreatedAt, string UpdatedAt)
{
    public string? Etag => CurrentRevisionId is null ? null : $"\"{CurrentRevisionId}\"";
}
public sealed record SavedRevision(string ProjectId, string RevisionId, string Etag, string CreatedAt);
