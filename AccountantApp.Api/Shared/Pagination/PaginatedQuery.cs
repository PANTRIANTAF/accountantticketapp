namespace AccountantApp.Api.Shared.Pagination;

// The system-wide pagination ceiling (App/GeneralAppArchitecture.md §8) lives in one place
// so a slice cannot quietly pick its own default or maximum.
public static class PaginatedQuery
{
    public const int DefaultPageSize = 15;
    public const int MaxPageSize = 50;

    public static (int PageNumber, int PageSize) Normalize(int pageNumber, int pageSize) =>
        (Math.Max(pageNumber, 1),
         Math.Clamp(pageSize <= 0 ? DefaultPageSize : pageSize, 1, MaxPageSize));
}
