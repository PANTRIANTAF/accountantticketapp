import {
  useMutation,
  useQuery,
  useQueryClient,
  type QueryClient,
  type UseQueryResult,
} from '@tanstack/react-query';
import {
  usePaginatedQuery,
  type UsePaginatedQueryResult,
} from '../../shared/hooks/usePaginatedQuery';
import {
  createTicketType,
  editTicketType,
  getTicketType,
  getTicketTypeVersion,
  listTicketTypes,
  toggleTicketType,
} from './api';
import type {
  CreateTicketTypeRequest,
  EditTicketTypeRequest,
  TicketTypeDetail,
  TicketTypeListItem,
  ToggleTicketTypeRequest,
} from './types';

/**
 * Three queries and three mutations. Screens import hooks; screens never import api.ts
 * (GeneralUIArchitecture.md section 3.2 rule A).
 *
 * The keys are section 3.1's, verbatim, and they are built HERE so no screen spells one:
 *
 *   ['ticketTypes','list',{ pageNumber, pageSize, activeOnly }]
 *   ['ticketTypes','detail', ticketTypeId]
 *   ['ticketTypes','version', ticketTypeId, versionNumber]
 */
export const ticketTypeKeys = {
  all: ['ticketTypes'] as const,
  lists: ['ticketTypes', 'list'] as const,
  /**
   * ACTIVEONLY APPEARS IN THE KEY EVEN WHEN `undefined`, so the three states of the filter cannot
   * share one cache entry. Every filter that changes the response must be in the key, or *All* and
   * *Inactive* read each other's rows and the screen shows the wrong ones with no error anywhere.
   */
  list: (pageNumber: number, pageSize: number, activeOnly: boolean | undefined) =>
    ['ticketTypes', 'list', { pageNumber, pageSize, activeOnly }] as const,
  detail: (ticketTypeId: string) => ['ticketTypes', 'detail', ticketTypeId] as const,
  version: (ticketTypeId: string, versionNumber: number) =>
    ['ticketTypes', 'version', ticketTypeId, versionNumber] as const,
};

/** Through usePaginatedQuery and nothing else (section 3.2 rule G), so the clamping trap is handled once. */
export function useTicketTypeList(params: {
  pageNumber: number;
  pageSize: number;
  activeOnly?: boolean | undefined;
}): UsePaginatedQueryResult<TicketTypeListItem> {
  const { activeOnly } = params;

  return usePaginatedQuery<TicketTypeListItem>({
    queryKey: ticketTypeKeys.list(params.pageNumber, params.pageSize, activeOnly),
    // The hook supplies the clamped page; activeOnly comes from the caller and is omitted from the
    // query string by api.ts when it is undefined.
    queryFn: (requested) => listTicketTypes({ ...requested, activeOnly }),
    pageNumber: params.pageNumber,
    pageSize: params.pageSize,
  });
}

/**
 * `enabled` is a GENUINE DATA DEPENDENCY here, not a permission gate (section 3.2 rule B): the
 * ticketTypeId comes from a route parameter, and /api/ticket-types/detail takes a NON-NULLABLE Guid,
 * so calling it with '' is a 400 from the model binder with framework wording.
 */
export function useTicketTypeDetail(
  ticketTypeId: string,
  options?: { enabled?: boolean },
): UseQueryResult<TicketTypeDetail, Error> {
  return useQuery<TicketTypeDetail, Error>({
    queryKey: ticketTypeKeys.detail(ticketTypeId),
    queryFn: () => getTicketType(ticketTypeId),
    enabled: Boolean(ticketTypeId) && (options?.enabled ?? true),
  });
}

/**
 * THE PRE-SUBMIT READ OF THE STALE CHECK (GeneralUIArchitecture.md section 9.4 step 2, screen spec
 * section 5.6). It lives here, and not in the screen, because a screen never imports api.ts
 * (section 3.2 rule A) -- the query key and the endpoint function must stay in one file or the
 * editor reads a key nothing else writes.
 *
 * `fetchQuery`, NOT `invalidateQueries`: the value is needed HERE AND NOW, before a POST is sent, and
 * an invalidation is a background refresh whose result arrives after the decision has been made.
 *
 * `staleTime: 0` STATED EXPLICITLY, because `fetchQuery` resolves from the cache when the entry is
 * still fresh -- and a stale check that can be answered by the cache it is checking against is not a
 * check. This must reach the server every time.
 */
