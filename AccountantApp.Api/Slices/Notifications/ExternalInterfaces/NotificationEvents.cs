namespace AccountantApp.Api.Slices.Notifications.ExternalInterfaces;

public static class NotificationEvents
{
    // --- Identity ---
    public const string Invited              = "Invited";
    public const string PasswordResetRequested = "PasswordResetRequested";
    public const string AccountSuspended     = "AccountSuspended";

    // --- Tickets: to the Customer side ---
    public const string TicketPickedUp       = "TicketPickedUp";
    public const string InformationRequested = "InformationRequested";   // → AwaitingInformation
    public const string FieldRejected        = "FieldRejected";
    public const string TicketAnswered       = "TicketAnswered";
    public const string TicketClosed         = "TicketClosed";
    public const string TicketCancelled      = "TicketCancelled";
    public const string AccountantResponded  = "AccountantResponded";

    // --- Tickets: to the Office ---
    public const string TicketSubmitted      = "TicketSubmitted";
    public const string CorrectionSubmitted  = "CorrectionSubmitted";
    public const string CustomerReplied      = "CustomerReplied";
    public const string TicketAssignedToYou  = "TicketAssignedToYou";
    public const string DueDateApproaching   = "DueDateApproaching";

    // --- Employees ---
    //
    // EmployeeInvited goes to the INVITEE and is emailed -- they have no session yet, so an in-app
    // notification would be one they could never read.
    //
    // The other two go to the Customer's own Admins and are in-app only, deliberately absent from
    // Emailed below. A Customer Admin who registers six people in an afternoon does not want six emails
    // about their own afternoon, and the events carry no token and nothing time-critical. EmployeeDeparted
    // is the one worth reading, because it is the record that somebody's access is gone.
    public const string EmployeeInvited      = "EmployeeInvited";
    public const string EmployeeRegistered   = "EmployeeRegistered";
    public const string EmployeeDeparted     = "EmployeeDeparted";

    /// <summary>All valid event kinds, built by reflection.</summary>
    public static readonly IReadOnlySet<string> All = new HashSet<string>(
        typeof(NotificationEvents)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.FieldType == typeof(string) && f.IsLiteral)
            .Select(f => (string)f.GetRawConstantValue()!)
        , StringComparer.Ordinal);

    /// <summary>Kinds that are also emailed. Everything else is in-app only.</summary>
    public static readonly IReadOnlySet<string> Emailed = new HashSet<string>(StringComparer.Ordinal)
    {
        Invited, PasswordResetRequested, InformationRequested, FieldRejected,
        TicketAnswered, TicketClosed, EmployeeInvited
    };
}
