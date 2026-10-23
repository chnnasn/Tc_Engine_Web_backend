using System.Net.Http.Json;
using System.Security.Cryptography;
using TomCat.Build;

namespace TomCat.Api;

public sealed class BuildSettings(IConfiguration configuration)
{
    public string? Secret => configuration["Build:Secret"];
    public bool Enabled => !string.IsNullOrWhiteSpace(configuration["Build:WorkerUrl"]) && Secret is { Length: >= 32 };
    public Uri Worker => BuildProtocol.ServiceUri(configuration["Build:WorkerUrl"]!);
    public int LeaseSeconds => Math.Clamp(configuration.GetValue("Build:LeaseSeconds", 60), 5, 300);
    public int TimeoutSeconds => Math.Clamp(configuration.GetValue("Build:TimeoutSeconds", 900), 30, 7200);
    public int MaxAttempts => Math.Clamp(configuration.GetValue("Build:MaxAttempts", 3), 1, 10);
    public int RetrySeconds => Math.Clamp(configuration.GetValue("Build:RetrySeconds", 5), 1, 60);
}

// The SQLite row is the durable outbox. The wake-up only reduces latency; a restart,
// lost HTTP acknowledgement or worker crash is recovered using the persisted lease.
public sealed class BuildDispatcher(Database db, BuildSettings settings, IHttpClientFactory clients,
    ILogger<BuildDispatcher> logger) : BackgroundService
{
    private readonly SemaphoreSlim wake = new(0, 1);
    public void Signal() { try { wake.Release(); } catch (SemaphoreFullException) { } }
    internal static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (settings.Enabled) await Dispatch(stoppingToken);
                await wake.WaitAsync(TimeSpan.FromSeconds(settings.RetrySeconds), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error)
            {
                logger.LogWarning(error, "Build event dispatch failed; persisted jobs will be retried");
                await Task.Delay(TimeSpan.FromSeconds(settings.RetrySeconds), stoppingToken);
            }
        }
    }

    private async Task Dispatch(CancellationToken cancellationToken)
    {
        BuildRequested? requested = null;
        using (var connection = db.Open())
        using (var transaction = connection.BeginTransaction())
        {
            using var recover = Database.Command(connection, """
                UPDATE build_jobs SET status='cancelled', finished_at=$date
                WHERE status IN ('queued','running') AND NOT EXISTS (
                    SELECT 1 FROM publications p WHERE p.build_job_id=build_jobs.id AND p.status='pending'
                    AND NOT EXISTS(SELECT 1 FROM publication_moderation m WHERE m.project_id=p.project_id));
                UPDATE publications SET status='failed', error='构建服务多次中断，请稍后重新发布。'
                WHERE status='pending' AND build_job_id IN (
                    SELECT id FROM build_jobs WHERE status='running' AND (lease_until<=$now OR deadline<=$now) AND attempts >= $max);
                UPDATE build_jobs SET status='failed', finished_at=$date
                WHERE status='running' AND (lease_until<=$now OR deadline<=$now) AND attempts >= $max;
                UPDATE build_jobs SET status='queued', lease_token='', next_attempt=$now
                WHERE status='running' AND (lease_until<=$now OR deadline<=$now);
                """, transaction, ("$now", Now), ("$date", Database.Now()), ("$max", settings.MaxAttempts));
            recover.ExecuteNonQuery();
            using var select = Database.Command(connection,
                "SELECT id FROM build_jobs WHERE status='queued' AND next_attempt <= $now ORDER BY created_at LIMIT 1;",
                transaction, ("$now", Now));
            if (select.ExecuteScalar() is string id)
            {
                var lease = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
                using var claim = Database.Command(connection, """
                    UPDATE build_jobs SET status='running', lease_token=$lease, lease_until=$until, deadline=$deadline, attempts=attempts+1
                    WHERE id=$id AND status='queued';
                    """, transaction, ("$lease", lease), ("$until", Now + settings.LeaseSeconds),
                    ("$deadline", Now + settings.TimeoutSeconds + 180), ("$id", id));
                claim.ExecuteNonQuery();
                requested = new(BuildProtocol.Version, id, lease, settings.LeaseSeconds, settings.TimeoutSeconds);
            }
            transaction.Commit();
        }
        if (requested is null) return;
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(settings.Worker, "/internal/builds/events"));
        request.Headers.Authorization = new("Bearer", settings.Secret);
        request.Content = JsonContent.Create(requested);
        using var response = await clients.CreateClient("build-events").SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode) return;
        // Only explicit backpressure proves that the worker did not accept the event.
        // Network errors retain the lease: the worker may already be running it.
        if (response.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable)
        {
            using var connection = db.Open();
            using var release = Database.Command(connection, """
                UPDATE build_jobs SET status='queued', lease_token='', attempts=attempts-1, next_attempt=$next
                WHERE id=$id AND status='running' AND lease_token=$lease;
                """, null, ("$id", requested.JobId), ("$lease", requested.LeaseToken), ("$next", Now + settings.RetrySeconds));
            release.ExecuteNonQuery();
        }
        else logger.LogWarning("Build event {Job} rejected with HTTP {Status}", requested.JobId, (int)response.StatusCode);
    }
}
