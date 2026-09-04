using AccountantApp.Api.Shared.Errors;
using AccountantApp.Api.Slices.Audit.ExternalInterfaces;
using AccountantApp.Api.Slices.Identity.Application;
using AccountantApp.Api.Slices.Identity.Application.Dtos;
using AccountantApp.Api.Slices.Identity.Application.Handlers;
using AccountantApp.Api.Slices.Identity.Core;
using AccountantApp.Api.Slices.Identity.Infrastructure;
using AccountantApp.Api.Slices.Notifications.ExternalInterfaces;
using Microsoft.EntityFrameworkCore;

namespace AccountantApp.Tests.Identity;

public class TokenFlowTests
{
    private const string NewPassword = "a-brand-new-password";

    private static UserAccountToken AddToken(
        IdentityDbContext db, Guid accountId, string purpose, string rawToken,
        DateTimeOffset? expiresAt = null, DateTimeOffset? consumedAt = null)
    {
        var token = new UserAccountToken
        {
            Id = Guid.NewGuid(),
            UserAccountId = accountId,
            Purpose = purpose,
            TokenHash = IdentityTestHarness.Tokens.HashToken(rawToken),
            ExpiresAt = expiresAt ?? DateTimeOffset.UtcNow.AddHours(1),
            ConsumedAt = consumedAt,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.Tokens.Add(token);
        return token;
    }

    // --- Request a reset ---

    [Fact]
    public async Task Requesting_a_reset_for_an_unknown_address_still_returns_200()
    {
        await using var db = IdentityTestHarness.NewDb();
        var audit = new RecordingAuditApi();
        var transaction = new CountingRequestTransaction();
        var notifications = new RecordingNotificationApi();
        var handler = new RequestPasswordResetHandler(
            db, IdentityTestHarness.Tokens, IdentityTestHarness.Links, notifications, transaction, audit);

        var result = await handler.Handle(
            new RequestPasswordResetRequestDto { Email = "nobody@example.com" }, default);

        // 200, not 404. A 404 here makes the endpoint a free tool for testing which of an organisation's
        // addresses are registered.
        Assert.True(result.Success);
        Assert.Empty(notifications.Requests);

        // The audit entry must survive: a targeted enumeration sweep that leaves no trace is exactly what
        // this log is for, so the handler commits before returning.
        Assert.Equal(1, transaction.Commits);
        Assert.Single(audit.WithAction(AuditActions.PasswordResetRequested));
    }

    [Fact]
    public async Task A_malformed_address_also_returns_200_rather_than_422()
    {
        await using var db = IdentityTestHarness.NewDb();
        var handler = new RequestPasswordResetHandler(
            db, IdentityTestHarness.Tokens, IdentityTestHarness.Links,
            new RecordingNotificationApi(), new CountingRequestTransaction(), new RecordingAuditApi());

        // A 422 for a malformed address next to a 200 for a well-formed unknown one is the same oracle,
        // just quieter. EmailNormalization.Require must NOT be called on this path.
        var result = await handler.Handle(
            new RequestPasswordResetRequestDto { Email = "not-an-email-at-all" }, default);

        Assert.True(result.Success);
    }

    [Fact]
    public async Task Requesting_a_reset_invalidates_outstanding_reset_tokens_but_not_invitations()
    {
        await using var db = IdentityTestHarness.NewDb();
        var account = IdentityTestHarness.NewAccount();
        db.UserAccounts.Add(account);
        var firstReset = AddToken(db, account.Id, TokenPurpose.PasswordReset, "old-reset-token");
        var invitation = AddToken(db, account.Id, TokenPurpose.Invitation, "pending-invitation-token");
        await db.SaveChangesAsync();

        var notifications = new RecordingNotificationApi();
        var handler = new RequestPasswordResetHandler(
            db, IdentityTestHarness.Tokens, IdentityTestHarness.Links,
            notifications, new CountingRequestTransaction(), new RecordingAuditApi());

        await handler.Handle(new RequestPasswordResetRequestDto { Email = account.LoginEmail }, default);

        // Asking three times must not leave three working tokens for an hour each -- the window an
        // attacker can hunt in would be as wide as the user is impatient.
        Assert.NotNull(firstReset.ConsumedAt);

        // The invitation is untouched: a different flow, and consuming it here would leave a
        // half-onboarded account nobody can finish setting up.
        Assert.Null(invitation.ConsumedAt);

        Assert.Equal(2, await db.Tokens.CountAsync(token => token.Purpose == TokenPurpose.PasswordReset));
    }

    [Fact]
    public async Task The_raw_reset_token_appears_only_in_the_email_body()
    {
        await using var db = IdentityTestHarness.NewDb();
        var account = IdentityTestHarness.NewAccount();
        db.UserAccounts.Add(account);
        await db.SaveChangesAsync();

        var notifications = new RecordingNotificationApi();
        var audit = new RecordingAuditApi();
        var handler = new RequestPasswordResetHandler(
            db, IdentityTestHarness.Tokens, IdentityTestHarness.Links,
            notifications, new CountingRequestTransaction(), audit);

        await handler.Handle(new RequestPasswordResetRequestDto { Email = account.LoginEmail }, default);

        var notification = Assert.Single(notifications.Requests);
        Assert.Equal(NotificationEvents.PasswordResetRequested, notification.EventKind);
        Assert.NotNull(notification.EmailBody);
        Assert.Contains("https://app.test/reset-password?token=", notification.EmailBody);

        // Body is what is STORED on the notification row and rendered in the app. A raw token there
        // defeats the whole hash-only design: anyone who can read the table could reset anyone's password.
        Assert.DoesNotContain("token=", notification.Body);

        // And the stored hash is not the raw token.
        var stored = await db.Tokens.SingleAsync();
        Assert.Equal(64, stored.TokenHash.Length);
        Assert.DoesNotContain(stored.TokenHash, notification.EmailBody);
    }

    [Fact]
    public async Task A_suspended_account_gets_the_same_200_and_no_email()
    {
        await using var db = IdentityTestHarness.NewDb();
        db.UserAccounts.Add(IdentityTestHarness.NewAccount(status: AccountStatus.Suspended));
        await db.SaveChangesAsync();

        var notifications = new RecordingNotificationApi();
        var handler = new RequestPasswordResetHandler(
            db, IdentityTestHarness.Tokens, IdentityTestHarness.Links,
            notifications, new CountingRequestTransaction(), new RecordingAuditApi());

        var result = await handler.Handle(
            new RequestPasswordResetRequestDto { Email = "alice@example.com" }, default);

        Assert.True(result.Success);
        Assert.Empty(notifications.Requests);
        Assert.Equal(0, await db.Tokens.CountAsync());
    }

    // --- Complete a reset ---

    [Fact]
    public async Task Completing_a_reset_sets_the_password_consumes_the_token_and_clears_the_lockout()
    {
        await using var db = IdentityTestHarness.NewDb();
        var account = IdentityTestHarness.NewAccount(mustChangePassword: true);
        account.FailedLoginCount = 4;
        account.LockoutExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10);
        db.UserAccounts.Add(account);
        var token = AddToken(db, account.Id, TokenPurpose.PasswordReset, "the-raw-token");
        await db.SaveChangesAsync();

        var audit = new RecordingAuditApi();
        var handler = new CompletePasswordResetHandler(
            db, IdentityTestHarness.Tokens, IdentityTestHarness.Passwords,
            new CountingRequestTransaction(), audit);

        await handler.Handle(new CompletePasswordResetRequestDto
        {
            Token = "the-raw-token",
            NewPassword = NewPassword
        }, default);

        Assert.Equal(PasswordVerification.Success,
            IdentityTestHarness.Passwords.Verify(account.PasswordHash, NewPassword));
        Assert.NotNull(token.ConsumedAt);

        // Someone locked out by an attacker guessing at their account has now proven control of the
        // mailbox. Leaving the lockout would make the reset appear to succeed and the next login still
        // fail for fifteen minutes, with nothing to say why.
        Assert.Null(account.LockoutExpiresAt);
        Assert.Equal(0, account.FailedLoginCount);

        // They chose this password themselves, so do not ask them to change it again.
        Assert.False(account.MustChangePassword);
        Assert.Single(audit.WithAction(AuditActions.PasswordResetCompleted));
    }

