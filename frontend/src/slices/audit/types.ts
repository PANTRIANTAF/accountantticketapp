import type { AuditOutcome } from '../../shared/format/enums';

/**
 * Hand-written camelCase mirrors of the Audit slice's DTOs
 * (GeneralUIArchitecture.md section 2.5). Each carries the C# file it mirrors so the next reader
 * can diff them.
 *
 * TWO INTERFACES, NOT A BASE AND A SUBCLASS. AuditEntryDto.cs:12-15 says why in C# and the reason
 * survives translation: if AuditEntryDetail extended AuditEntry then AuditEntry would also BE a
 * detail type, and the separation that keeps up to 8 KB of payload off the LIST endpoint would
 * depend on nobody ever widening a variable. The eleven shared properties are repeated on purpose.
 *
 * NULLABILITY IS READ OFF THE C#, NOT GUESSED. customerId is Guid? -> string | null. Every other
 * string property is non-nullable and initialised to string.Empty, so targetId, sourceIp and
 * userAgent arrive as "" -- never null -- when there is nothing to record.
 */

/** Mirrors Slices/Audit/Application/Dtos/AuditEntryDto.cs:16-28. The LIST row: no payload. */
export interface AuditEntry {
  /** Not a column: the row's link target, /audit/:auditEntryId. */
  id: string;
  /**
   * A UserAccount id -- or, for a failed login, whatever identifier was attempted, which may match
   * no row anywhere. Truncated to 100 characters at write time (AuditApi.cs:46).
   * NEVER resolved to a name: BACKEND_CHANGES_REQUIRED item 23, AuditScreens.md section 6 rule A.
   */
  actorUserId: string;
  /**
   * A STRING here while `role` everywhere else in the API is an integer: AuditApi.cs:35 stores
   * user.Role.ToString(), and LogUnauthenticatedAsync stores the literal "Unknown" (:22). So
   * ROLE_LABELS (an integer map) yields undefined for it and Number(actorRole) yields NaN --
   * see auditRoleLabel() in auditFormat.ts. Truncated to 30 characters (AuditApi.cs:47).
   */
  actorRole: string;
  /** null means "no Customer was involved", NEVER "every Customer". Rendered as an em dash. */
  customerId: string | null;
  /** A catalogue code from AuditActions.cs. Rendered verbatim, monospace -- never humanised. */
  action: string;
  /** From AuditTargets.cs:74-81. "None" is a real value, not a gap. */
  targetKind: string;
  /** May be "". Not unique across kinds. Truncated to 100 characters (AuditApi.cs:50). */
  targetId: string;
  /**
   * Typed with its own vocabulary rather than as a bare string, so StatusChip cannot be handed a
   * Customer status by mistake (GeneralUIArchitecture.md section 10.1). Enforced three ways on the
   * way in: AuditApi.cs:38, the CHECK on audit_entries.outcome, and AuditOutcome.All.
   */
  outcome: AuditOutcome;
  /** C# DateTimeOffset: an ISO string CARRYING AN OFFSET (section 10.2). Parse it directly. */
  occurredAt: string;
  /**
   * The connection's remote address, truncated to 45 characters -- exactly the width of the
   * longest legal textual IP -- so a well-formed address cannot be mangled (AuditApi.cs:55).
   * May be "". Never re-formatted, reverse-resolved or geo-located, and never captioned as "the
   * user's IP address": behind Caddy it may uniformly be the proxy's (punch-list items 2 and 25).
   */
  sourceIp: string;
  /**
   * The raw header, truncated to 512 characters (AuditApi.cs:56) -- and real user agents DO exceed
   * that, so this value genuinely is clipped sometimes. Rendered verbatim and NEVER parsed into
   * "Chrome on Windows": the parse would run on a mutilated string and replace evidence with a guess.
   */
  userAgent: string;
}

/**
 * Mirrors Slices/Audit/Application/Dtos/AuditEntryDetailDto.cs:9-25 -- the same eleven properties
 * plus the two payloads, returned one entry at a time by /api/audit/detail and nowhere else.
 */
export interface AuditEntryDetail {
  id: string;
  actorUserId: string;
  actorRole: string;
  customerId: string | null;
  action: string;
  targetKind: string;
  targetId: string;
  outcome: AuditOutcome;
  occurredAt: string;
  sourceIp: string;
  userAgent: string;

  /**
   * JSON TEXT, NOT AN OBJECT. before_value/after_value are jsonb columns mapped to string?
   * (AuditRecordConfiguration.cs:21-22), so the body carries a quoted string:
   * "beforeValue": "{\"Name\":\"Acme\"}". Object.keys() on it gives character indices.
   *
   * Already redacted AT WRITE TIME by Application/Redaction.cs -- the column never held the
   * secret, so there is nothing to un-redact and "[redacted]" is rendered literally.
   *
   * null means "this entry records no change to existing data" (a create has no before, a read has
   * neither). It does not mean "{}" and it is not an error.
   */
  beforeValue: string | null;
  afterValue: string | null;
}

/**
 * Mirrors Slices/Audit/Application/Dtos/AuditActionsResponseDto.cs:7-12. THREE lists, not two:
 * the shipped DTO is a superset of the backend plan's section 6.3 and the code is right
 * (punch-list item 25). `outcomes` exists because the search 422s an unrecognised outcome, so a
 * client holding its own copy could 422 itself.
 *
 * Never hardcode any of the three: the server adds an action code in the same commit as the
 * feature that emits it, so a copy silently lacks the newest codes -- exactly the ones being
 * investigated. Counts are not depended on anywhere (AuditActions.All is reflection-built).
 */
export interface AuditActionCodes {
  actions: string[];
  targetKinds: string[];
  outcomes: string[];
}

/**
 * Mirrors Slices/Audit/Application/Dtos/SearchAuditLogRequestDto.cs:12-24: eight optional filters
 * combined with AND, plus the two paging fields. All eight null means "the whole log, most recent
 * page first".
 *
 * EVERY FIELD IS `| null` RATHER THAN OPTIONAL, ON PURPOSE. The seven string filters are tested
 * with IsNullOrWhiteSpace so "" happens to behave as absent -- but customerId is a Guid?, and ""
 * from the body is a model-binding 400 whose ProblemDetails carries no sentence worth showing
 * (section 9.3 rule F). Sending null for every untouched field means that asymmetry never arises.
 */
export interface AuditSearchRequest {
  /** Exact, case-sensitive equality (SearchAuditLogHandler.cs:42). A partial id matches nothing. */
  actorUserId: string | null;
  /** Must be in AuditActions.All or the search answers 422 (:95). */
  action: string | null;
  /** Must be in AuditTargets.All or the search answers 422 (:103). */
  targetKind: string | null;
  /** 422 if sent without targetKind (:92). The panel prevents that structurally. */
  targetId: string | null;
  /** The one non-string filter: Guid?. "" is a 400 from binding, not a 422. */
  customerId: string | null;
  /** Must be in AuditOutcome.All or 422 (:107). */
  outcome: string | null;
  /** Inclusive >= (:54). An ISO string with an explicit offset, via toISOString(). */
  from: string | null;
  /** Inclusive <= (:56). 422 if from > to (:85), which the schema catches first. */
  to: string | null;
  /** Clamped up to 1 by PaginatedQuery.Normalize, never rejected. */
  pageNumber: number;
  /** Default 15, clamped to 50, never rejected (punch-list item 17). */
  pageSize: number;
}
