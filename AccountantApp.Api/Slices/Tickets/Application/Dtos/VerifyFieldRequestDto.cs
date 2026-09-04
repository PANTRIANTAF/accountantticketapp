namespace AccountantApp.Api.Slices.Tickets.Application.Dtos;

/// <summary>
/// Accepts or rejects one field value of the CURRENT revision. Accountants only.
///
/// It names a <c>FieldValueId</c> rather than a field key, because a verification attaches to the value
/// in a specific revision (plan §1.5) -- a key would be ambiguous the moment a correction is submitted.
/// </summary>
public class VerifyFieldRequestDto
{
    public Guid TicketId { get; set; }

    public int Version { get; set; }

    public Guid FieldValueId { get; set; }

    /// <summary>Accepted or Rejected. Anything else is 422.</summary>
    public string Outcome { get; set; } = string.Empty;

    /// <summary>
    /// Required on a rejection, and shown VERBATIM to the Customer side (§4.6 rule 2) -- so it is a
    /// user-facing sentence, not an internal code. A missing reason is 422 here rather than a 500 from
    /// ck_field_verifications_reason.
    /// </summary>
    public string? RejectionReason { get; set; }
}
