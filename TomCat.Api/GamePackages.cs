using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace TomCat.Api;

public sealed record GamePackage(string Id, string Path, long ByteLength, string Sha256);

// 示例游戏包随 API 一起发布（Games/*.tcpak 复制到输出目录），启动时建立只读目录。
// 包是不可变内容，因此 ETag 直接取内容 SHA-256，可长期缓存。
public sealed class GameCatalog
{
    private readonly Dictionary<string, GamePackage> packages = new(StringComparer.Ordinal);

    public GameCatalog()
    {
        var directory = System.IO.Path.Combine(AppContext.BaseDirectory, "Games");
        if (!Directory.Exists(directory)) return;
        foreach (var file in Directory.EnumerateFiles(directory, "*.tcpak"))
        {
            var id = System.IO.Path.GetFileNameWithoutExtension(file);
            if (!Regex.IsMatch(id, "^[a-z0-9][a-z0-9-]{0,31}$"))
                throw new InvalidOperationException($"示例包文件名不是合法标识：{file}");
            using var stream = File.OpenRead(file);
            var header = new byte[12];
            if (stream.Read(header, 0, header.Length) != header.Length ||
                Encoding.ASCII.GetString(header, 0, 8) != "TCPACK01" ||
                BitConverter.ToUInt32(header, 8) != 8)
                throw new InvalidOperationException($"示例包不是 tcpak v8：{file}");
            stream.Position = 0;
            packages[id] = new GamePackage(id, file, stream.Length, Convert.ToHexStringLower(SHA256.HashData(stream)));
        }
    }

    public IReadOnlyCollection<GamePackage> All => packages.Values;
    public GamePackage? Find(string id) => packages.TryGetValue(id, out var package) ? package : null;
}

public static class GamePackages
{
    // 示例作品是公开内容，游客（未登录）也能打开游玩预览，因此这里不做鉴权。
    public static void Map(WebApplication app)
    {
        app.MapGet("/v1/games", (GameCatalog catalog) => Results.Ok(catalog.All
            .OrderBy(package => package.Id, StringComparer.Ordinal)
            .Select(package => new { id = package.Id, byteLength = package.ByteLength, etag = $"\"{package.Sha256}\"" })));

        app.MapGet("/v1/games/{id}/package", (string id, GameCatalog catalog, HttpContext context) =>
        {
            var package = catalog.Find(id);
            if (package is null) return Results.NotFound(new { error = "没有这个示例游戏包。" });
            var etag = $"\"{package.Sha256}\"";
            context.Response.Headers.ETag = etag;
            // 包内容不可变，覆盖全局的 no-store。
            context.Response.Headers.CacheControl = "public, max-age=0, must-revalidate";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            if (context.Request.Headers.IfNoneMatch.ToString() == etag) return Results.StatusCode(304);
            return Results.File(package.Path, "application/octet-stream", enableRangeProcessing: true);
        });
    }
}
