using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace TomCat.Api;

// Server-clock presence leases; overlapping browser tabs count once per account.
public static partial class Activity
{
    public sealed record HeartbeatInput(string? TabId);
    public static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    public static string Day(long time) => DateTimeOffset.FromUnixTimeSeconds(time).ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd");
    [GeneratedRegex("^[a-f0-9]{32}$")]
    private static partial Regex TabIdPattern();
    public static void Settle(SqliteConnection connection, SqliteTransaction transaction, string owner, long now)
    {
        long cursor;
        using (var query = Database.Command(connection, "SELECT activity_cursor FROM users WHERE id=$owner", transaction, ("$owner", owner))) cursor = Convert.ToInt64(query.ExecuteScalar());
        long last;
        using (var query = Database.Command(connection, "SELECT COALESCE(MAX(last_heartbeat),0) FROM activity_presence WHERE owner_id=$owner", transaction, ("$owner", owner))) last = Convert.ToInt64(query.ExecuteScalar());
        var end = Math.Min(now, last + 60);
        var seconds = cursor == 0 || last == 0 ? 0 : Math.Max(0, end - cursor);
        if (seconds > 0)
        {
            using var update = Database.Command(connection, "UPDATE users SET online_seconds=online_seconds+$seconds WHERE id=$owner", transaction, ("$owner", owner), ("$seconds", seconds));
            update.ExecuteNonQuery();
            // A heartbeat near midnight may span two reporting days (Asia/Shanghai).
            for (var start = cursor; start < end;)
            {
                var date = Day(start);
                var midnight = DateTimeOffset.Parse(date + "T00:00:00+08:00").AddDays(1).ToUnixTimeSeconds();
                var stop = Math.Min(end, midnight);
                using var daily = Database.Command(connection, "INSERT INTO activity_daily VALUES($owner,$day,$seconds) ON CONFLICT(owner_id,day) DO UPDATE SET online_seconds=online_seconds+$seconds", transaction,
                    ("$owner", owner), ("$day", date), ("$seconds", stop - start));
                daily.ExecuteNonQuery(); start = stop;
            }
        }
        using var advance = Database.Command(connection, "UPDATE users SET activity_cursor=$now WHERE id=$owner", transaction, ("$owner", owner), ("$now", now));
        advance.ExecuteNonQuery();
    }
    public static void Clear(SqliteConnection connection, SqliteTransaction transaction, string owner)
    {
        Settle(connection, transaction, owner, Now);
        using var delete = Database.Command(connection, "DELETE FROM activity_presence WHERE owner_id=$owner", transaction, ("$owner", owner));
        delete.ExecuteNonQuery();
    }
    public static void Map(WebApplication app)
    {
        foreach (var action in new[] { "heartbeat", "stop" })
        {
            var stop = action == "stop";
            app.MapPost($"/v1/activity/{action}", (HeartbeatInput input, Database db, HttpContext context) =>
            {
                if (input.TabId is null || !TabIdPattern().IsMatch(input.TabId)) return Results.BadRequest();
                var owner = context.User.FindFirstValue(ClaimTypes.NameIdentifier)!; var now = Now;
                using var connection = db.Open(); using var transaction = connection.BeginTransaction(deferred: false);
                Settle(connection, transaction, owner, now);
                using var change = Database.Command(connection, stop
                    ? "DELETE FROM activity_presence WHERE owner_id=$owner AND tab_id=$tab"
                    : "INSERT INTO activity_presence VALUES($owner,$tab,$now) ON CONFLICT(owner_id,tab_id) DO UPDATE SET last_heartbeat=$now", transaction,
                    ("$owner", owner), ("$tab", input.TabId), ("$now", now));
                change.ExecuteNonQuery();
                if (!stop)
                {
                    using var seen = Database.Command(connection, "UPDATE users SET last_seen_at=$now WHERE id=$owner; INSERT OR IGNORE INTO activity_daily VALUES($owner,$day,0)", transaction,
                        ("$owner", owner), ("$now", now), ("$day", Day(now)));
                    seen.ExecuteNonQuery();
                }
                using var clean = Database.Command(connection, "DELETE FROM activity_presence WHERE owner_id=$owner AND last_heartbeat<$cutoff", transaction, ("$owner", owner), ("$cutoff", now - 120));
                clean.ExecuteNonQuery(); transaction.Commit();
                return Results.NoContent();
            }).RequireAuthorization().RequireRateLimiting("activity");
        }
    }
}
