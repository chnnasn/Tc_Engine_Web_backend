using System.Net;
using System.Net.Mail;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;

namespace TomCat.Api;

public sealed record EmailStart(string? Email, string? Password);
public sealed record EmailVerify(string? ChallengeId, string? Code);
public sealed record EmailComplete(string? Token);

public sealed class VerificationMail(IConfiguration config, IHostEnvironment environment, IHttpClientFactory clients)
{
    bool Pickup => environment.IsDevelopment() && !string.IsNullOrWhiteSpace(config["Mail:PickupDirectory"]);
    bool Resend => string.Equals(config["Mail:Provider"], "Resend", StringComparison.OrdinalIgnoreCase);
    public bool Ready => Pickup || !string.IsNullOrWhiteSpace(config["Mail:From"]) &&
        (Resend ? !string.IsNullOrWhiteSpace(config["Mail:ApiKey"]) : !string.IsNullOrWhiteSpace(config["Mail:Host"]));
    public async Task Send(string email, string code, bool recovery = false)
    {
        var subject = recovery ? "TC Fun 密码重置验证码" : "TC Fun 邮箱验证码";
        var text = recovery ? $"你正在重置 TC Fun 账号密码。验证码是 {code}，10 分钟内有效。如非本人操作，请忽略此邮件；不要向他人提供验证码。" : $"你的验证码是 {code}，10 分钟内有效。如非本人操作，请忽略此邮件。";
        var html = MailTemplate.Render(code, recovery);
        if (Resend && !Pickup)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "emails");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", config["Mail:ApiKey"]);
            request.Content = JsonContent.Create(new {
                from = config["Mail:From"], to = new[] { email }, subject, text, html
            });
            using var response = await clients.CreateClient("verification-mail").SendAsync(request);
            if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Mail provider returned HTTP {(int)response.StatusCode}.");
            return;
        }
        using var message = new MailMessage(config["Mail:From"] ?? "test@localhost", email,
            subject, text);
        message.BodyEncoding = Encoding.UTF8;
        message.SubjectEncoding = Encoding.UTF8;
        message.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(html, Encoding.UTF8, "text/html"));
        using var smtp = new SmtpClient(config["Mail:Host"] ?? "localhost", config.GetValue("Mail:Port", 587));
        if (environment.IsDevelopment() && config["Mail:PickupDirectory"] is { Length: > 0 } pickup)
        {
            Directory.CreateDirectory(pickup);
            smtp.DeliveryMethod = SmtpDeliveryMethod.SpecifiedPickupDirectory;
            smtp.PickupDirectoryLocation = Path.GetFullPath(pickup);
        }
        else
        {
            smtp.EnableSsl = true;
            smtp.Credentials = new NetworkCredential(config["Mail:Username"], config["Mail:Password"]);
        }
        smtp.Timeout = 15000;
        await smtp.SendMailAsync(message).WaitAsync(TimeSpan.FromSeconds(20));
    }
}

public static class EmailAuth
{
    static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    static string? Normalize(string? value)
    {
        value = value?.Trim().ToLowerInvariant();
        if (value is null || value.Length > 254 || !MailAddress.TryCreate(value, out var address) || address.Address != value || !value.Contains('@')) return null;
        return value;
    }
    public static object PublicUser(SqliteDataReader reader) => new { id = reader.GetString(0), email = reader.GetString(3), emailVerified = !reader.IsDBNull(4) };
    public static Task SignIn(HttpContext context, string id, string email, long version = 0) => context.SignInAsync(new ClaimsPrincipal(new ClaimsIdentity(
        [new Claim(ClaimTypes.NameIdentifier, id), new Claim(ClaimTypes.Name, email), new Claim("session_version", version.ToString())], CookieAuthenticationDefaults.AuthenticationScheme)));

