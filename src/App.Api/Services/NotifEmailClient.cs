using System.Net.Http.Json;
using System.Text.Json;

namespace App.Api.Services;

/// <summary>
/// Sends a recruiter-composed email, optionally with an iCalendar attachment, through notif-svc's
/// internal endpoint. app-svc never talks SMTP directly: notif-svc owns the transport and the
/// credentials. A failure never rolls back a saved interview; callers report whether the mail went out.
/// </summary>
public class NotifEmailClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _internalToken;
    private readonly ILogger<NotifEmailClient> _logger;

    public NotifEmailClient(HttpClient http, IConfiguration configuration, ILogger<NotifEmailClient> logger)
    {
        _http = http;
        _baseUrl = (configuration["NOTIF_SERVICE_URL"] ?? "").Trim().TrimEnd('/');
        _internalToken = (configuration["NOTIF_INTERNAL_TOKEN"] ?? "").Trim();
        _logger = logger;
    }

    /// <summary>False when the URL or the shared token is missing; sending is skipped, not attempted.</summary>
    public bool Enabled => !string.IsNullOrWhiteSpace(_baseUrl) && !string.IsNullOrWhiteSpace(_internalToken);

    public async Task<EmailDispatchResult> SendAsync(EmailDispatch email, CancellationToken ct = default)
    {
        if (!Enabled)
        {
            _logger.LogWarning("notif-svc is not configured; email to {Recipient} was not sent.", email.To);
            return EmailDispatchResult.Failed("Email delivery is not configured.");
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/api/internal/email")
            {
                Content = JsonContent.Create(email, options: JsonOptions),
            };
            request.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _internalToken);

            using var response = await _http.SendAsync(request, ct);
            if (response.IsSuccessStatusCode)
                return EmailDispatchResult.Success;

            var detail = await response.Content.ReadAsStringAsync(ct);
            _logger.LogWarning(
                "notif-svc rejected an email to {Recipient}: HTTP {Status} {Detail}.",
                email.To,
                (int)response.StatusCode,
                detail.Length > 300 ? detail[..300] : detail);
            return EmailDispatchResult.Failed("The mail service rejected the message.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "notif-svc email call failed for {Recipient}.", email.To);
            return EmailDispatchResult.Failed("The mail service could not be reached.");
        }
    }
}

/// <summary>One outgoing message. <see cref="IcsContent"/> is attached as text/calendar when present.</summary>
public sealed record EmailDispatch(
    string To,
    string Subject,
    string Html,
    string Text,
    string? IcsFileName = null,
    string? IcsContent = null);

public sealed record EmailDispatchResult(bool Sent, string? Error)
{
    public static readonly EmailDispatchResult Success = new(true, null);
    public static EmailDispatchResult Failed(string error) => new(false, error);
}
