using System.Net.Mail;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;

namespace TomCat.Api;

public sealed record ForgotPassword(string? Email);
public sealed record ResetPassword(string? ChallengeId, string? Code, string? NewPassword);
public sealed record ChangePassword(string? CurrentPassword, string? NewPassword);

public static class PasswordAuth
{
    static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    static bool ValidPassword(string? password) => password is { Length: >= 12 and <= 128 };
    static IResult Invalid() => Results.BadRequest(new { error = "验证码错误或已过期，请重新发送。" });
    public static void Map(WebApplication app)
    {
        app.MapPost("/v1/auth/forgot-password", async (ForgotPassword input, Database db, VerificationMail mail) =>
        {
            var email = input.Email?.Trim().ToLowerInvariant();
            if (email is null || email.Length > 254 || !MailAddress.TryCreate(email, out var address) || address.Address != email || !email.Contains('@'))
                return Results.BadRequest(new { error = "请输入有效邮箱。" });
            if (!mail.Ready) return Results.Json(new { error = "邮件服务暂不可用，请稍后重试。" }, statusCode: 503);
            // Identical response for unknown, legacy and verified accounts; only verified accounts receive mail.
            var id = Database.Id(); var code = RandomNumberGenerator.GetInt32(1000000).ToString("D6");
            bool send = false;
            using (var connection = db.Open())
            using (var transaction = connection.BeginTransaction())
            {
                string? owner = null; long version = 0;
                using (var query = Database.Command(connection, "SELECT id,session_version FROM users WHERE email=$email AND email_verified_at IS NOT NULL;", transaction, ("$email", email)))
                using (var reader = query.ExecuteReader())
                    if (reader.Read()) { owner = reader.GetString(0); version = reader.GetInt64(1); }
                if (owner is not null)
                {
                    using var recent = Database.Command(connection, "SELECT COUNT(*) FROM password_resets WHERE email=$email AND sent_at>$time;", transaction, ("$email", email), ("$time", Now - 60));
                    if (Convert.ToInt64(recent.ExecuteScalar()) == 0)
                    {
                        using var cleanup = Database.Command(connection, "DELETE FROM password_resets WHERE expires_at<$now OR owner_id=$owner;", transaction, ("$now", Now), ("$owner", owner)); cleanup.ExecuteNonQuery();
                        using var insert = Database.Command(connection, "INSERT INTO password_resets(id,owner_id,email,code_hash,expires_at,sent_at,session_version) VALUES($id,$owner,$email,$hash,$expires,$now,$version);", transaction,
                            ("$id", id), ("$owner", owner), ("$email", email), ("$hash", Hash(id + code)), ("$expires", Now + 600), ("$now", Now), ("$version", version)); insert.ExecuteNonQuery(); send = true;
                    }
                }
                transaction.Commit();
            }
            if (send)
            {
                try { await mail.Send(email, code, recovery: true); }
                catch (Exception exception) when (exception is SmtpException or TimeoutException or InvalidOperationException or ArgumentException or FormatException or HttpRequestException or OperationCanceledException)
                {
                    using var connection = db.Open(); using var delete = Database.Command(connection, "DELETE FROM password_resets WHERE id=$id;", null, ("$id", id)); delete.ExecuteNonQuery();
                    // Do not reveal whether a recipient is registered when provider delivery fails.
                }
            }
            return Results.Ok(new { challengeId = id, expiresIn = 600, resendAfter = 60, message = "如果该邮箱已绑定账号，将收到密码重置验证码；重发请等待 60 秒。" });
        }).RequireRateLimiting("auth");

        app.MapPost("/v1/auth/reset-password", async (ResetPassword input, Database db, IPasswordHasher<UserRow> hasher, HttpContext context) =>
        {
            if (!ValidPassword(input.NewPassword)) return Results.BadRequest(new { error = "新密码须为 12–128 个字符。" });
            if (input.ChallengeId is null || !Regex.IsMatch(input.ChallengeId, "^[a-f0-9]{32}$") || input.Code is null || !Regex.IsMatch(input.Code, "^[0-9]{6}$")) return Invalid();
            using var connection = db.Open(); using var transaction = connection.BeginTransaction();
            string owner, username, expected;
            using (var query = Database.Command(connection, "SELECT p.owner_id,u.username,p.code_hash FROM password_resets p JOIN users u ON u.id=p.owner_id WHERE p.id=$id AND p.expires_at>$now AND p.attempts<5 AND p.session_version=u.session_version AND p.email=u.email AND u.email_verified_at IS NOT NULL;", transaction, ("$id", input.ChallengeId), ("$now", Now)))
            using (var reader = query.ExecuteReader())
            {
                if (!reader.Read()) return Invalid();
                owner = reader.GetString(0); username = reader.GetString(1); expected = reader.GetString(2);
            }
            if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(expected), Convert.FromHexString(Hash(input.ChallengeId + input.Code))))
            {
                using var fail = Database.Command(connection, "UPDATE password_resets SET attempts=attempts+1 WHERE id=$id;", transaction, ("$id", input.ChallengeId)); fail.ExecuteNonQuery(); transaction.Commit(); return Invalid();
            }
            var user = new UserRow(owner, username, "");
            UpdatePassword(connection, transaction, owner, hasher.HashPassword(user, input.NewPassword!));
            transaction.Commit(); await context.SignOutAsync();
            return Results.Ok(new { message = "密码已重置，请使用新密码登录。" });
        }).RequireRateLimiting("auth");

        app.MapPost("/v1/auth/change-password", async (ChangePassword input, Database db, IPasswordHasher<UserRow> hasher, HttpContext context) =>
        {
            if (!ValidPassword(input.NewPassword) || input.CurrentPassword is null || input.CurrentPassword.Length > 128)
                return Results.BadRequest(new { error = "请输入当前密码，新密码须为 12–128 个字符。" });
            if (input.CurrentPassword == input.NewPassword) return Results.BadRequest(new { error = "新密码不能与当前密码相同。" });
            var owner = context.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
            using var connection = db.Open(); using var transaction = connection.BeginTransaction();
            UserRow user; long version;
            using (var query = Database.Command(connection, "SELECT username,password_hash,session_version FROM users WHERE id=$id;", transaction, ("$id", owner)))
            using (var reader = query.ExecuteReader())
            {
                if (!reader.Read()) return Results.Unauthorized();
                user = new UserRow(owner, reader.GetString(0), reader.GetString(1)); version = reader.GetInt64(2);
            }
            if ((context.User.FindFirstValue("session_version") ?? "0") != version.ToString()) return Results.Unauthorized();
            if (hasher.VerifyHashedPassword(user, user.PasswordHash, input.CurrentPassword) == PasswordVerificationResult.Failed)
                return Results.BadRequest(new { error = "当前密码错误。" });
            UpdatePassword(connection, transaction, owner, hasher.HashPassword(user, input.NewPassword!));
            transaction.Commit(); await context.SignOutAsync();
            return Results.Ok(new { message = "密码已修改，所有设备需重新登录。" });
        }).RequireAuthorization().RequireRateLimiting("auth");
    }
    static void UpdatePassword(Microsoft.Data.Sqlite.SqliteConnection connection, Microsoft.Data.Sqlite.SqliteTransaction transaction, string owner, string hash)
    {
        using var update = Database.Command(connection, "UPDATE users SET password_hash=$hash,session_version=session_version+1 WHERE id=$id;", transaction, ("$hash", hash), ("$id", owner)); update.ExecuteNonQuery();
        using var reset = Database.Command(connection, "DELETE FROM password_resets WHERE owner_id=$id;", transaction, ("$id", owner)); reset.ExecuteNonQuery();
        using var email = Database.Command(connection, "DELETE FROM email_challenges WHERE owner_id=$id;", transaction, ("$id", owner)); email.ExecuteNonQuery();
    }
}