    [Fact]
    public async Task A_reset_token_is_single_use()
    {
        await using var db = IdentityTestHarness.NewDb();
        var account = IdentityTestHarness.NewAccount();
        db.UserAccounts.Add(account);
        AddToken(db, account.Id, TokenPurpose.PasswordReset, "the-raw-token");
        await db.SaveChangesAsync();

        var handler = new CompletePasswordResetHandler(
            db, IdentityTestHarness.Tokens, IdentityTestHarness.Passwords,
            new CountingRequestTransaction(), new RecordingAuditApi());

        var request = new CompletePasswordResetRequestDto
        {
            Token = "the-raw-token",
            NewPassword = NewPassword
        };
        await handler.Handle(request, default);

        var exception = await Assert.ThrowsAsync<AppException>(() => handler.Handle(
            new CompletePasswordResetRequestDto { Token = "the-raw-token", NewPassword = "another-password-x" },
            default));
        Assert.Equal(400, exception.StatusCode);
    }

    [Fact]
    public async Task An_invitation_token_cannot_complete_a_password_reset()
    {
        await using var db = IdentityTestHarness.NewDb();
        var account = IdentityTestHarness.NewAccount();
        db.UserAccounts.Add(account);
        AddToken(db, account.Id, TokenPurpose.Invitation, "an-invitation-token");
        await db.SaveChangesAsync();

        var handler = new CompletePasswordResetHandler(
            db, IdentityTestHarness.Tokens, IdentityTestHarness.Passwords,
            new CountingRequestTransaction(), new RecordingAuditApi());

        // Purpose is part of the WHERE clause. Both purposes share one table and one unique hash index,
        // so without that filter a valid invitation token would complete a reset -- skipping the
        // email-confirmation step the invitation flow exists to perform.
        await Assert.ThrowsAsync<AppException>(() => handler.Handle(
            new CompletePasswordResetRequestDto { Token = "an-invitation-token", NewPassword = NewPassword },
            default));
    }

