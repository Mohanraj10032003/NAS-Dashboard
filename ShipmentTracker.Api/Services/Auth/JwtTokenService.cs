using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ShipmentTracker.Api.Options;

namespace ShipmentTracker.Api.Services.Auth;

/// <summary>
/// Creates and checks login tokens (JWT, HS256). A token is "header.payload.signature": the signature is an
/// HMAC of the first two parts with the server's secret key, so nobody can change the payload without the key.
/// </summary>
public sealed class JwtTokenService
{
    private readonly byte[] _key;
    private readonly TimeSpan _lifetime;

    public JwtTokenService(IOptions<AuthOptions> options, ILogger<JwtTokenService> logger)
    {
        var o = options.Value;
        var secret = o.JwtSecret;
        if (string.IsNullOrWhiteSpace(secret) || secret.Length < 32)
        {
            _key = RandomNumberGenerator.GetBytes(48);
            logger.LogWarning("Auth:JwtSecret is empty or shorter than 32 characters - using a random key. Everybody has to sign in again after a restart. Set a long Auth:JwtSecret to avoid that.");
        }
        else
        {
            _key = Encoding.UTF8.GetBytes(secret);
        }
        _lifetime = TimeSpan.FromHours(Math.Clamp(o.TokenHours, 1, 24 * 30));
    }

    public (string Token, DateTime ExpiresUtc) Create(UserEntry user)
    {
        var now = DateTimeOffset.UtcNow;
        var expires = now.Add(_lifetime);
        var header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { alg = "HS256", typ = "JWT" }));
        var payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["sub"] = user.Username,
            ["name"] = user.Name,
            ["role"] = user.Role,
            ["iat"] = now.ToUnixTimeSeconds(),
            ["exp"] = expires.ToUnixTimeSeconds()
        }));
        var unsigned = $"{header}.{payload}";
        return ($"{unsigned}.{Base64Url(Sign(unsigned))}", expires.UtcDateTime);
    }

    public ClaimsPrincipal? Validate(string token, string authenticationType)
    {
        try
        {
            var parts = token.Split('.');
            if (parts.Length != 3) return null;

            using (var headerDoc = JsonDocument.Parse(FromBase64Url(parts[0])))
            {
                if (!headerDoc.RootElement.TryGetProperty("alg", out var alg) || alg.GetString() != "HS256") return null;
            }

            var expected = Sign($"{parts[0]}.{parts[1]}");
            if (!CryptographicOperations.FixedTimeEquals(expected, FromBase64Url(parts[2]))) return null;

            using var doc = JsonDocument.Parse(FromBase64Url(parts[1]));
            var root = doc.RootElement;
            if (!root.TryGetProperty("exp", out var exp) || exp.GetInt64() < DateTimeOffset.UtcNow.ToUnixTimeSeconds()) return null;

            var username = root.GetProperty("sub").GetString() ?? "";
            var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, username),
                new Claim(ClaimTypes.Name, root.TryGetProperty("name", out var n) ? n.GetString() ?? username : username),
                new Claim(ClaimTypes.Role, root.TryGetProperty("role", out var r) ? r.GetString() ?? "Viewer" : "Viewer")
            ], authenticationType);
            return new ClaimsPrincipal(identity);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException or KeyNotFoundException)
        {
            return null;
        }
    }

    private byte[] Sign(string data) => HMACSHA256.HashData(_key, Encoding.ASCII.GetBytes(data));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        s = s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
        return Convert.FromBase64String(s);
    }
}
