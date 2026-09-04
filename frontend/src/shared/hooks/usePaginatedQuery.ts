import { useQuery, type QueryKey, type UseQueryResult } from '@tanstack/react-query';
import { DEFAULT_PAGE_SIZE, MAX_PAGE_SIZE, type PaginatedResponse } from '../api/paginated';

/**
 * The one hook wrapping useQuery for a PaginatedResponse<T>. Every paginated list in every slice
 * uses it and nothing else (GeneralUIArchitecture.md section 3.2 rule G), so the clamping trap is
 * handled once.
 *
 * Three server behaviours it respects rather than defends against (section 3.3):
 *
 * 1. pageSize is CLAMPED, not rejected. Render the pager from response.pageSize, never from the
 *    value sent, or the pager computes the wrong page count and rows go missing with no error.
 *    PaginatedTable does that; this hook simply never sends more than MAX_PAGE_SIZE.
 * 2. A page past the end returns items: [] with a 200, not a 404 -- hence isOverrunPage, which
 *    EmptyState turns into "back to the first page" rather than "no results".
 * 3. Pages are 1-based; MUI's TablePagination is 0-based. The conversion is NOT here. It is in
 *    PaginatedTable, in exactly one place.
 *
 * The hook does NOT build query keys. The caller supplies one, because every filter that changes
 * the response must appear in it -- otherwise two different filters share one cache entry and the
 * screen shows another filter's rows (section 3.1). And it does not own the page number: the
 * screen holds that in React state and passes it in.
 */
export interface UsePaginatedQueryOptions<T> {
  queryKey: QueryKey;
  queryFn: (page: { pageNumber: number; pageSize: number }) => Promise<PaginatedResponse<T>>;
  pageNumber: number;
  pageSize?: number;
  /** For genuine data dependencies only -- never to express "not allowed" (section 3.2 rule B). */
  enabled?: boolean;
}

/**
 * An INTERSECTION, not an interface extending UseQueryResult. UseQueryResult is a DISCRIMINATED UNION
 * of the pending / error / success shapes, and an interface cannot extend a union ("An interface can
 * only extend an object type or intersection of object types with statically known members", TS2312).
 * The intersection keeps the discrimination intact, so `if (result.data !== undefined)` still narrows
 * for the caller.
 */
export type UsePaginatedQueryResult<T> = UseQueryResult<PaginatedResponse<T>, Error> & {
  /**
   * The page ran past the end of the result set. items is empty but rows exist, so the correct
   * offer is "back to the first page", not "no results".
   */
  isOverrunPage: boolean;
};

export function usePaginatedQuery<T>(
  options: UsePaginatedQueryOptions<T>,
): UsePaginatedQueryResult<T> {
  const pageSize = Math.min(options.pageSize ?? DEFAULT_PAGE_SIZE, MAX_PAGE_SIZE);
  const pageNumber = Math.max(options.pageNumber, 1);

  const query = useQuery<PaginatedResponse<T>, Error>({
    queryKey: options.queryKey,
    queryFn: () => options.queryFn({ pageNumber, pageSize }),
    ...(options.enabled === undefined ? {} : { enabled: options.enabled }),
  });

  const data = query.data;
  const isOverrunPage = data !== undefined && data.totalCount > 0 && data.items.length === 0;

  // The query result is returned UNCHANGED apart from the added flag. It does not reshape items.
  return { ...query, isOverrunPage } as UsePaginatedQueryResult<T>;
}
