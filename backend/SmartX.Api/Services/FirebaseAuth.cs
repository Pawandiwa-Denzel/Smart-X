using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SmartX.Api.Services;

public sealed record AuthenticatedUser(string Uid, string? Email, string? Name);

// Verifies Firebase Authentication ID tokens.
//
// The frontend signs the user in with the Firebase Auth SDK and sends the ID
// token it gets back as "Authorization: Bearer <token>". A Firebase ID token is
// an RS256-signed JWT, so the API can check it on its own without calling
// Firebase for every request:
//
//   1. Download Google's public signing keys (JWKS) and cache them for as long
//      as Google's Cache-Control header allows.
//   2. Check the signature with the key whose "kid" matches the token header.
//   3. Check the claims Firebase documents: aud = project ID,
//      iss = https://securetoken.google.com/<project ID>, not expired,
//      issued in the past, and a non-empty subject (the user's UID).
//
// Anything that fails returns null and the request gets a 401.
public sealed class FirebaseTokenValidator(IConfiguration config, ILogger<FirebaseTokenValidator> log)
{
    private const string GoogleJwksUrl =
        "https://www.googleapis.com/service_accounts/v1/jwk/securetoken@system.gserviceaccount.com";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(2);

    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private Dictionary<string, RSAParameters> _keys = new();
    private DateTime _keysExpireAt = DateTime.MinValue;

    public string? ProjectId => config["Firebase:ProjectId"] is { Length: > 0 } id ? id : null;
    public bool IsConfigured => ProjectId is not null;

    private string JwksUrl => config["Firebase:JwksUrl"] is { Length: > 0 } url ? url : GoogleJwksUrl;

    public async Task<AuthenticatedUser?> ValidateAsync(string? token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token) || ProjectId is not { } projectId) return null;

        var parts = token.Split('.');
        if (parts.Length != 3) return null;

        try
        {
            using var header = JsonDocument.Parse(Base64UrlDecode(parts[0]));
            if (header.RootElement.GetProperty("alg").GetString() != "RS256") return null;
            var kid = header.RootElement.TryGetProperty("kid", out var k) ? k.GetString() : null;
            if (kid is null) return null;

            var key = await GetKeyAsync(kid, ct);
            if (key is null) return null;

            using var rsa = RSA.Create();
            rsa.ImportParameters(key.Value);
            var signed = Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}");
            if (!rsa.VerifyData(signed, Base64UrlDecode(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                return null;

            using var payload = JsonDocument.Parse(Base64UrlDecode(parts[1]));
            var claims = payload.RootElement;
            var now = DateTimeOffset.UtcNow;

            if (claims.GetProperty("aud").GetString() != projectId) return null;
            if (claims.GetProperty("iss").GetString() != $"https://securetoken.google.com/{projectId}") return null;
            if (DateTimeOffset.FromUnixTimeSeconds(claims.GetProperty("exp").GetInt64()) < now - ClockSkew) return null;
            if (DateTimeOffset.FromUnixTimeSeconds(claims.GetProperty("iat").GetInt64()) > now + ClockSkew) return null;

            var uid = claims.TryGetProperty("sub", out var sub) ? sub.GetString() : null;
            if (string.IsNullOrEmpty(uid)) return null;

            return new AuthenticatedUser(
                uid,
                claims.TryGetProperty("email", out var email) ? email.GetString() : null,
                claims.TryGetProperty("name", out var name) ? name.GetString() : null);
        }
        catch (Exception ex) when (ex is FormatException or JsonException or KeyNotFoundException or InvalidOperationException or CryptographicException)
        {
            log.LogDebug(ex, "Rejected malformed token");
            return null;
        }
    }

    private async Task<RSAParameters?> GetKeyAsync(string kid, CancellationToken ct)
    {
        if (DateTime.UtcNow < _keysExpireAt && _keys.TryGetValue(kid, out var cached)) return cached;

        // Keys expired, or a kid we haven't seen (Google rotates keys) - refresh once.
        await _refreshGate.WaitAsync(ct);
        try
        {
            if (DateTime.UtcNow >= _keysExpireAt || !_keys.ContainsKey(kid))
            {
                await RefreshKeysAsync(ct);
            }

            return _keys.TryGetValue(kid, out var key) ? key : null;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task RefreshKeysAsync(CancellationToken ct)
    {
        try
        {
            using var res = await Http.GetAsync(JwksUrl, ct);
            res.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));

            var keys = new Dictionary<string, RSAParameters>();
            foreach (var jwk in doc.RootElement.GetProperty("keys").EnumerateArray())
            {
                if (jwk.GetProperty("kty").GetString() != "RSA") continue;
                keys[jwk.GetProperty("kid").GetString()!] = new RSAParameters
                {
                    Modulus = Base64UrlDecode(jwk.GetProperty("n").GetString()!),
                    Exponent = Base64UrlDecode(jwk.GetProperty("e").GetString()!),
                };
            }

            _keys = keys;
            var maxAge = res.Headers.CacheControl?.MaxAge ?? TimeSpan.FromHours(1);
            _keysExpireAt = DateTime.UtcNow + maxAge;
            log.LogInformation("Loaded {Count} Firebase signing keys (valid for {MaxAge})", keys.Count, maxAge);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            // Keep any keys we already had; try again in a minute.
            _keysExpireAt = DateTime.UtcNow.AddMinutes(1);
            log.LogWarning(ex, "Could not download Firebase signing keys from {Url}", JwksUrl);
        }
    }

    private static byte[] Base64UrlDecode(string input)
    {
        var s = input.Replace('-', '+').Replace('_', '/');
        s = s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
        return Convert.FromBase64String(s);
    }
}

