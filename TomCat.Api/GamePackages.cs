using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TomCat.Api;

public sealed record GamePackage(string Id, string Path, long ByteLength, string Sha256);

public sealed record SampleControl(string Key, string Action);

public sealed class SampleGame
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Subtitle { get; init; }
    public required string Author { get; init; }
    public required string Category { get; init; }
    public required string Color { get; init; }
    public required string Description { get; init; }
    public required GamePackage Package { get; init; }
    public string? ImagePath { get; init; }
    public string? SourcePath { get; init; }
    public SampleControl[] Controls { get; init; } = [];
}

file sealed class CatalogFile
{
    public string? Id { get; init; }
    public string? Title { get; init; }
    public string? Subtitle { get; init; }
    public string? Author { get; init; }
    public string? Category { get; init; }
    public string? Color { get; init; }
    public string? Description { get; init; }
    public string? ImageFile { get; init; }
    public string? SourceFile { get; init; }
    public SampleControl[]? Controls { get; init; }
}

// 示例目录和游戏包随 API 一起发布。包不可变，ETag 取内容 SHA-256。
public sealed class GameCatalog
{
    private readonly List<SampleGame> samples = [];

    public GameCatalog()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "Games");
        var catalogPath = Path.Combine(directory, "catalog.json");
        if (!File.Exists(catalogPath)) return;
        var entries = JsonSerializer.Deserialize<List<CatalogFile>>(File.ReadAllText(catalogPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
        foreach (var entry in entries)
        {
            if (entry.Id is null || !Regex.IsMatch(entry.Id, "^[a-z0-9][a-z0-9-]{0,31}$") ||
                string.IsNullOrWhiteSpace(entry.Title) || string.IsNullOrWhiteSpace(entry.Subtitle) ||
                string.IsNullOrWhiteSpace(entry.Author) || string.IsNullOrWhiteSpace(entry.Category) ||
                string.IsNullOrWhiteSpace(entry.Color) || string.IsNullOrWhiteSpace(entry.Description))
                throw new InvalidOperationException($"示例目录缺少必要字段：{entry.Id}");
            var packagePath = Path.Combine(directory, entry.Id + ".tcpak");
            if (!File.Exists(packagePath)) throw new InvalidOperationException($"示例包不存在：{packagePath}");
            using var stream = File.OpenRead(packagePath);
            var header = new byte[12];
            if (stream.Read(header, 0, header.Length) != header.Length ||
                Encoding.ASCII.GetString(header, 0, 8) != "TCPACK01" ||
                BitConverter.ToUInt32(header, 8) != 8)
                throw new InvalidOperationException($"示例包不是 tcpak v8：{packagePath}");
            stream.Position = 0;
            var image = Located(directory, entry.ImageFile);
            var source = Located(directory, entry.SourceFile);
            samples.Add(new SampleGame
            {
                Id = entry.Id, Title = entry.Title, Subtitle = entry.Subtitle, Author = entry.Author,
                Category = entry.Category, Color = entry.Color, Description = entry.Description,
                Controls = entry.Controls ?? [], ImagePath = image, SourcePath = source,
                Package = new GamePackage(entry.Id, packagePath, stream.Length, Convert.ToHexStringLower(SHA256.HashData(stream))),
            });
        }
    }

    public IReadOnlyList<SampleGame> All => samples;
    public SampleGame? Find(string id) => samples.Find(sample => sample.Id == id);

    static string? Located(string directory, string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Contains('/') || name.Contains('\\') || name.Contains("..")) return null;
        var path = Path.Combine(directory, name);
        return File.Exists(path) ? path : null;
    }
}

public static class GamePackages
{
    // 示例作品是公开内容，游客也能打开游玩预览，因此这里不做鉴权。
    public static void Map(WebApplication app)
    {
        app.MapGet("/v1/games", (GameCatalog catalog) => Results.Ok(catalog.All.Select(Describe)));

        app.MapGet("/v1/games/{id}/package", (string id, GameCatalog catalog, HttpContext context) =>
        {
            var sample = catalog.Find(id);
            if (sample is null) return Results.NotFound(new { error = "没有这个示例游戏包。" });
            var package = sample.Package;
            var etag = $"\"{package.Sha256}\"";
            context.Response.Headers.ETag = etag;
            context.Response.Headers.CacheControl = "public, max-age=0, must-revalidate";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            if (context.Request.Headers.IfNoneMatch.ToString() == etag) return Results.StatusCode(304);
            return Results.File(package.Path, "application/octet-stream", enableRangeProcessing: true);
        });

        app.MapGet("/v1/games/{id}/cover", (string id, GameCatalog catalog) =>
        {
            var sample = catalog.Find(id);
            return sample?.ImagePath is null ? Results.NotFound() : Results.File(sample.ImagePath, "image/png");
        });

        app.MapGet("/v1/games/{id}/source", (string id, GameCatalog catalog) =>
        {
            var sample = catalog.Find(id);
            return sample?.SourcePath is null ? Results.NotFound() : Results.File(sample.SourcePath, "application/zip", $"{sample.Id}-source.zip");
        });
    }

    static object Describe(SampleGame sample) => new
    {
        id = sample.Id, title = sample.Title, subtitle = sample.Subtitle, author = sample.Author,
        category = sample.Category, color = sample.Color, description = sample.Description,
        image = sample.ImagePath is null ? "" : $"/v1/games/{sample.Id}/cover",
        sourceUrl = sample.SourcePath is null ? null : $"/v1/games/{sample.Id}/source",
        controls = sample.Controls.Select(control => new { key = control.Key, action = control.Action }),
        byteLength = sample.Package.ByteLength, etag = $"\"{sample.Package.Sha256}\"",
    };
}
