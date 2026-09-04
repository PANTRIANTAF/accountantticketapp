/**
 * Mirrors Shared/Pagination/PaginatedResponse.cs. Identical for every list in the API -- there is
 * no per-slice variant to add later.
 */
export interface PaginatedResponse<T> {
  pageNumber: number;
  pageSize: number;
  totalCount: number;
  totalPages: number;
  items: T[];
}

/**
 * Mirrors Shared/Pagination/PaginatedQuery.cs:7-12, whose Normalize is
 * (Math.Max(pageNumber, 1), Math.Clamp(pageSize <= 0 ? DefaultPageSize : pageSize, 1, MaxPageSize)).
 *
 * pageSize is CLAMPED, not rejected: ask for 999 and the response says 50 with a 200 OK. So no
 * screen imports MAX_PAGE_SIZE to validate input (BACKEND_CHANGES_REQUIRED item 17) -- the
 * constant exists so a page-size selector never OFFERS more than 50, and PaginatedTable renders
 * the pager from response.pageSize rather than from the value sent.
 */
export const DEFAULT_PAGE_SIZE = 15;
export const MAX_PAGE_SIZE = 50;

/** The two parameters every paginated read takes. */
export interface PageRequest {
  pageNumber: number;
  pageSize: number;
}