// Gatekeeper for every /api route.
//
//  - Public:            "/", /api/health, /api/auth/status
//  - Device endpoints:  telemetry ingest, device provisioning and the device
//                       shadow accept EITHER a signed-in user OR the fleet's
//                       X-Device-Key header (ESP32 boards don't log in with
//                       a person's account - they carry a device key).
//  - Everything else:   requires a valid Firebase ID token.
//
// The SSE stream (/api/events) is opened with EventSource, which can't send
// headers, so it may pass the token as ?access_token= instead.
public sealed class FirebaseAuthMiddleware(RequestDelegate next, FirebaseTokenValidator validator, IConfiguration config)
{
    public async Task InvokeAsync(HttpContext ctx)
    {
        var path = ctx.Request.Path.Value ?? "/";
        var method = ctx.Request.Method;

        if (HttpMethods.IsOptions(method) || !path.StartsWith("/api", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/api/health", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/api/auth/status", StringComparison.OrdinalIgnoreCase))
        {
            await next(ctx);
            return;
        }

        if (IsDeviceEndpoint(method, path) && HasValidDeviceKey(ctx))
        {
            ctx.Items["device"] = true;
            await next(ctx);
            return;
        }

        string? token = null;
        if (AuthenticationHeaderValue.TryParse(ctx.Request.Headers.Authorization, out var auth) &&
            auth.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase))
        {
            token = auth.Parameter;
        }
        else if (path.Equals("/api/events", StringComparison.OrdinalIgnoreCase))
        {
            token = ctx.Request.Query["access_token"];
        }

        var user = await validator.ValidateAsync(token, ctx.RequestAborted);
        if (user is null)
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await ctx.Response.WriteAsJsonAsync(new
            {
                error = validator.IsConfigured
                    ? "Sign in required - send a valid Firebase ID token as 'Authorization: Bearer <token>'."
                    : "Firebase is not configured on the API. Set Firebase:ProjectId in appsettings.json.",
            });
            return;
        }

        ctx.Items["user"] = user;
        await next(ctx);
    }

    private static bool IsDeviceEndpoint(string method, string path) =>
        (HttpMethods.IsPost(method) && (path.Equals("/api/telemetry", StringComparison.OrdinalIgnoreCase) ||
                                        path.Equals("/api/telemetry/batch", StringComparison.OrdinalIgnoreCase) ||
                                        path.Equals("/api/sensors", StringComparison.OrdinalIgnoreCase))) ||
        (HttpMethods.IsGet(method) && path.StartsWith("/api/devices/", StringComparison.OrdinalIgnoreCase) &&
                                      path.EndsWith("/shadow", StringComparison.OrdinalIgnoreCase));

    private bool HasValidDeviceKey(HttpContext ctx)
    {
        var expected = config["Devices:ApiKey"];
        var supplied = ctx.Request.Headers["X-Device-Key"].ToString();
        if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(supplied)) return false;

        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(supplied));
    }
}
