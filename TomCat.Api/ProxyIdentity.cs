using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace TomCat.Api;

public static class ProxyIdentity
{
    public static string Client(HttpContext context, string? secret) => TrustedClient(context, secret) ?? context.Connection.RemoteIpAddress?.MapToIPv6().ToString() ?? "unknown";
    static byte[] Decode(string value) => Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/').PadRight((value.Length + 3) / 4 * 4, '='));
    public static string? TrustedClient(HttpContext context, string? secret)
    {
        if (!string.IsNullOrEmpty(secret) && context.Request.Headers["x-nf-sign"].FirstOrDefault() is { } signature && signature.Length < 4096)
        {
            try
            {
                var parts = signature.Split('.');
                if (parts.Length == 3)
                {
                    using var header = JsonDocument.Parse(Decode(parts[0]));
                    var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]));
                    if (header.RootElement.GetProperty("alg").GetString() == "HS256" && CryptographicOperations.FixedTimeEquals(expected, Decode(parts[2])))
                    {
                        using var payload = JsonDocument.Parse(Decode(parts[1]));
                        if (payload.RootElement.GetProperty("iss").GetString() == "netlify" && payload.RootElement.GetProperty("exp").GetInt64() > DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                            && IPAddress.TryParse(context.Request.Headers["x-nf-client-connection-ip"].FirstOrDefault(), out var ip)) return ip.MapToIPv6().ToString();
                    }
                }
            }
            catch (Exception error) when (error is FormatException or JsonException or KeyNotFoundException or InvalidOperationException) { }
        }
        return null;
    }
}