    [Fact]
    public async Task Expired_consumed_unknown_and_wrong_purpose_tokens_all_give_the_same_400()
    {
        var messages = new List<string>();
        var statuses = new List<int>();

        foreach (var rawToken in new[] { "unknown", "expired", "consumed", "wrong-purpose" })
        {
            await using var db = IdentityTestHarness.NewDb();
            var account = IdentityTestHarness.NewAccount();
            db.UserAccounts.Add(account);

            switch (rawToken)
            {
                case "expired":
                    AddToken(db, account.Id, TokenPurpose.PasswordReset, rawToken,
                        expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1));
                    break;
                case "consumed":
                    AddToken(db, account.Id, TokenPurpose.PasswordReset, rawToken,
                        consumedAt: DateTimeOffset.UtcNow.AddMinutes(-5));
                    break;
                case "wrong-purpose":
                    AddToken(db, account.Id, TokenPurpose.Invitation, rawToken);
                    break;
            }

            await db.SaveChangesAsync();

            var handler = new CompletePasswordResetHandler(
                db, IdentityTestHarness.Tokens, IdentityTestHarness.Passwords,
                new CountingRequestTransaction(), new RecordingAuditApi());

            var exception = await Assert.ThrowsAsync<AppException>(() => handler.Handle(
                new CompletePasswordResetRequestDto { Token = rawToken, NewPassword = NewPassword },
                default));

            messages.Add(exception.Message);
            statuses.Add(exception.StatusCode);
        }

