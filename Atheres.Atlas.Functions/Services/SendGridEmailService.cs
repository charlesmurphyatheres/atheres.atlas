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

    public async Task<bool> SendAsync(EmailRequest request, CancellationToken ct = default)
    {
        try
        {
            var client = new SendGridClient(_apiKey);
            var from = new EmailAddress(_fromEmail, _fromName);
            var to = new EmailAddress(request.To, request.ToName);

            var msg = MailHelper.CreateSingleEmail(
                from, to, request.Subject,
                request.PlainTextBody,
                request.HtmlBody
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
