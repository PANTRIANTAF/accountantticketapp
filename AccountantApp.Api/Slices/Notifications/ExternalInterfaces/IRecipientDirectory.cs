namespace AccountantApp.Api.Slices.Notifications.ExternalInterfaces;

public sealed record Recipient(string UserAccountId, string Email, string DisplayName, bool IsActive);

public interface IRecipientDirectory
{
    Task<Recipient?> FindAsync(string userAccountId, CancellationToken ct);
}
