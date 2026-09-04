namespace AccountantApp.Api.Shared.Validation;

/// <summary>
/// The one match timeout every <see cref="System.Text.RegularExpressions.Regex"/> built from a
/// stored, user-authored pattern must carry.
///
/// WHY THIS IS IN Shared/ AND NOT IN A SLICE. Two slices need it. TicketTypes compiles a pattern when
/// an Accountant authors a field descriptor, to reject one that is syntactically invalid while the
/// author is still there to be told. Tickets runs that same pattern against a value a Customer
/// submitted. The budget has to be identical in both places: with two different timeouts there exists
/// a pattern that sits between them, accepted under the generous one and dying under the strict one,
/// and the failure surfaces in the slice that did not author it.
///
/// It lived on <c>TicketTypes.Application.TicketTypeMapper</c> and was reached from
/// <c>Tickets.Application.FieldValueValidation</c> as
/// <c>TicketTypes.Application.TicketTypeMapper.RegexMatchTimeout</c>. That compiled — the field is
/// <c>internal</c> and there is one assembly — but it is a dependency rule 2 violation: a slice must
/// not reach into another slice's <c>Application</c>. The rule is not bureaucracy here. It is what
/// stops TicketTypes from being unable to refactor its own mapper without breaking a slice that has no
/// visible relationship to it. Shared/ is the honest home: this is a general limit on evaluating
/// untrusted patterns, not part of the TicketTypes contract, so it does not belong in
/// <c>TicketTypes.ExternalInterfaces</c> either.
///
/// COMPILING A PATTERN PROVES NOTHING ABOUT HOW LONG IT RUNS. <c>(a+)+$</c> is perfectly valid and
/// backtracks catastrophically against a long non-matching input, hanging the thread that evaluates
/// it. The pattern's author is a trusted Accountant; its input is a string from the internet. So this
/// is a request-side denial of service on the whole worker process, and the timeout is the only thing
/// standing in front of it.
///
/// Do NOT reach for <c>RegexOptions.NonBacktracking</c> as a substitute. It is not a drop-in: it
/// rejects backreferences and lookaround at construction time, so a pattern TicketTypes legitimately
/// accepted would throw <c>ArgumentException</c> when Tickets tried to run it — converting a valid
/// ticket type into a 500 in a slice that never saw the pattern authored.
/// </summary>
public static class UserSuppliedRegex
{
    /// <summary>
    /// 100 ms. Generous for any pattern a form field plausibly needs, and far below any request
    /// timeout, so a pathological pattern is reported as a rejected field rather than as a stall.
    /// </summary>
    public static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);
}