export function fetchCurrentTicketTypeDetail(
  queryClient: QueryClient,
  ticketTypeId: string,
): Promise<TicketTypeDetail> {
  return queryClient.fetchQuery({
    queryKey: ticketTypeKeys.detail(ticketTypeId),
    queryFn: () => getTicketType(ticketTypeId),
    staleTime: 0,
  });
}

/**
 * A VERSION KEY IS NEVER INVALIDATED BY ANYTHING, so staleTime is Infinity.
 * ticket_type_versions rows are immutable by design: EditTicketTypeHandler.cs:53-56 only ever ADDS a
 * row and nothing in the slice updates one. An invalidation here is a refetch that can only ever
 * return the identical bytes.
 */
export function useTicketTypeVersion(
  ticketTypeId: string,
  versionNumber: number | undefined,
): UseQueryResult<TicketTypeDetail, Error> {
  return useQuery<TicketTypeDetail, Error>({
    queryKey: ticketTypeKeys.version(ticketTypeId, versionNumber ?? 0),
    queryFn: () => {
      // Unreachable while `enabled` holds; a throw rather than a `?? 0` because requesting version 0
      // would be a 404 the user could not explain, and rather than a cast because a cast is a claim.
      if (versionNumber === undefined) throw new Error('versionNumber is required.');
      return getTicketTypeVersion(ticketTypeId, versionNumber);
    },
    enabled: Boolean(ticketTypeId) && versionNumber !== undefined,
    staleTime: Infinity,
  });
}

/**
 * THE SHARED onSuccess OF ALL THREE MUTATIONS: SEED THE DETAIL KEY FROM THE RESPONSE, THEN
 * INVALIDATE THE LIST KEYS.
 *
 * All three mutating endpoints return the full TicketTypeDetailDto
 * (TicketTypesEndpoints.cs:23, 32, 41), deliberately, so there is no second round trip -- refetching
 * the detail would discard a response already in hand and open a window where the screen shows stale
 * data after a successful save (section 3.2 rule D).
 *
 * The invalidation is the LIST PREFIX and not the whole cache (rule C): a create adds a row, an edit
 * can change displayName and therefore the row's position in the server's `DisplayName, Id`
 * ordering, and a toggle changes which of the three filter states a row belongs to. The version keys
 * are deliberately NOT invalidated -- see useTicketTypeVersion.
 *
 * NO OPTIMISTIC UPDATES, IN ANY OF THE THREE (rule E). There is no concurrency token anywhere in the
 * built backend, so an optimistic edit is a confident display of a version number that may not
 * exist.
 */
function seedDetailAndInvalidateLists(queryClient: QueryClient, updated: TicketTypeDetail): void {
  queryClient.setQueryData(ticketTypeKeys.detail(updated.id), updated);
  queryClient.invalidateQueries({ queryKey: ticketTypeKeys.lists });
}

export function useCreateTicketType() {
  const queryClient = useQueryClient();

  return useMutation<TicketTypeDetail, Error, CreateTicketTypeRequest>({
    mutationFn: createTicketType,
    onSuccess: (created) => {
      seedDetailAndInvalidateLists(queryClient, created);
    },
  });
}

/**
 * Every call mints a new version, including one that changed nothing: /edit has no early return.
 * The caller names the version in its success message -- "Saved as version 4." -- because silent
 * success on an operation that increments a counter is how a catalogue reaches v30 by accident.
 *
 * The stale check that must run BEFORE this mutation is in TicketTypeEditorScreen: it is a
 * pre-submit read, not a cache concern, and it must be able to abort without a request being sent.
 */
export function useEditTicketType() {
  const queryClient = useQueryClient();

  return useMutation<TicketTypeDetail, Error, EditTicketTypeRequest>({
    mutationFn: editTicketType,
    onSuccess: (updated) => {
      seedDetailAndInvalidateLists(queryClient, updated);
    },
  });
}

/**
 * Idempotent and silent about it (ToggleTicketTypeHandler.cs:44-45), so the caller renders from the
 * RETURNED isActive and never from what it sent.
 */
export function useToggleTicketType() {
  const queryClient = useQueryClient();

  return useMutation<TicketTypeDetail, Error, ToggleTicketTypeRequest>({
    mutationFn: toggleTicketType,
    onSuccess: (updated) => {
      seedDetailAndInvalidateLists(queryClient, updated);
    },
  });
}
