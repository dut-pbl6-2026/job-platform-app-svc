using System.Security.Cryptography;
using System.Text;

namespace App.Api.Auth;

/// <summary>
/// Shared bearer-token check for service-to-service endpoints under <c>/api/internal/*</c>.
/// Callers must present <c>Authorization: Bearer &lt;INTERNAL_SERVICE_TOKEN&gt;</c>.
/// Fails closed: an unconfigured token never makes an endpoint public.
/// </summary>
public static class InternalServiceAuth
{
    /// <summary>Configuration/env key holding the shared internal service token.</summary>
    public const string TokenConfigKey = "INTERNAL_SERVICE_TOKEN";

    private const string BearerPrefix = "Bearer ";

    /// <summary>
    /// Returns <c>null</c> when the request carries the configured token, otherwise a 401 result.
    /// </summary>
    public static IResult? Authorize(HttpContext ctx, IConfiguration config, ILogger logger)
    {
        var expected = config[TokenConfigKey];
        if (string.IsNullOrWhiteSpace(expected))
        {
            // Fail closed: an unconfigured token must never turn the endpoint public.
            // Return 401 (not 503) — misconfigured token is an auth failure, not a service outage.
            logger.LogError(
                "{TokenKey} is not configured. Returning 401 to avoid exposing the endpoint.",
                TokenConfigKey);
            return Unauthorized();
        }

        return IsAuthorized(ctx.Request.Headers.Authorization.ToString(), expected)
            ? null
            : Unauthorized();
    }

    private static IResult Unauthorized() =>
        Results.Json(new { message = "Unauthorized" }, statusCode: 401);

    private static bool IsAuthorized(string header, string expected)
    {
        if (!header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var token = header[BearerPrefix.Length..].Trim();
        if (token.Length == 0)
        {
            return false;
        }

        // Hash both sides first so the compared arrays are always the same length
        // (FixedTimeEquals returns early on a length mismatch, which would leak the
        // expected token's length), then compare in constant time.
        return CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(token)),
            SHA256.HashData(Encoding.UTF8.GetBytes(expected)));
    }
}