    public static void Map(WebApplication app)
    {
        app.MapPost("/v1/auth/register", (EmailStart input, Database db, VerificationMail mail, IPasswordHasher<UserRow> hasher) => Start(input, db, mail, hasher)).RequireRateLimiting("auth");
        app.MapPost("/v1/auth/verify-email", (EmailVerify input, Database db) =>
        {
            if (input.ChallengeId is null || !Regex.IsMatch(input.ChallengeId, "^[a-f0-9]{32}$") || input.Code is null || !Regex.IsMatch(input.Code, "^[0-9]{6}$")) return Results.BadRequest(new { error = "请输入六位验证码。" });
            using var connection = db.Open();
            using var transaction = connection.BeginTransaction();
            using var query = Database.Command(connection, "SELECT code_hash FROM email_challenges WHERE id=$id AND owner_id IS NULL AND expires_at>$now AND attempts<5 AND verified=0;", transaction, ("$id", input.ChallengeId), ("$now", Now));
            string expected;
            using (var reader = query.ExecuteReader())
            {
                if (!reader.Read()) return Results.BadRequest(new { error = "验证码已失效，请重新发送。" });
                expected = reader.GetString(0);
            }
            if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(expected), Convert.FromHexString(Hash(input.ChallengeId + input.Code))))
            {
                using var fail = Database.Command(connection, "UPDATE email_challenges SET attempts=attempts+1 WHERE id=$id;", transaction, ("$id", input.ChallengeId)); fail.ExecuteNonQuery(); transaction.Commit();
                return Results.BadRequest(new { error = "验证码错误，最多尝试五次。" });
            }
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            using var update = Database.Command(connection, "UPDATE email_challenges SET verified=1, setup_hash=$token, expires_at=$expires WHERE id=$id;", transaction, ("$token", Hash(token)), ("$expires", Now + 600), ("$id", input.ChallengeId)); update.ExecuteNonQuery();
            transaction.Commit();
            return Results.Ok(new { token });
        }).RequireRateLimiting("auth");
        app.MapPost("/v1/auth/complete-registration", async (EmailComplete input, Database db, HttpContext context) =>
        {
            if (input.Token is null || !Regex.IsMatch(input.Token, "^[A-Fa-f0-9]{64}$")) return Results.BadRequest(new { error = "邮箱验证已失效，请重新验证。" });
            using var connection = db.Open(); using var transaction = connection.BeginTransaction();
            string email, password;
            using (var query = Database.Command(connection, "SELECT email,password_hash FROM email_challenges WHERE setup_hash=$token AND owner_id IS NULL AND verified=1 AND expires_at>$now;", transaction, ("$token", Hash(input.Token)), ("$now", Now)))
            using (var reader = query.ExecuteReader())
            {
                if (!reader.Read()) return Results.BadRequest(new { error = "邮箱验证已失效，请重新验证。" });
                email = reader.GetString(0); password = reader.GetString(1);
            }
            var id = Database.Id();
            try
            {
                // The historical NOT NULL column is an internal opaque identifier only.
                // Account identity and the public API use the verified email exclusively.
                using var insert = Database.Command(connection, "INSERT INTO users(id,username,password_hash,created_at,email,email_verified_at) VALUES($id,$id,$password,$now,$email,$now);", transaction, ("$id", id), ("$password", password), ("$now", Database.Now()), ("$email", email)); insert.ExecuteNonQuery();
            }
            catch (SqliteException exception) when (exception.SqliteErrorCode == 19) { return Results.Conflict(new { error = "邮箱已被使用，请登录。" }); }
            using var delete = Database.Command(connection, "DELETE FROM email_challenges WHERE email=$email;", transaction, ("$email", email)); delete.ExecuteNonQuery();
            transaction.Commit();
            await SignIn(context, id, email);
            return Results.Ok(new { id, email, emailVerified = true });
        }).RequireRateLimiting("auth");
    }

    static async Task<IResult> Start(EmailStart input, Database db, VerificationMail mail, IPasswordHasher<UserRow> hasher)
    {
        var email = Normalize(input.Email);
        if (email is null || (input.Password is null || input.Password.Length is < 12 or > 128)) return Results.BadRequest(new { error = "请输入有效邮箱；密码须为 12–128 个字符。" });
        if (!mail.Ready) return Results.Json(new { error = "邮件服务尚未配置，暂时无法验证邮箱。" }, statusCode: 503);
        var id = Database.Id(); var code = RandomNumberGenerator.GetInt32(1000000).ToString("D6");
        using (var connection = db.Open())
        using (var transaction = connection.BeginTransaction())
        {
            using var duplicate = Database.Command(connection, "SELECT COUNT(*) FROM users WHERE email=$email;", transaction, ("$email", email));
            if (Convert.ToInt64(duplicate.ExecuteScalar()) > 0) return Results.Conflict(new { error = "邮箱已被使用，请登录。" });
            using var recent = Database.Command(connection, "SELECT COUNT(*) FROM email_challenges WHERE email=$email AND sent_at>$time;", transaction, ("$email", email), ("$time", Now - 60));
            if (Convert.ToInt64(recent.ExecuteScalar()) > 0) return Results.Json(new { error = "请等待 60 秒后重新发送。" }, statusCode: 429);
            // Remove expired challenges, but retain recent rows for the resend cooldown.
            using var cleanup = Database.Command(connection, "DELETE FROM email_challenges WHERE expires_at<$time OR email=$email;", transaction, ("$time", Now), ("$email", email)); cleanup.ExecuteNonQuery();
            var password = hasher.HashPassword(new UserRow(id, "", ""), input.Password!);
            using var insert = Database.Command(connection, "INSERT INTO email_challenges(id,email,password_hash,owner_id,code_hash,expires_at,sent_at) VALUES($id,$email,$password,NULL,$code,$expires,$now);", transaction,
                ("$id", id), ("$email", email), ("$password", password), ("$code", Hash(id + code)), ("$expires", Now + 600), ("$now", Now)); insert.ExecuteNonQuery(); transaction.Commit();
        }
        try { await mail.Send(email, code); }
        catch (Exception exception) when (exception is SmtpException or TimeoutException or InvalidOperationException or ArgumentException or FormatException or HttpRequestException or OperationCanceledException)
        {
            using var connection = db.Open(); using var delete = Database.Command(connection, "DELETE FROM email_challenges WHERE id=$id;", null, ("$id", id)); delete.ExecuteNonQuery();
            return Results.Json(new { error = "验证邮件发送失败，请稍后重试。" }, statusCode: 503);
        }
        return Results.Ok(new { challengeId = id, expiresIn = 600, resendAfter = 60 });
    }
}
