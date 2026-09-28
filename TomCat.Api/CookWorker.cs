using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TomCat.Api;

// Single-instance by design, like the SQLite volume: one cook at a time, pending tasks are
// retried after a restart. The cook runs the upstream TomCatCLI as a separate process over a
// materialized copy of the saved revision; user projects are untrusted input for that native
// tool, so the copy lives in a throwaway directory and the process is killed on timeout.
public sealed class CookWorker(Database db, IConfiguration config, ILogger<CookWorker> logger) : BackgroundService
{
    public const long MaxPackage = 256L * 1024 * 1024;
    public const string DefaultArguments = "cook --project \"{project}\" --output \"{output}\"";
    private const string SceneRelativePath = "Assets/Scene/WebScene.tomcat";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var poll = TimeSpan.FromSeconds(Math.Clamp(config.GetValue("Cook:PollSeconds", 3), 1, 600));
        using var timer = new PeriodicTimer(poll);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { await ProcessNext(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) { logger.LogError(error, "Publish cook pass failed"); }
        }
    }

    private async Task ProcessNext(CancellationToken stoppingToken)
    {
        string projectId;
        string revision;
        using (var connection = db.Open())
        {
            using var command = Database.Command(connection,
                "SELECT project_id, revision_id FROM publications WHERE status='pending' ORDER BY requested_at LIMIT 1;");
            using var reader = command.ExecuteReader();
            if (!reader.Read()) return;
            projectId = reader.GetString(0);
            revision = reader.GetString(1);
        }
        var cli = config["Cook:CliPath"];
        if (string.IsNullOrWhiteSpace(cli) || !File.Exists(cli))
        {
            await Fail(projectId, "服务器未配置打包工具（Cook:CliPath）。");
            return;
        }
        var timeout = TimeSpan.FromSeconds(Math.Clamp(config.GetValue("Cook:TimeoutSeconds", 900), 30, 7200));
        var arguments = config["Cook:CliArgs"] is { Length: > 0 } template ? template : DefaultArguments;
        var directory = Path.Combine(Path.GetTempPath(), "tomcat-cook-" + Guid.NewGuid().ToString("N"));
        var output = Path.Combine(directory, ".cook", "Game.tcpak");
        // Every failure after the claim must land in the row: a stuck pending task would retry forever.
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            await MaterializeAsync(db, projectId, revision, directory, stoppingToken);
            var (code, log) = await RunCookAsync(cli, arguments, directory, output, timeout, stoppingToken);
            // Shutdown leaves the row pending so the next process retries; real failures are recorded.
            if (stoppingToken.IsCancellationRequested) return;
            if (code != 0)
            {
                await Fail(projectId, $"打包工具退出码 {code}：{Tail(log)}");
                return;
            }
            if (!File.Exists(output) || new FileInfo(output).Length is 0 or > MaxPackage)
            {
                await Fail(projectId, "打包工具未产出有效的游戏包。");
                return;
            }
            var bytes = await File.ReadAllBytesAsync(output, stoppingToken);
            var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
            using (var connection = db.Open())
            using (var command = Database.Command(connection,
                "UPDATE publications SET status='published', error='', package=$package, byte_length=$size, sha256=$sha, published_at=$now " +
                "WHERE project_id=$project AND status='pending' AND revision_id=$revision;", null,
                ("$package", bytes), ("$size", bytes.Length), ("$sha", sha), ("$now", Database.Now()),
                ("$project", projectId), ("$revision", revision)))
            {
                if (command.ExecuteNonQuery() == 1)
                    logger.LogInformation("Published project {Project} revision {Revision} ({Size} bytes)", projectId, revision, bytes.Length);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { /* Shutdown: keep pending. */ }
        catch (Exception error) { await Fail(projectId, Tail(error.Message)); }
        finally
        {
            try { Directory.Delete(directory, true); } catch { /* The temp copy is best effort. */ }
        }
    }

    // Rebuilds an engine project directory from a saved revision. The web session keeps the
    // scene only as an archive string, so the entry scene file and BuildSettings are written
    // here to match the on-disk layout the CLI cooks from (see Samples/PhysicsPlayground).
    private static async Task MaterializeAsync(Database db, string projectId, string revision, string directory, CancellationToken stoppingToken)
    {
        string payload;
        using (var connection = db.Open())
        {
            using var command = Database.Command(connection,
                "SELECT r.payload FROM revisions r JOIN projects p ON p.id = r.project_id WHERE r.id=$revision AND p.id=$project;", null,
                ("$revision", revision), ("$project", projectId));
            payload = command.ExecuteScalar() as string
                ?? throw new InvalidOperationException("要发布的修订不存在，请重新保存后再发布。");
        }
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        if (!ProjectFiles.TryManifest(root, out var files))
            throw new InvalidOperationException("此修订缺少完整资源清单，请在编辑器中重新保存后再发布。");
        var sceneHandle = root.GetProperty("sceneHandle").GetString()!;
        var archive = root.GetProperty("archive").GetString()!;
        Directory.CreateDirectory(directory);
        foreach (var file in files)
        {
            var target = Path.Combine(directory, file.Path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await File.WriteAllBytesAsync(target, await ReadUploadAsync(db, projectId, file, stoppingToken), stoppingToken);
        }
        var sceneFile = Path.Combine(directory, SceneRelativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(sceneFile)!);
        await File.WriteAllTextAsync(sceneFile, archive, new UTF8Encoding(false), stoppingToken);
        await File.WriteAllTextAsync(sceneFile + ".tcmeta",
            $"SchemaVersion: 2\nAsset:\n  Handle: {sceneHandle}\n  Type: Scene\n  ImportSettings:\n    {{}}\n  SubAssets:\n    []\n",
            new UTF8Encoding(false), stoppingToken);
        var buildSettings =
            "{\n" +
            "  \"schemaVersion\": 1,\n" +
            $"  \"entrySceneHandle\": {sceneHandle},\n" +
            "  \"scenes\": [\n" +
            $"    {{ \"handle\": {sceneHandle}, \"enabled\": true, \"pathHint\": \"{SceneRelativePath["Assets/".Length]}\" }}\n" +
            "  ]\n" +
            "}\n";
        await File.WriteAllTextAsync(Path.Combine(directory, "ProjectSettings", "BuildSettings.json"), buildSettings, new UTF8Encoding(false), stoppingToken);
    }

    private static async Task<byte[]> ReadUploadAsync(Database db, string projectId, RevisionFile file, CancellationToken stoppingToken)
    {
        using var connection = db.Open();
        using var command = Database.Command(connection,
            "SELECT content FROM uploads WHERE id=$upload AND project_id=$project AND content_hash=$hash AND byte_length=$size;", null,
            ("$upload", file.UploadId), ("$project", projectId), ("$hash", file.ContentHash), ("$size", file.Size));
        var result = await command.ExecuteScalarAsync(stoppingToken);
        return result as byte[] ?? throw new InvalidOperationException($"修订资源缺失或不一致：{file.Path}");
    }

    private async Task<(int Code, string Log)> RunCookAsync(string cli, string arguments, string directory, string output,
        TimeSpan timeout, CancellationToken stoppingToken)
    {
        var start = new ProcessStartInfo
        {
            FileName = cli,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = directory,
        };
        foreach (var argument in SplitArguments(arguments, Path.Combine(directory, "Project.tcproj"), output))
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动打包工具进程。");
        var stdout = process.StandardOutput.ReadToEndAsync(stoppingToken);
        var stderr = process.StandardError.ReadToEndAsync(stoppingToken);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        cancellation.CancelAfter(timeout);
        try { await process.WaitForExitAsync(cancellation.Token); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* The process may already be gone. */ }
            if (stoppingToken.IsCancellationRequested) throw;
            throw new InvalidOperationException($"打包超时（超过 {(int)timeout.TotalSeconds} 秒），已终止。");
        }
        var lines = await Task.WhenAll(stdout, stderr);
        return (process.ExitCode, string.Join('\n', lines.Where(line => line.Length > 0)));
    }

    // Supports the plain CLI form and wrappers such as "TomCat.exe --cli cook ..." via Cook:CliArgs.
    private static IEnumerable<string> SplitArguments(string template, string project, string output)
    {
        var values = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        foreach (var character in template)
        {
            if (character == '"') quoted = !quoted;
            else if (!quoted && char.IsWhiteSpace(character))
            {
                if (current.Length > 0) { values.Add(current.ToString()); current.Clear(); }
            }
            else current.Append(character);
        }
        if (current.Length > 0) values.Add(current.ToString());
        return values.Select(value => value.Replace("{project}", project).Replace("{output}", output));
    }

    private static string Tail(string message)
    {
        message = message.Trim();
        if (message.Length <= 1000) return message.Length == 0 ? "打包失败，未输出诊断信息。" : message;
        return "…" + message[^1000..];
    }

    private async Task Fail(string projectId, string message)
    {
        try
        {
            using var connection = db.Open();
            using var command = Database.Command(connection,
                "UPDATE publications SET status='failed', error=$error WHERE project_id=$project AND status='pending';", null,
                ("$error", Tail(message)), ("$project", projectId));
            command.ExecuteNonQuery();
        }
        catch (Exception error) { logger.LogError(error, "Could not record the publish failure for {Project}", projectId); }
    }
}
