using AccountantApp.Api.Shared.Errors;
using AccountantApp.Api.Slices.Tickets.Application;
using AccountantApp.Api.Slices.Tickets.Core;
using static AccountantApp.Tests.Tickets.TicketsTestHarness;

namespace AccountantApp.Tests.Tickets;

/// <summary>Optimistic concurrency on the tickets row, plan section 3.2 / section 9.7, LOCKED.</summary>
public sealed class TicketConcurrencyTests
{
    [Fact]
    public void A_matching_version_passes()
    {
        var ticket = NewTicket(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        TicketConcurrency.RequireVersion(ticket, 1);
    }

    /// <summary>
    /// 409, not 500 and not 422 (section 9.7). It is not a server fault and the request was valid when the
    /// client composed it; only a 409 tells the client that retrying unchanged is pointless while
    /// retrying after a reload is not.
    /// </summary>
    [Fact]
    public void A_stale_version_is_409()
    {
        var ticket = NewTicket(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        TicketConcurrency.Touch(ticket, Now);

        var exception = Assert.Throws<AppException>(() => TicketConcurrency.RequireVersion(ticket, 1));

        Assert.Equal(409, exception.StatusCode);
    }

    /// <summary>
    /// A version from the FUTURE is a conflict too. An inequality check rather than a "less than" one:
    /// the only correct response to a version the server has never issued is to make the client re-read.
    /// </summary>
    [Fact]
    public void A_version_ahead_of_the_row_is_also_409()
    {
        var ticket = NewTicket(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        Assert.Equal(409,
            Assert.Throws<AppException>(() => TicketConcurrency.RequireVersion(ticket, 7)).StatusCode);
    }

    [Fact]
    public void Touch_advances_the_version_and_stamps_activity()
    {
        var ticket = NewTicket(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var later = Now.AddDays(2);

        TicketConcurrency.Touch(ticket, later);

        Assert.Equal(2, ticket.Version);
        Assert.Equal(later, ticket.LastActivityAt);

        TicketConcurrency.Touch(ticket, later.AddMinutes(1));
        Assert.Equal(3, ticket.Version);
    }

    /// <summary>
    /// Section 3.2 rule 5 and success criterion 38. The append-only entities carry NO concurrency token:
    /// posting a message or adding a verification does not conflict with a concurrent one, and a version
    /// check there would reject two people typing at once for no reason.
    ///
    /// Asserted by reflection because the failure mode is somebody adding the property "for consistency"
    /// -- which compiles, maps to a column that does not exist, and fails on the first insert.
    /// </summary>
    [Fact]
    public void No_append_only_entity_has_a_version_property()
    {
        foreach (var type in new[]
                 {
                     typeof(TicketRevision), typeof(FieldValue), typeof(FieldVerification),
                     typeof(TicketMessage), typeof(TicketMessageDocument),
                 })
            Assert.Null(type.GetProperty("Version"));

        // And the ticket itself does carry one -- the other half of the rule.
        Assert.NotNull(typeof(Ticket).GetProperty("Version"));
    }
}
