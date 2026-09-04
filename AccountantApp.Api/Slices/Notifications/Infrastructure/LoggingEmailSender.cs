using AccountantApp.Api.Slices.Notifications.Application;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AccountantApp.Api.Slices.Notifications.Infrastructure;

internal sealed class LoggingEmailSender : IEmailSender
{
    private readonly ILogger<LoggingEmailSender> _logger;
    private readonly IHostEnvironment _env;

    public LoggingEmailSender(ILogger<LoggingEmailSender> logger, IHostEnvironment env)
    {
        _logger = logger;
        _env = env;
    }

    public Task<EmailSendResult> SendAsync(EmailMessage message, CancellationToken ct)
    {
        if (_env.IsDevelopment())
        {
            _logger.LogInformation(
                "EMAIL (not sent — no transport configured) to {To}: {Subject}",
                message.To, message.Subject);
            // Never log message.Body; it may have come from a secret EmailBody on the outbox.
        }

        return Task.FromResult(new EmailSendResult(EmailSendOutcome.Sent));
    }
}
