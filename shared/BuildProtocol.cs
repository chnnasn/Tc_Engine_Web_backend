using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;

namespace TomCat.Build;

public sealed record BuildRequested(string Protocol, string JobId, string LeaseToken, int LeaseSeconds, int TimeoutSeconds);
public sealed record BuildFile(string Path, string UploadId, string ContentHash, long Size);
public sealed record BuildInput(string EngineCommit, string SceneHandle, string Archive, List<BuildFile> Files);
public sealed record BuildFailure(string Error);

public static class BuildProtocol
{
    public const string Version = "tomcat.build.v1";
    public const string LeaseHeader = "X-TomCat-Build-Lease";
    public const string HashHeader = "X-TomCat-Package-Sha256";
    public const long MaxPackage = 256L * 1024 * 1024;
    public const int MaxProject = 36 * 1024 * 1024;
    public const int MaxFile = 8 * 1024 * 1024;

    public static bool Id(string? value) => value is not null && Regex.IsMatch(value, "^[a-f0-9]{32}$");
    public static bool Hash(string? value) => value is not null && Regex.IsMatch(value, "^[a-f0-9]{64}$");
    public static bool Equal(string expected, string actual) => CryptographicOperations.FixedTimeEquals(
        SHA256.HashData(Encoding.UTF8.GetBytes(expected)), SHA256.HashData(Encoding.UTF8.GetBytes(actual)));
    public static bool Authorized(HttpRequest request, string? secret) => secret is { Length: >= 32 } &&
        !request.Headers.ContainsKey("Origin") && Equal("Bearer " + secret, request.Headers.Authorization.ToString());
    public static Uri ServiceUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath != "/")
            throw new InvalidOperationException("Build service URL must be an http(s) origin without credentials, path, query or fragment.");
        return uri;
    }
    public static bool SafePath(string path)
    {
        if (path == "Project.tcproj") return true;
        if (Regex.IsMatch(path, "^ProjectSettings/[A-Za-z0-9_-]+\\.json$")) return true;
        return path.Length <= 240 && path.StartsWith("Assets/", StringComparison.Ordinal) &&
            path.Split('/').All(part => Regex.IsMatch(part, @"^[\p{L}\p{N}_][\p{L}\p{N}\p{M}_. -]*$") && !part.EndsWith('.') && !part.EndsWith(' '));
    }
    public static string Tail(string message) => string.IsNullOrWhiteSpace(message) ? "构建失败，未输出诊断信息。" :
        message.Length <= 1000 ? message.Trim() : "…" + message[^999..];
}
