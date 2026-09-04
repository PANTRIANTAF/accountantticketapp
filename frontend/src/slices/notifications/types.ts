/**
 * Wire types for the Notifications slice. Three interfaces, camelCase, each commented with the C#
 * DTO it mirrors (GeneralUIArchitecture.md section 2.5). A C# `Guid` is a `string` here.
 *
 * Hand-written rather than generated: there is no OpenAPI document
 * (BACKEND_CHANGES_REQUIRED item 9), and `/api/notifications/list` declares `.Produces<object>(200)`
 * (NotificationsEndpoints.cs:21) which would generate as `unknown` anyway. The shapes below are read
 * off ListMyNotificationsHandler and the DTO files, never off the annotation.
 */

/**
 * Mirrors Slices/Notifications/Application/Dtos/NotificationDto.cs:5-15.
 *
 * The envelope is PaginatedResponse<Notification>, ordered createdAt desc then id desc
 * (ListMyNotificationsHandler.cs:44-45). Every row is already scoped to CurrentUser.Id by the
 * handler (:34) and no endpoint accepts a recipient, so there is no client-side row filtering to do
 * -- and doing any would be a UI concealing a server leak (02-AuthorizationMatrix.md section 9).
 */
export interface Notification {
  id: string;
  /** Guid? -- present on the ticket kinds. NEVER rendered (NotificationsScreens.md section 4.4 rule F). */
  ticketId: string | null;
  /**
   * A raw string, NOT a union. NotificationEvents.cs will grow, and a union makes an unknown kind a
   * type error at the one boundary TypeScript cannot check -- then invites an exhaustive switch that
   * throws on the nineteenth kind. eventKinds.ts handles it as data instead.
   */
  eventKind: string;
  /** Written by the producing handler for this reader. Rendered verbatim (section 4.4 rule B). */
  title: string;
  /** Plain text, `\n` and no markup. Rendered as TEXT, never as HTML (section 4.4 rule C). */
  body: string;
  isRead: boolean;
  /** DateTimeOffset?. Never rendered: the unread dot already says what this says. */
  readAt: string | null;
  /** DateTimeOffset -- carries an offset, so it parses directly (GeneralUIArchitecture.md section 10.2). */
  createdAt: string;
  /**
   * Pending | Sent | Failed | Abandoned | Skipped | null, projected from the outbox row
   * (ListMyNotificationsHandler.cs:55-58, :75). Operator telemetry a recipient can only worry
   * about; never rendered (section 4.4 rule F).
   */
  emailStatus: string | null;
}

/**
 * Mirrors Slices/Notifications/Application/Dtos/MarkReadResponseDto.cs:5 -- the response of BOTH
 * mark endpoints. It counts only rows that were not already read
 * (MarkNotificationsReadHandler.cs:62-69, MarkAllNotificationsReadHandler.cs:39-45), so 0 is a
 * reachable success and not an error.
 */
export interface MarkReadResult {
  markedCount: number;
}

/** Mirrors Slices/Notifications/Application/Dtos/UnreadCountResponseDto.cs:5. */
export interface UnreadCountResult {
  unreadCount: number;
}
