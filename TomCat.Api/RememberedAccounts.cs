using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Authentication;
using System.Security.Claims;

namespace TomCat.Api;

// A protected HttpOnly cookie holds opt-in, independently expiring account grants.
// The browser receives only display data; passwords and grant contents are never exposed to JS.
public sealed class RememberedAccounts(IDataProtectionProvider protection, IHostEnvironment environment, Database db)
{
    const string Cookie = "tomcat.accounts";
    const string UserSelect = "SELECT id,username,password_hash,email,email_verified_at,session_version,is_admin,display_name,avatar_version FROM users u WHERE id=$id AND session_version=$version AND email_verified_at IS NOT NULL AND is_suspended=0 AND EXISTS(SELECT 1 FROM remembered_logins r WHERE r.id=$grant AND r.owner_id=u.id AND r.session_version=$version AND r.expires_at=$expires AND r.expires_at>unixepoch())";
    readonly IDataProtector protector = protection.CreateProtector("TomCat.RememberedAccounts.v1");
    readonly Database database = db;
    public sealed record Grant(string Id, long Version, long Expires, string Key);
    public sealed record Selection(string? Id);
    List<Grant> Read(HttpContext context)
    {
        var value = context.Request.Cookies[Cookie];
        if (value is null || value.Length > 8000) return [];
        try
        {
            return (JsonSerializer.Deserialize<List<Grant>>(protector.Unprotect(value)) ?? [])
                .Where(grant => grant.Expires > Activity.Now && grant.Id.Length <= 100 && grant.Key.Length == 32).DistinctBy(grant => grant.Id).TakeLast(8).ToList();
        }
        catch (Exception error) when (error is CryptographicException or JsonException or FormatException or NullReferenceException) { return []; }
    }
    void Write(HttpContext context, List<Grant> grants)
    {
        var options = new CookieOptions { HttpOnly = true, Secure = !environment.IsDevelopment(), SameSite = SameSiteMode.Strict, Path = "/v1/auth", IsEssential = true };
        if (grants.Count == 0) context.Response.Cookies.Delete(Cookie, options);
        else
        {
            options.Expires = DateTimeOffset.FromUnixTimeSeconds(grants.Max(grant => grant.Expires));
            context.Response.Cookies.Append(Cookie, protector.Protect(JsonSerializer.Serialize(grants)), options);
        }
    }
    public void Remember(HttpContext context, string id, long version, bool remember)
    {
        var old = Read(context);
        var grants = old.Where(grant => grant.Id != id).ToList();
        using var connection = database.Open(); using var transaction = connection.BeginTransaction(deferred: false);
        foreach (var grant in old.Where(grant => grant.Id == id)) Revoke(connection, transaction, grant);
        if (remember)
        {
            var grant = new Grant(id, version, Activity.Now + 30 * 86400, Database.Id());
            using var insert = Database.Command(connection, "INSERT INTO remembered_logins VALUES($key,$owner,$version,$expires)", transaction,
                ("$key", grant.Key), ("$owner", id), ("$version", version), ("$expires", grant.Expires));
            insert.ExecuteNonQuery(); grants.Add(grant);
        }
        foreach (var grant in grants.Take(Math.Max(0, grants.Count - 8))) Revoke(connection, transaction, grant);
        using var cleanup = Database.Command(connection, "DELETE FROM remembered_logins WHERE expires_at<=unixepoch()", transaction); cleanup.ExecuteNonQuery();
        transaction.Commit(); Write(context, grants.TakeLast(8).ToList());
    }
    static void Revoke(Microsoft.Data.Sqlite.SqliteConnection connection, Microsoft.Data.Sqlite.SqliteTransaction transaction, Grant grant)
    {
        using var delete = Database.Command(connection, "DELETE FROM remembered_logins WHERE id=$key AND owner_id=$owner", transaction, ("$key", grant.Key), ("$owner", grant.Id));
        delete.ExecuteNonQuery();
    }
    public void Remove(HttpContext context, string id)
    {
        var grants = Read(context);
        using var connection = database.Open(); using var transaction = connection.BeginTransaction(deferred: false);
        foreach (var grant in grants.Where(grant => grant.Id == id)) Revoke(connection, transaction, grant);
        transaction.Commit(); Write(context, grants.Where(grant => grant.Id != id).ToList());
    }
    public static void Map(WebApplication app)
    {
        app.MapGet("/v1/auth/remembered", (RememberedAccounts saved, HttpContext context) =>
        {
            using var connection = saved.database.Open();
            var items = new List<object>();
            var valid = new List<Grant>();
            foreach (var grant in saved.Read(context))
            {
                using var query = Database.Command(connection, UserSelect, null, ("$id", grant.Id), ("$version", grant.Version), ("$grant", grant.Key), ("$expires", grant.Expires));
                using var reader = query.ExecuteReader();
                if (!reader.Read()) continue;
                items.Add(new { id = grant.Id, email = reader.GetString(3), displayName = UserProfiles.Name(grant.Id, reader.GetString(7)), avatarUrl = UserProfiles.AvatarUrl(grant.Id, reader.GetString(8)), isAdmin = reader.GetBoolean(6), expiresAt = DateTimeOffset.FromUnixTimeSeconds(grant.Expires) });
                valid.Add(grant);
            }
            saved.Write(context, valid);
            return Results.Ok(items);
        }).RequireRateLimiting("auth");
        app.MapPost("/v1/auth/switch", async (Selection input, RememberedAccounts saved, HttpContext context) =>
        {
            var grant = saved.Read(context).FirstOrDefault(grant => grant.Id == input.Id);
            if (grant is null) return Results.Unauthorized();
            using var connection = saved.database.Open();
            using var query = Database.Command(connection, UserSelect, null, ("$id", grant.Id), ("$version", grant.Version), ("$grant", grant.Key), ("$expires", grant.Expires));
            using var reader = query.ExecuteReader();
            if (!reader.Read()) { saved.Remove(context, grant.Id); return Results.Unauthorized(); }
            await EmailAuth.SignIn(context, grant.Id, reader.GetString(3), grant.Version, true, DateTimeOffset.FromUnixTimeSeconds(grant.Expires));
            return Results.Ok(EmailAuth.PublicUser(reader));
        }).RequireRateLimiting("auth");
        app.MapDelete("/v1/auth/remembered/{id}", async (string id, RememberedAccounts saved, HttpContext context) =>
        {
            saved.Remove(context, id);
            if (context.User.FindFirstValue(ClaimTypes.NameIdentifier) == id) await context.SignOutAsync();
            return Results.NoContent();
        }).RequireRateLimiting("auth");
    }
}
