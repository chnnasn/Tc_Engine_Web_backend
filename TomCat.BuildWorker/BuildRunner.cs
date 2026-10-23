using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using TomCat.Build;

namespace TomCat.BuildWorker;

public sealed class WorkerSettings
{
    public Uri Api { get; }
    public string Secret { get; }
    public string Cli { get; }
    public string Arguments { get; }
    public int Parallelism { get; }
    public WorkerSettings(IConfiguration configuration)
    {
        Api = BuildProtocol.ServiceUri(configuration["Build:ApiUrl"] ?? "");
        Secret = configuration["Build:Secret"] ?? "";
        if (Secret.Length < 32) throw new InvalidOperationException("Build:Secret must contain at least 32 characters.");
        Cli = configuration["Cook:CliPath"] ?? "";
        if (!File.Exists(Cli)) throw new InvalidOperationException("Cook:CliPath must point to the build executable.");
        Arguments = configuration["Cook:CliArgs"] ?? "cook --project \"{project}\" --output \"{output}\"";
        Parallelism = Math.Clamp(configuration.GetValue("Build:Parallelism", 1), 1, 4);
    }
}

public sealed class BuildRunner : BackgroundService
{
    private sealed class LeaseRevokedException : Exception;
    private readonly WorkerSettings settings;
    private readonly HttpClient api;
    private readonly ILogger<BuildRunner> logger;
    private readonly Channel<BuildRequested> queue;
    private readonly ConcurrentDictionary<string, string> active = new();
    private readonly SemaphoreSlim slots;
    public BuildRunner(WorkerSettings settings, IHttpClientFactory clients, ILogger<BuildRunner> logger)
    {
        this.settings = settings;
        this.logger = logger;
        api = clients.CreateClient("build-api");
        slots = new(settings.Parallelism, settings.Parallelism);
        queue = Channel.CreateBounded<BuildRequested>(settings.Parallelism);
    }
    public bool Enqueue(BuildRequested requested)
    {
        if (active.TryGetValue(requested.JobId, out var lease)) return BuildProtocol.Equal(lease, requested.LeaseToken);
        if (!slots.Wait(0)) return false;
        if (active.TryAdd(requested.JobId, requested.LeaseToken))
        {
            if (queue.Writer.TryWrite(requested)) return true;
            active.TryRemove(requested.JobId, out _);
        }
        slots.Release();
        return false;
    }
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.WhenAll(
        Enumerable.Range(0, settings.Parallelism).Select(_ => Consume(stoppingToken)));
    private async Task Consume(CancellationToken stoppingToken)
    {
        await foreach (var requested in queue.Reader.ReadAllAsync(stoppingToken))
        {
            try { await Run(requested, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception error) { logger.LogWarning(error, "Build {Job} interrupted; the API lease will recover it", requested.JobId); }
            finally { active.TryRemove(requested.JobId, out _); slots.Release(); }
        }
    }
    private HttpRequestMessage Request(BuildRequested job, HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, new Uri(settings.Api, $"/internal/builds/{job.JobId}/{path}"));
        request.Headers.Authorization = new("Bearer", settings.Secret);
        request.Headers.Add(BuildProtocol.LeaseHeader, job.LeaseToken);
        return request;
    }
    private async Task Run(BuildRequested job, CancellationToken stoppingToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        lifetime.CancelAfter(TimeSpan.FromSeconds(job.TimeoutSeconds + 150));
        var heartbeat = Heartbeat(job, lifetime);
        var directory = Path.Combine(Path.GetTempPath(), "tomcat-build-" + Guid.NewGuid().ToString("N"));
        var output = Path.Combine(directory, ".cook", "Game.tcpak");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            await Materialize(job, directory, lifetime.Token);
            var (code, log) = await Cook(directory, output, job.TimeoutSeconds, lifetime.Token);
            if (code != 0) throw new InvalidOperationException($"打包工具退出码 {code}：{BuildProtocol.Tail(log)}");
            if (!File.Exists(output) || new FileInfo(output).Length is 0 or > BuildProtocol.MaxPackage)
                throw new InvalidOperationException("打包工具未产出有效的游戏包。");
            string sha;
            await using (var file = File.OpenRead(output)) sha = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, lifetime.Token));
            await Callback(job, "complete", () =>
            {
                var request = Request(job, HttpMethod.Post, "complete");
                request.Headers.Add(BuildProtocol.HashHeader, sha);
                request.Headers.ExpectContinue = true;
                request.Content = new StreamContent(File.OpenRead(output));
                request.Content.Headers.ContentType = new("application/octet-stream");
                return request;
            }, lifetime.Token);
            logger.LogInformation("Build {Job} completed and acknowledged", job.JobId);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { /* Shutdown, revoked lease or transfer deadline: retry from durable API job. */ }
        catch (LeaseRevokedException) { /* A cancellation or newer publication owns this project now. */ }
        catch (Exception error)
        {
            logger.LogWarning("Build {Job} failed: {Error}", job.JobId, BuildProtocol.Tail(error.Message));
            await Callback(job, "failed", () =>
            {
                var request = Request(job, HttpMethod.Post, "failed");
                // Fixed-length content also bounds the API's diagnostic request body.
                request.Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(new BuildFailure(BuildProtocol.Tail(error.Message))), Encoding.UTF8, "application/json");
                return request;
            }, lifetime.Token);
        }
        finally
        {
            await lifetime.CancelAsync();
            try { await heartbeat; } catch (OperationCanceledException) { }
            try { Directory.Delete(directory, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
    private async Task Heartbeat(BuildRequested job, CancellationTokenSource lifetime)
    {
        var lastSuccess = DateTimeOffset.UtcNow;
        while (!lifetime.IsCancellationRequested)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, job.LeaseSeconds / 3)));
                using var request = Request(job, HttpMethod.Post, "heartbeat");
                using var response = await api.SendAsync(request, timeout.Token);
                if (response.IsSuccessStatusCode) lastSuccess = DateTimeOffset.UtcNow;
                else if (response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.NotFound or HttpStatusCode.Unauthorized)
                { await lifetime.CancelAsync(); return; }
            }
            catch (Exception error) when (error is HttpRequestException or OperationCanceledException) { }
            if (DateTimeOffset.UtcNow - lastSuccess >= TimeSpan.FromSeconds(job.LeaseSeconds))
            { await lifetime.CancelAsync(); return; }
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, job.LeaseSeconds / 3)), lifetime.Token);
        }
    }
    private async Task Materialize(BuildRequested job, string directory, CancellationToken cancellationToken)
    {
        using var request = Request(job, HttpMethod.Get, "input");
        using var response = await api.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var input = await response.Content.ReadFromJsonAsync<BuildInput>(cancellationToken)
            ?? throw new InvalidOperationException("构建输入为空。");
        if (input.Files is null || input.Files.Count > 512 || input.Archive is null || Encoding.UTF8.GetByteCount(input.Archive) is 0 or > 4 * 1024 * 1024 ||
            !ulong.TryParse(input.SceneHandle, out var scene) || scene == 0) throw new InvalidOperationException("构建输入无效。");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var file in input.Files)
        {
            if (file.Path is null || !BuildProtocol.SafePath(file.Path) || !paths.Add(file.Path) || !BuildProtocol.Id(file.UploadId) ||
                !BuildProtocol.Hash(file.ContentHash) || file.Size is < 0 or > BuildProtocol.MaxFile || (total += file.Size) > BuildProtocol.MaxProject)
                throw new InvalidOperationException("构建资源路径或大小无效。");
            var target = Path.Combine(directory, file.Path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var download = Request(job, HttpMethod.Get, "files/" + file.UploadId);
            using var resource = await api.SendAsync(download, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            resource.EnsureSuccessStatusCode();
            await using var source = await resource.Content.ReadAsStreamAsync(cancellationToken);
            await using var destination = File.Create(target);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[65536];
            long length = 0;
            int count;
            while ((count = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                length += count;
                if (length > file.Size) throw new InvalidOperationException("资源下载超出清单大小。");
                hash.AppendData(buffer, 0, count);
                await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
            }
            if (length != file.Size || Convert.ToHexStringLower(hash.GetHashAndReset()) != file.ContentHash)
                throw new InvalidOperationException("资源校验失败：" + file.Path);
        }
        const string scenePath = "Assets/Scene/WebScene.tomcat";
        var sceneFile = Path.Combine(directory, scenePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(sceneFile)!);
        Directory.CreateDirectory(Path.Combine(directory, "ProjectSettings"));
        await File.WriteAllTextAsync(sceneFile, input.Archive, new UTF8Encoding(false), cancellationToken);
        await File.WriteAllTextAsync(sceneFile + ".tcmeta", $"SchemaVersion: 2\nAsset:\n  Handle: {scene}\n  Type: Scene\n  ImportSettings:\n    {{}}\n  SubAssets:\n    []\n", cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(directory, "ProjectSettings", "BuildSettings.json"),
            $"{{\"schemaVersion\":1,\"entrySceneHandle\":{scene},\"scenes\":[{{\"handle\":{scene},\"enabled\":true,\"pathHint\":\"Scene/WebScene.tomcat\"}}]}}", cancellationToken);
    }
    private async Task Callback(BuildRequested job, string operation, Func<HttpRequestMessage> create, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(60));
                using var request = create();
                using var response = await api.SendAsync(request, timeout.Token);
                if (response.IsSuccessStatusCode) return;
                if (response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.NotFound)
                    throw new LeaseRevokedException();
                response.EnsureSuccessStatusCode();
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested && attempt < 4 && error is HttpRequestException or TaskCanceledException)
            { logger.LogWarning("Build {Job} {Operation} callback will retry", job.JobId, operation); }
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(1 << attempt, 10)), cancellationToken);
        }
    }
    private async Task<(int Code, string Log)> Cook(string directory, string output, int seconds, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(settings.Cli)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = directory,
        };
        foreach (var argument in SplitArguments(settings.Arguments, Path.Combine(directory, "Project.tcproj"), output)) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动构建工具。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
        var stdout = ReadTail(process.StandardOutput, timeout.Token);
        var stderr = ReadTail(process.StandardError, timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            return (process.ExitCode, string.Join('\n', await Task.WhenAll(stdout, stderr)));
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            try { await Task.WhenAll(stdout, stderr); } catch (OperationCanceledException) { }
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException($"打包超时（超过 {seconds} 秒），已终止。");
        }
    }
    private static async Task<string> ReadTail(StreamReader reader, CancellationToken cancellationToken)
    {
        var tail = new StringBuilder();
        var buffer = new char[2048];
        int count;
        while ((count = await reader.ReadAsync(buffer, cancellationToken)) > 0)
        { tail.Append(buffer, 0, count); if (tail.Length > 4000) tail.Remove(0, tail.Length - 4000); }
        return tail.ToString();
    }
    private static IEnumerable<string> SplitArguments(string template, string project, string output)
    {
        var values = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        foreach (var character in template)
        {
            if (character == '"') quoted = !quoted;
            else if (!quoted && char.IsWhiteSpace(character))
            { if (current.Length > 0) { values.Add(current.ToString()); current.Clear(); } }
            else current.Append(character);
        }
        if (quoted) throw new InvalidOperationException("Cook:CliArgs contains an unmatched quote.");
        if (current.Length > 0) values.Add(current.ToString());
        return values.Select(value => value.Replace("{project}", project).Replace("{output}", output));
    }
}
