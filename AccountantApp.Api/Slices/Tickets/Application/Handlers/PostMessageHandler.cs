using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Shared.Authorization;
using AccountantApp.Api.Shared.Data;
using AccountantApp.Api.Shared.Errors;
using AccountantApp.Api.Slices.Audit.ExternalInterfaces;
using AccountantApp.Api.Slices.Documents.ExternalInterfaces;
using AccountantApp.Api.Slices.Employees.ExternalInterfaces;
using AccountantApp.Api.Slices.Notifications.ExternalInterfaces;
using AccountantApp.Api.Slices.Tickets.Application.Dtos;
using AccountantApp.Api.Slices.Tickets.Core;
using AccountantApp.Api.Slices.Tickets.Infrastructure;

namespace AccountantApp.Api.Slices.Tickets.Application.Handlers;

/// <summary>
/// Plan §4.10. ONE handler, TWO endpoints and TWO ACTIONS -- and the two actions are why it is one class.
///
/// <c>PostMessage</c> is open to all four roles; <c>PostInternalNote</c> is Accountants only, enforced by
/// the CATALOGUE and not by a branch in here (rule 3). Matrix §6: internal notes are "the Office's private
/// channel, not the Admin's", so both Accountant roles have them and neither Customer-side role does. A
/// handler that decided this itself would be a second copy of the matrix; a catalogue entry is the copy
/// the permission tests already read.
///
/// THE KIND IS DERIVED FROM THE ROLE, NEVER FROM THE BODY (rule 1). If it came from the body a Customer
/// could post something that renders as an Accountant response, which is a forgery with the Office's name
/// on it. <c>SystemEvent</c> is not producible here at all (rule 4): only
/// <c>TicketTransitions.Apply</c> writes one, with a null author.
///
/// Append-only (rule 6). There is no edit handler, no delete handler and no <c>edited_at</c> column for
/// one to write to.
/// </summary>
public class PostMessageHandler
{
    private readonly TicketsDbContext _db;
    private readonly IPermissionChecker _permissions;
    private readonly IRequestTransaction _transaction;
    private readonly IAuditApi _audit;
    private readonly IEmployeeApi _employees;
    private readonly IDocumentApi _documents;
    private readonly INotificationApi _notifications;

    public PostMessageHandler(
        TicketsDbContext db,
        IPermissionChecker permissions,
        IRequestTransaction transaction,
        IAuditApi audit,
        IEmployeeApi employees,
        IDocumentApi documents,
        INotificationApi notifications)
    {
        _db = db;
        _permissions = permissions;
        _transaction = transaction;
        _audit = audit;
        _employees = employees;
        _documents = documents;
        _notifications = notifications;
    }

    /// <summary>The public channel: an AccountantResponse or a CustomerMessage, decided by the role.</summary>
    public async Task<MessagePostedDto> Handle(
        PostMessageRequestDto req, CurrentUser user, CancellationToken ct)
    {
        await _permissions.RequireAsync(user, "PostMessage", ct: ct);

        var kind = TicketVisibility.IsAccountant(user)
            ? TicketMessageKind.AccountantResponse
            : TicketMessageKind.CustomerMessage;

        return await PostAsync(req, user, kind, ct);
    }

    /// <summary>
    /// The Office's private channel. A separate action name, so a Customer-side caller is stopped by the
    /// permission check before this method is reached -- there is no role test below.
    /// </summary>
    public async Task<MessagePostedDto> HandleInternalNote(
        PostMessageRequestDto req, CurrentUser user, CancellationToken ct)
    {
        await _permissions.RequireAsync(user, "PostInternalNote", ct: ct);

        return await PostAsync(req, user, TicketMessageKind.InternalNote, ct);
    }

