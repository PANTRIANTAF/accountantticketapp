namespace AccountantApp.Api.Slices.Tickets.Application.Dtos;

/// <summary>
/// The Office's work queue. It takes no filter on purpose: the two conditions of plan §4.4 ARE the
/// queue, and a status filter here is the first step toward "status == Submitted", which is the most
/// likely bug in the state machine.
/// </summary>
public class ListPickupQueueRequestDto
{
    public int PageNumber { get; set; } = 1;

    public int PageSize { get; set; } = Shared.Pagination.PaginatedQuery.DefaultPageSize;
}
