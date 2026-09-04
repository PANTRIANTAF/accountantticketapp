namespace AccountantApp.Api.Slices.Tickets.Core;

/// <summary>
/// An Accountant's judgement on one Field Value. 01-DomainModel.md section 3.
///
/// It attaches to a FieldValue in a SPECIFIC revision, so the verification history of a corrected
/// field is fully preserved. That is also why carrying an accepted value forward into a new revision
/// is not automatic: the new revision has NEW FieldValue rows, so the acceptance must be copied
/// forward as a new row pointing at the new value, PRESERVING the original verifier and timestamp.
///
/// APPEND-ONLY. A re-verification appends a row; the latest by VerifiedAt (tie-broken by Id) is
/// current. Never update an existing row -- the history is the point. No Version property (9.7).
/// </summary>
public sealed class FieldVerification
{
    public Guid Id { get; set; }
    public Guid FieldValueId { get; set; }

    /// <summary>Accepted or Rejected. See <see cref="VerificationOutcome"/>.</summary>
    public string Outcome { get; set; } = string.Empty;

    /// <summary>
    /// Required when rejected, forbidden when accepted, and shown VERBATIM to the Customer side --
    /// so it is a user-facing string, never an internal code. ck_field_verifications_reason is the
    /// database backstop, including the whitespace-only case; a handler should return 422 with a real
    /// message rather than let the constraint produce a 500.
    /// </summary>
    public string? RejectionReason { get; set; }

    public Guid VerifiedByUserAccountId { get; set; }
    public DateTimeOffset VerifiedAt { get; set; }

    public bool IsAccepted => Outcome == VerificationOutcome.Accepted;
    public bool IsRejected => Outcome == VerificationOutcome.Rejected;
}

public static class VerificationOutcome
{
    public const string Accepted = "Accepted";
    public const string Rejected = "Rejected";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
        { Accepted, Rejected };
}
