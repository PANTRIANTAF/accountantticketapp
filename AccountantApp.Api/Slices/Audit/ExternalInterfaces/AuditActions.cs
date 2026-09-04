using System.Reflection;

namespace AccountantApp.Api.Slices.Audit.ExternalInterfaces;

public static class AuditActions
{
    public const string LoginSucceeded = "LoginSucceeded";
    public const string LoginFailed = "LoginFailed";
    public const string LoggedOut = "LoggedOut";
    public const string AccountLockedOut = "AccountLockedOut";
    public const string PasswordResetRequested = "PasswordResetRequested";
    public const string PasswordResetCompleted = "PasswordResetCompleted";
    public const string PasswordChanged = "PasswordChanged";
    public const string AccountInvited = "AccountInvited";
    public const string InvitationAccepted = "InvitationAccepted";
    public const string AccountSuspended = "AccountSuspended";
    public const string AccountReactivated = "AccountReactivated";
    public const string AccountantAccountCreated = "AccountantAccountCreated";
    public const string AccountantPromoted = "AccountantPromoted";
    public const string AccountantDemoted = "AccountantDemoted";
    public const string CustomerCreated = "CustomerCreated";
    public const string CustomerUpdated = "CustomerUpdated";
    public const string CustomerSuspended = "CustomerSuspended";
    public const string CustomerReactivated = "CustomerReactivated";
    public const string EmployeeRegistered = "EmployeeRegistered";
    public const string EmployeeEdited = "EmployeeEdited";
    public const string EmployeeDeparted = "EmployeeDeparted";
    public const string EmployeeInvited = "EmployeeInvited";

    // Added for Employees' set-role endpoint. Deliberately NOT a reuse of EmployeeEdited: a role
    // change and a phone-number change must be distinguishable, or the log cannot answer "who made
    // this person an administrator" -- the one question it will actually be asked about roles.
    public const string EmployeeRoleChanged = "EmployeeRoleChanged";

    // Reversing a departure. NOT a reuse of EmployeeEdited or a second EmployeeDeparted entry with a
    // different snapshot: "this person's departure was undone, by whom, and when" is a question the log
    // will be asked precisely because the operation exists to correct a mistake.
    public const string EmployeeReinstated = "EmployeeReinstated";

    // Changing the address an account signs in with. Distinct from EmployeeEdited, which covers the WORK
    // email -- the two are different fields with different consequences, and a log that cannot tell them
    // apart cannot answer "who changed the address this person authenticates with".
    public const string LoginEmailChanged = "LoginEmailChanged";
    public const string TicketTypeCreated = "TicketTypeCreated";
    public const string TicketTypeVersionCreated = "TicketTypeVersionCreated";
    public const string TicketTypeActivated = "TicketTypeActivated";
    public const string TicketTypeDeactivated = "TicketTypeDeactivated";
    public const string TicketCreated = "TicketCreated";
    public const string TicketStatusChanged = "TicketStatusChanged";
    public const string TicketAssigned = "TicketAssigned";
    public const string TicketReassigned = "TicketReassigned";
    public const string TicketCancelled = "TicketCancelled";
    public const string TicketClosed = "TicketClosed";
    public const string RevisionSubmitted = "RevisionSubmitted";
    public const string FieldVerified = "FieldVerified";
    public const string FieldRejected = "FieldRejected";
    public const string MessagePosted = "MessagePosted";
    public const string PriorityChanged = "PriorityChanged";
    public const string DueDateChanged = "DueDateChanged";
    public const string DocumentUploaded = "DocumentUploaded";
    public const string DocumentDownloaded = "DocumentDownloaded";
    public const string DocumentSoftDeleted = "DocumentSoftDeleted";
    public const string PermissionDenied = "PermissionDenied";

    public static readonly IReadOnlySet<string> All = typeof(AuditActions)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.IsLiteral && field.FieldType == typeof(string))
        .Select(field => (string)field.GetRawConstantValue()!)
        .ToHashSet(StringComparer.Ordinal);
}

public static class AuditTargets
{
    public const string UserAccount = "UserAccount";
    public const string Customer = "Customer";
    public const string Employee = "Employee";
    public const string TicketType = "TicketType";
    public const string Ticket = "Ticket";
    public const string Document = "Document";
    public const string Notification = "Notification";
    public const string None = "None";

    public static readonly IReadOnlySet<string> All = typeof(AuditTargets)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.IsLiteral && field.FieldType == typeof(string))
        .Select(field => (string)field.GetRawConstantValue()!)
        .ToHashSet(StringComparer.Ordinal);
}