using Microsoft.Extensions.Logging;
using SendGrid;
using SendGrid.Helpers.Mail;

namespace Atheres.Atlas.Functions.Services;

public class SendGridEmailService : IEmailService
{
    private readonly ILogger<SendGridEmailService> _logger;
    private readonly string _apiKey;
    private readonly string _fromEmail;
    private readonly string _fromName;

    public SendGridEmailService(ILogger<SendGridEmailService> logger)
    {
        _logger = logger;
        _apiKey = Environment.GetEnvironmentVariable("SendGridApiKey")
                  ?? throw new InvalidOperationException("SendGridApiKey is not configured.");
        _fromEmail = Environment.GetEnvironmentVariable("SendGridFromEmail") ?? "noreply@atheres-atlas.com";
        _fromName = Environment.GetEnvironmentVariable("SendGridFromName") ?? "Atheres Atlas Delivery";
    }

    // DEBUG: redirect all emails to this address. Remove for production.
    private const string DebugRedirectTo = "charles.rollins@sbcglobal.net";

    public async Task<bool> SendAsync(EmailRequest request, CancellationToken ct = default)
    {
        try
        {
            var client = new SendGridClient(_apiKey);
            var from = new EmailAddress(_fromEmail, _fromName);

            // DEBUG: redirect to test inbox and inject original recipient info
            var actualTo = request.To;
            var to = new EmailAddress(DebugRedirectTo, $"DEBUG — {request.ToName}");

            var debugBanner = $"""
                <div style="background:#fef3c7;border:2px solid #f59e0b;border-radius:8px;padding:12px 16px;margin-bottom:20px;font-family:Arial,sans-serif;">
                    <p style="margin:0 0 4px 0;font-size:14px;font-weight:bold;color:#92400e;">DEBUG MODE — Email Redirected</p>
                    <p style="margin:0;font-size:13px;color:#78350f;">Intended recipient: <strong>{actualTo}</strong> ({request.ToName})</p>
                    <p style="margin:4px 0 0 0;font-size:11px;color:#a16207;">This email was intercepted for testing. In production it would go to the address above.</p>
                </div>
                """;
            var debugPlain = $"[DEBUG] Intended recipient: {actualTo} ({request.ToName})\n---\n";

            var msg = MailHelper.CreateSingleEmail(
                from, to,
                $"[DEBUG] {request.Subject}",
                debugPlain + request.PlainTextBody,
                debugBanner + request.HtmlBody
            );

            var response = await client.SendEmailAsync(msg, ct);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Body.ReadAsStringAsync(ct);
                _logger.LogError("SendGrid error {StatusCode}: {Body}", response.StatusCode, body);
                return false;
            }

            _logger.LogInformation("Email sent to {To} — Subject: {Subject}", request.To, request.Subject);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send email to {To}", request.To);
            return false;
        }
    }
}
