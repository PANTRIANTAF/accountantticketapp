import { get, post } from '../../shared/api/http';
import type { PaginatedResponse } from '../../shared/api/paginated';
import type { MarkReadResult, Notification, UnreadCountResult } from './types';

/**
 * One function per endpoint, verified line by line against
 * AccountantApp.Api/Slices/Notifications/NotificationsEndpoints.cs:
 *
 *   listNotifications         POST /api/notifications/list           :19   a POST READ, body required
 *   getUnreadCount            GET  /api/notifications/unread-count   :23   no query string at all
 *   markNotificationsRead     POST /api/notifications/mark-read      :27   { notificationIds }, 422 x2
 *   markAllNotificationsRead  POST /api/notifications/mark-all-read  :31   NO body parameter (:65-72)
 *
 * NO REACT, NO HOOKS, NO TANSTACK QUERY here (GeneralUIArchitecture.md section 2.5). Cache behaviour
 * is in queries.ts.
 *
 * THE VERBS ARE ASYMMETRIC ON PURPOSE, and neither is to be "corrected". Section 2.3 rule C names
 * /api/notifications/list among the five POST reads this SPA calls; the `list` suffix predicts
 * nothing -- /api/ticket-types/list next door is a GET. Changing either verb yields a 405 with
 * nothing in the body to explain it.
 *
 * The group carries no .RequireAuthorization() (NotificationsEndpoints.cs:11-16) and that is
 * deliberate: authentication is enforced by CurrentUserFactory, which answers 401 when there is no
 * principal. So an anonymous call here is a 401, never a 200 with zero -- which is why
 * useUnreadCount is gated on the session rather than left polling.
 */

/**
 * A POST read with a filter body.
 *
 * Typed from ListMyNotificationsHandler's return type (PaginatedResponse<NotificationDto>), NOT from
 * the endpoint's `.Produces<object>(200)` annotation, which is wrong about the handler rather than
 * the other way round (BACKEND_CHANGES_REQUIRED item 9).
 *
 * ALL THREE KEYS GO ON EVERY CALL. ListMyNotificationsRequestDto is a required parameter
 * (NotificationsEndpoints.cs:37), so an absent body is a 400 from model binding -- not the C#
 * property defaults of `false / 1 / 15` (ListMyNotificationsRequestDto.cs:5-7).
 *
 * pageSize is CLAMPED to 50, not rejected (PaginatedQuery.Normalize; BACKEND_CHANGES_REQUIRED
 * item 17). Ask for 200 and the response says 50 with a 200 OK, so every pager renders from
 * response.pageSize.
 */
export const listNotifications = (body: {
  unreadOnly: boolean;
  pageNumber: number;
  pageSize: number;
}): Promise<PaginatedResponse<Notification>> => post('/api/notifications/list', body);

/** A GET, and no query string at all -- the route takes nothing (NotificationsEndpoints.cs:23, :46-53). */
export const getUnreadCount = (): Promise<UnreadCountResult> =>
  get('/api/notifications/unread-count');

/**
 * MarkNotificationsReadHandler.MaxIdsPerRequest (MarkNotificationsReadHandler.cs:15). The server
 * checks it AFTER Distinct() (:46-51); the assert below runs on the raw array, so this client is
 * marginally stricter than the server -- which is correct, because a guard that fires only on input
 * the server would also reject is never exercised.
 */
const MARK_READ_MAX_IDS = 200;

/**
 * Two bounds, both asserted here rather than discovered as a 422.
 *
 * EMPTY: MarkNotificationsReadHandler.cs:43-44 answers 422 "NotificationIds cannot be empty." -- a
 * banner naming a C# DTO property, for a no-op nobody asked for. Above this file the screen never
 * offers the action with nothing selected, and a row's button always carries exactly one id.
 *
 * OVER THE CAP: assert, DO NOT CHUNK. With pageSize clamped to 50 the cap is unreachable today, so
 * a chunking loop would be dead code -- and a silent multi-request loop turns one audited operation
 * into several. If a future bulk action could exceed 200, this throws in development, in the file
 * that owns the bound.
 */
export function markNotificationsRead(notificationIds: string[]): Promise<MarkReadResult> {
  if (notificationIds.length === 0) throw new Error('markNotificationsRead: no ids');
  if (notificationIds.length > MARK_READ_MAX_IDS) {
    throw new Error(
      `markNotificationsRead: ${String(notificationIds.length)} ids exceeds ${String(MARK_READ_MAX_IDS)}`,
    );
  }
  return post('/api/notifications/mark-read', { notificationIds });
}

/**
 * NO SECOND ARGUMENT. The endpoint declares no body parameter at all
 * (NotificationsEndpoints.cs:65-72). Sending `{}` works today but asserts a contract the endpoint
 * does not have.
 *
 * Irreversible: there is no mark-unread endpoint anywhere in the API and `is_read` is the row's only
 * mutable field, so the caller gates this behind a ConfirmDialog (NotificationsScreens.md section 6
 * rule F).
 */
export const markAllNotificationsRead = (): Promise<MarkReadResult> =>
  post('/api/notifications/mark-all-read');