        // "This token has expired" versus "no such token" confirms a token existed, which confirms an
        // account exists at whatever address the caller guessed.
        Assert.Single(messages.Distinct());
        Assert.Single(statuses.Distinct());
        Assert.Equal(400, statuses[0]);
    }

    // --- Accept an invitation ---

    [Fact]
    public async Task Accepting_an_invitation_activates_the_account_and_confirms_the_email()
    {
        await using var db = IdentityTestHarness.NewDb();
        var account = IdentityTestHarness.NewAccount(status: AccountStatus.Invited, password: null);
        db.UserAccounts.Add(account);
        var token = AddToken(db, account.Id, TokenPurpose.Invitation, "invite-me",
            expiresAt: DateTimeOffset.UtcNow.Add(TokenPurpose.InvitationLifetime));
        await db.SaveChangesAsync();

        var audit = new RecordingAuditApi();
        var handler = new AcceptInvitationHandler(
            db, IdentityTestHarness.Tokens, IdentityTestHarness.Passwords,
            new CountingRequestTransaction(), audit);

        await handler.Handle(new AcceptInvitationRequestDto
        {
            Token = "invite-me",
            NewPassword = NewPassword,
            DisplayName = "  Alice Actual  "
        }, default);

        Assert.Equal(AccountStatus.Active, account.Status);

        // Redeeming a token that only ever existed in an email to that address IS the confirmation. There
        // is no separate confirm-your-email step.
        Assert.NotNull(account.EmailConfirmedAt);

        // False: the person chose this password themselves.
        Assert.False(account.MustChangePassword);

        Assert.Equal("Alice Actual", account.DisplayName);
        Assert.NotNull(token.ConsumedAt);
        Assert.Single(audit.WithAction(AuditActions.InvitationAccepted));
    }

    [Fact]
    public async Task An_absent_or_blank_display_name_keeps_the_one_the_inviter_typed()
    {
        foreach (var supplied in new[] { null, "", "   " })
        {
            await using var db = IdentityTestHarness.NewDb();
            var account = IdentityTestHarness.NewAccount(status: AccountStatus.Invited, password: null);
            db.UserAccounts.Add(account);
            AddToken(db, account.Id, TokenPurpose.Invitation, "invite-me");
            await db.SaveChangesAsync();

            var handler = new AcceptInvitationHandler(
                db, IdentityTestHarness.Tokens, IdentityTestHarness.Passwords,
                new CountingRequestTransaction(), new RecordingAuditApi());

            await handler.Handle(new AcceptInvitationRequestDto
            {
                Token = "invite-me",
                NewPassword = NewPassword,
                DisplayName = supplied
            }, default);

            // Blank is "keep what is there", NOT "blank the name". A nameless account shows as empty space
            // in every assignment dropdown and message header in the system.
            Assert.Equal("Alice Example", account.DisplayName);
        }
    }

    [Fact]
    public async Task An_already_active_account_cannot_replay_its_invitation()
    {
        await using var db = IdentityTestHarness.NewDb();
        var account = IdentityTestHarness.NewAccount(status: AccountStatus.Active);
        var originalHash = account.PasswordHash;
        db.UserAccounts.Add(account);
        AddToken(db, account.Id, TokenPurpose.Invitation, "invite-me");
        await db.SaveChangesAsync();

        var handler = new AcceptInvitationHandler(
            db, IdentityTestHarness.Tokens, IdentityTestHarness.Passwords,
            new CountingRequestTransaction(), new RecordingAuditApi());

        // Accepting again would reset a working account's password using a link from an old email.
        await Assert.ThrowsAsync<AppException>(() => handler.Handle(new AcceptInvitationRequestDto
        {
            Token = "invite-me",
            NewPassword = NewPassword
        }, default));

        Assert.Equal(originalHash, account.PasswordHash);
    }

    [Fact]
    public async Task A_password_reset_token_cannot_accept_an_invitation()
    {
        await using var db = IdentityTestHarness.NewDb();
        var account = IdentityTestHarness.NewAccount(status: AccountStatus.Invited, password: null);
        db.UserAccounts.Add(account);
        AddToken(db, account.Id, TokenPurpose.PasswordReset, "a-reset-token");
        await db.SaveChangesAsync();

        var handler = new AcceptInvitationHandler(
            db, IdentityTestHarness.Tokens, IdentityTestHarness.Passwords,
            new CountingRequestTransaction(), new RecordingAuditApi());

        // The mirror of the reset-side check. A reset token here would activate the account and confirm
        // the address without either having been proven by the invitation.
        await Assert.ThrowsAsync<AppException>(() => handler.Handle(new AcceptInvitationRequestDto
        {
            Token = "a-reset-token",
            NewPassword = NewPassword
        }, default));

        Assert.Equal(AccountStatus.Invited, account.Status);
    }

    [Fact]
    public async Task A_too_short_password_is_refused_before_the_token_is_consumed()
    {
        await using var db = IdentityTestHarness.NewDb();
        var account = IdentityTestHarness.NewAccount(status: AccountStatus.Invited, password: null);
        db.UserAccounts.Add(account);
        var token = AddToken(db, account.Id, TokenPurpose.Invitation, "invite-me");
        await db.SaveChangesAsync();

        var handler = new AcceptInvitationHandler(
            db, IdentityTestHarness.Tokens, IdentityTestHarness.Passwords,
            new CountingRequestTransaction(), new RecordingAuditApi());

        var exception = await Assert.ThrowsAsync<AppException>(() => handler.Handle(
            new AcceptInvitationRequestDto { Token = "invite-me", NewPassword = "short" }, default));

        Assert.Equal(422, exception.StatusCode);

        // The token must survive a rejected password, or one typo burns the invitation and the person needs
        // a whole new one.
        Assert.Null(token.ConsumedAt);
        Assert.Equal(AccountStatus.Invited, account.Status);
    }

    // --- Token issuing ---

    [Fact]
    public void Raw_tokens_are_url_safe_unpredictable_and_hashed_to_lowercase_hex()
    {
        var first = IdentityTestHarness.Tokens.GenerateRawToken();
        var second = IdentityTestHarness.Tokens.GenerateRawToken();

        Assert.NotEqual(first, second);
        Assert.Equal(43, first.Length);   // 32 bytes of CSPRNG output in Base64Url

        // Base64Url, because '+', '/' and '=' survive some URL handling and not others -- the failure is
        // an occasional invalid token nobody can reproduce.
        Assert.DoesNotContain('+', first);
        Assert.DoesNotContain('/', first);
        Assert.DoesNotContain('=', first);

        var hash = IdentityTestHarness.Tokens.HashToken(first);
        Assert.Equal(64, hash.Length);
        Assert.Equal(hash.ToLowerInvariant(), hash);

        // Deterministic: the lookup is a single indexed equality on token_hash.
        Assert.Equal(hash, IdentityTestHarness.Tokens.HashToken(first));
        Assert.NotEqual(hash, IdentityTestHarness.Tokens.HashToken(second));
    }
}