    private async Task<MessagePostedDto> PostAsync(
        PostMessageRequestDto req, CurrentUser user, string kind, CancellationToken ct)
    {
        var callerAccountId = TicketVisibility.RequireAccountId(user);
        var callerEmployeeId = await TicketAccess.ResolveCallerEmployeeIdAsync(_employees, user, ct);

        var ticket = await TicketAccess.LoadVisibleAsync(_db, user, callerEmployeeId, req.TicketId, ct);
        TicketConcurrency.RequireVersion(ticket, req.Version);

        // Rule 8. A Closed ticket is read-only, and so is a Cancelled one -- while their documents stay
        // downloadable (§4.11 rule 2), which is why the terminal guard lives on the paths that WRITE and
        // not in the visibility filter.
        TicketAccess.RequireNotTerminal(ticket);

        var body = req.Body?.Trim();
        if (string.IsNullOrWhiteSpace(body))
            // ck_ticket_messages_body would reject it too; a 422 with a sentence is better than a 500.
            throw new AppException("A message needs some text.", 422);

        var attachments = await ResolveAttachmentsAsync(ticket, req.AttachedDocumentIds, ct);

        await using var scope = await _transaction.BeginAsync(_db, ct);

        var now = DateTimeOffset.UtcNow;

        var message = new TicketMessage
        {
            Id = Guid.NewGuid(),
            TicketId = ticket.Id,

            // Non-null for every kind this handler can produce, which is what
            // ck_ticket_messages_author's second branch requires.
            AuthorUserAccountId = callerAccountId,
            Kind = kind,
            Body = body,
            CreatedAt = now,
        };

        _db.TicketMessages.Add(message);
        ticket.Messages.Add(message);

        foreach (var documentId in attachments)
            _db.TicketMessageDocuments.Add(new TicketMessageDocument
            {
                TicketMessageId = message.Id,
                DocumentId = documentId,
            });

        // JUDGMENT CALL, reported: a message Touches the ticket. LastActivityAt is what every list sorts
        // by, and a conversation that does not count as activity means a ticket with ten unanswered
        // customer replies sinks to the bottom of the Office's list.
        TicketConcurrency.Touch(ticket, now);

        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(new AuditEntry(
            AuditActions.MessagePosted,
            AuditTargets.Ticket,
            ticket.Id.ToString(),
            ticket.CustomerId,
            After: new
            {
                MessageId = message.Id,
                message.Kind,
                AttachedDocumentIds = attachments,

                // The body is deliberately NOT copied into the audit entry. It is already stored,
                // immutably, in a table nothing deletes from -- and an internal note duplicated into the
                // audit log is the Office's private channel written a second time somewhere its
                // visibility rules do not apply.
            }), ct);

        await NotifyAsync(ticket, kind, ct);

        await _transaction.CommitAsync(ct);

        return new MessagePostedDto
        {
            TicketId = ticket.Id,
            MessageId = message.Id,
            Kind = message.Kind,
            CreatedAt = message.CreatedAt,
            Ticket = TicketMapper.ToState(ticket),
        };
    }

    /// <summary>
    /// Rule 7, and §0.3 step 5 for the third time: every attachment must ALREADY belong to this ticket.
    ///
    /// The ids are checked against the ticket's own live document list, so a document from a ticket the
    /// caller cannot read is a 404 -- with the same message as an id that does not exist at all, because
    /// distinguishing the two enumerates other Customers' document ids. Every test of "attaching my own
    /// document works" passes without this check; that is exactly why it is written out here.
    /// </summary>
    private async Task<List<Guid>> ResolveAttachmentsAsync(
        Ticket ticket, List<Guid>? requested, CancellationToken ct)
    {
        var ids = (requested ?? []).Distinct().ToList();
        if (ids.Count == 0)
            return [];

        var live = (await _documents.ListByTicketAsync(ticket.Id, ct))
            .Select(document => document.Id)
            .ToHashSet();

        if (ids.Any(id => !live.Contains(id)))
            throw new AppException("One of the attached documents was not found on this ticket.", 404);

        return ids;
    }

    /// <summary>
    /// Rule 5, in one place because the three cases are one decision:
    ///
    ///   - <c>InternalNote</c> notifies NOBODY on the Customer side. §4.0 I. A notification is a message
    ///     the recipient can read, so notifying a Customer about an internal note leaks it through the
    ///     back door regardless of how carefully the conversation is filtered.
    ///   - <c>CustomerMessage</c> notifies the ASSIGNEE (<c>CustomerReplied</c>) -- the person handling
    ///     the ticket, not the whole Office. An unassigned ticket has nobody to tell, and the ticket is
    ///     already in the pickup queue.
    ///   - <c>AccountantResponse</c> notifies the Customer side (<c>AccountantResponded</c>).
    /// </summary>
    private async Task NotifyAsync(Ticket ticket, string kind, CancellationToken ct)
    {
        switch (kind)
        {
            case TicketMessageKind.InternalNote:
                return;

            case TicketMessageKind.CustomerMessage:
                if (ticket.AssigneeUserAccountId is { } assignee)
                    await _notifications.NotifyAsync(new NotificationRequest(
                        assignee.ToString(),
                        NotificationEvents.CustomerReplied,
                        $"New message on {ticket.Reference}",
                        $"There is a new message on {ticket.Title}.",
                        ticket.Id), ct);

                return;

            case TicketMessageKind.AccountantResponse:
                await TicketAccess.NotifyCustomerSideAsync(
                    _notifications,
                    _employees,
                    ticket,
                    NotificationEvents.AccountantResponded,
                    $"New message on {ticket.Reference}",
                    $"The Office has replied on {ticket.Title}.",
                    ct);

                return;
        }
    }
}
