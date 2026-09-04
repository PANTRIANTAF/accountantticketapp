using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Shared.Authorization;

namespace AccountantApp.Api.Slices.Tickets;

/// <summary>
/// Plan §7.2. Twenty-two actions -- the eighteen ticket actions plus the four document actions this slice
/// authorizes on behalf of <c>Documents</c> (§0.3).
///
/// NOT ONE ROW HERE IS <c>AccountantAdmin</c>-ONLY, and if one ever becomes so it is wrong. Matrix §7:
/// "Serving tickets is fully open to both Accountant roles. Verifying, responding, assigning, and closing
/// are all available to an Accountant User. This is the core of what the role exists for." And §9.9:
/// restricting reassignment to <c>AccountantAdmin</c> would create a fifth Admin-only power and contradict
/// the locked "exactly four powers" list.
///
/// EVERY CUSTOMER-SIDE "yes, but only…" QUALIFIER IS MISSING FROM THIS TABLE ON PURPOSE (rule 4). This
/// file says who may CALL; §0.4's visibility filters and the per-handler checks say which ROWS. A reviewer
/// reading only this file would conclude an <c>Employee</c> may cancel any ticket -- <c>CancelTicketHandler</c>
/// (§4.12 rule 1) is what stops them, by requiring Creator and a status in {Draft, Submitted}.
///
/// The names are matched byte for byte against the string literals in the handlers by
/// <c>EndpointRoutingTests</c>, in both directions: an action a handler requires and this file omits is a
/// 403 for everybody that reads exactly like a deliberate decision, and an action here that no handler
/// requires is dead configuration that a permissions review reads as a granted power.
/// </summary>
public sealed class TicketsActionCatalogue : IActionCatalogue
{
    public string SliceName => "Tickets";

    public IReadOnlyDictionary<string, UserRole[]> Actions { get; } =
        new Dictionary<string, UserRole[]>(StringComparer.Ordinal)
        {
            ["CreateTicket"] = [UserRole.AccountantAdmin, UserRole.AccountantUser,
                UserRole.CustomerAdmin, UserRole.Employee],
            ["SubmitTicket"] = [UserRole.AccountantAdmin, UserRole.AccountantUser,
                UserRole.CustomerAdmin, UserRole.Employee],
            ["ListTickets"] = [UserRole.AccountantAdmin, UserRole.AccountantUser,
                UserRole.CustomerAdmin, UserRole.Employee],

            // "ViewTicket", not "View" (rule 5): action names are GLOBALLY unique across every slice's
            // catalogue, and the composer fails startup on a duplicate.
            ["ViewTicket"] = [UserRole.AccountantAdmin, UserRole.AccountantUser,
                UserRole.CustomerAdmin, UserRole.Employee],
            ["ListPickupQueue"] = [UserRole.AccountantAdmin, UserRole.AccountantUser],
            ["SubmitRevision"] = [UserRole.AccountantAdmin, UserRole.AccountantUser,
                UserRole.CustomerAdmin, UserRole.Employee],
            ["VerifyField"] = [UserRole.AccountantAdmin, UserRole.AccountantUser],
            ["SetTicketPriority"] = [UserRole.AccountantAdmin, UserRole.AccountantUser],
            ["SetTicketDueDate"] = [UserRole.AccountantAdmin, UserRole.AccountantUser],
            ["PickupTicket"] = [UserRole.AccountantAdmin, UserRole.AccountantUser],
            ["AssignTicket"] = [UserRole.AccountantAdmin, UserRole.AccountantUser],
            ["AnswerTicket"] = [UserRole.AccountantAdmin, UserRole.AccountantUser],
            ["CloseTicket"] = [UserRole.AccountantAdmin, UserRole.AccountantUser],
            ["RequestInformation"] = [UserRole.AccountantAdmin, UserRole.AccountantUser],

            // The catalogue's spelling of the action, which differs from the handler's class name
            // (ReturnToReviewHandler) because §7.2 and §4.9 name them differently. THIS one is the string
            // the handler passes to RequireAsync.
            ["ReturnTicketToReview"] = [UserRole.AccountantAdmin, UserRole.AccountantUser],
            ["PostMessage"] = [UserRole.AccountantAdmin, UserRole.AccountantUser,
                UserRole.CustomerAdmin, UserRole.Employee],

            // A SEPARATE ACTION from PostMessage (rule 2), served by the same handler class through a second
            // entry point. This row is what denies an internal note to a Customer-side caller -- the handler
            // does not branch on the role, so deleting this line does not deny the operation, it grants it to
            // nobody. Matrix §6: internal notes are "the Office's private channel, not the Admin's", so both
            // Accountant roles have them and neither Customer-side role does.
            ["PostInternalNote"] = [UserRole.AccountantAdmin, UserRole.AccountantUser],
            ["CancelTicket"] = [UserRole.AccountantAdmin, UserRole.AccountantUser,
                UserRole.CustomerAdmin, UserRole.Employee],

            // ── The four document actions. Registered by THIS slice, per the Documents plan §0.2, and they
            // live here and NOWHERE ELSE (rule 3): a DocumentsActionCatalogue declaring them as well is a
            // startup failure naming both slices. All four are open to all four roles, and every "but only
            // their own" qualifier is enforced by the handlers -- DeleteDocumentHandler's two halves being
            // the sharpest example. ──
            ["UploadDocument"] = [UserRole.AccountantAdmin, UserRole.AccountantUser,
                UserRole.CustomerAdmin, UserRole.Employee],
            ["ListTicketDocuments"] = [UserRole.AccountantAdmin, UserRole.AccountantUser,
                UserRole.CustomerAdmin, UserRole.Employee],
            ["DownloadDocument"] = [UserRole.AccountantAdmin, UserRole.AccountantUser,
                UserRole.CustomerAdmin, UserRole.Employee],
            ["DeleteDocument"] = [UserRole.AccountantAdmin, UserRole.AccountantUser,
                UserRole.CustomerAdmin, UserRole.Employee],
        };
}
