using Atheres.Atlas.Scheduling.Providers;
using Microsoft.Extensions.Logging;

namespace Atheres.Atlas.Functions.Services;

/// <summary>
/// Bridges <see cref="ISchedulingEmailSender"/> (defined in the
/// Scheduling project) onto the existing <see cref="IEmailService"/>
/// SendGrid pipeline so the Scheduling project doesn't have to take
/// a SendGrid reference.
/// </summary>
public sealed class SchedulingEmailSenderAdapter : ISchedulingEmailSender
{
    private readonly IEmailService _email;
    private readonly ILogger<SchedulingEmailSenderAdapter> _log;

    public SchedulingEmailSenderAdapter(IEmailService email, ILogger<SchedulingEmailSenderAdapter> log)
    {
        _email = email;
        _log   = log;
    }

    public async Task SendAsync(
        IReadOnlyList<string> recipients,
        string subject,
        string htmlBody,
        CancellationToken ct = default)
    {
        // SendGridEmailService takes one To per call. Email-scheduling
        // recipients are typically a small list (the store's ops contacts),
        // so a sequential fan-out is fine.
        foreach (var to in recipients)
        {
            var ok = await _email.SendAsync(
                new EmailRequest(
                    To:            to,
                    ToName:        to,
                    Subject:       subject,
                    HtmlBody:      htmlBody,
                    PlainTextBody: StripHtml(htmlBody)),
                ct).ConfigureAwait(false);
            if (!ok) _log.LogWarning("Scheduling email send failed for {Recipient}", to);
        }
    }

    private static string StripHtml(string html)
        => System.Text.RegularExpressions.Regex.Replace(html, "<[^>]+>", " ").Trim();
}
