namespace Atheres.Atlas.Functions.Services;

public record EmailRequest(
    string To,
    string ToName,
    string Subject,
    string HtmlBody,
    string PlainTextBody
);

public interface IEmailService
{
    Task<bool> SendAsync(EmailRequest request, CancellationToken ct = default);
}
