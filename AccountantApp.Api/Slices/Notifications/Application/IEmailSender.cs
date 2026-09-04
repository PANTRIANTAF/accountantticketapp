namespace AccountantApp.Api.Slices.Notifications.Application;

public sealed record EmailMessage(string To, string Subject, string Body);

public enum EmailSendOutcome
{
    Sent,
    TransientFailure,
    PermanentFailure
}

public sealed record EmailSendResult(EmailSendOutcome Outcome, string? Error = null);

public interface IEmailSender
{
    Task<EmailSendResult> SendAsync(EmailMessage message, CancellationToken ct);
}
