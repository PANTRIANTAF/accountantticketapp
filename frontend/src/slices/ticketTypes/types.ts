/**
 * Six interfaces, each mirroring one C# type property-for-property and in the C# file's ORDER, so
 * the two can be diffed line by line (GeneralUIArchitecture.md section 2.5). There is no generated
 * client and no OpenAPI document -- punch-list item 9 -- so hand-written mirrors are the only
 * option today (section 2.6).
 *
 * THE REQUEST-SIDE AND RESPONSE-SIDE DESCRIPTORS ARE TWO TYPES AND MUST STAY TWO. On the request
 * side ChoiceOptions, Validation and ConditionalVisibility are ALL nullable
 * (CreateTicketTypeRequestDto.cs:26-28); on the response side the first two are non-nullable with
 * `= new()` defaults. Collapsing them into one type makes the editor's optional members look
 * mandatory and the renderer's mandatory members look optional.
 */

import type {
  ChoiceOption,
  ConditionalVisibility,
  FieldDescriptor,
  FieldValidation,
} from '../../shared/dynamicForm/types';

/**
 * The renderer's types live in shared/dynamicForm/, because shared/ may never import from slices/
 * (section 1.4 rule A) and slices/tickets/ will need them. Re-exported here so the rest of this
 * slice has ONE import site.
 */
export type { FieldDescriptor, ChoiceOption, FieldValidation, ConditionalVisibility };

/**
 * Mirrors ExternalInterfaces/TicketTypeListItemDto.cs:6-14. SIX properties and no others.
 *
 * There is no description, createdAt, updatedAt or field count on this DTO. A column for any of
 * them means an N+1 of /detail calls behind a table (Screens/TicketTypesScreens.md section 3.1
 * rule A). Put them on the detail screen.
 */
export interface TicketTypeListItem {
  id: string;
  code: string;
  displayName: string;
  category: string;
  /**
   * A bool, NOT one of the four glossary status strings (section 10.1). Mapped at the render site
   * to the word "Active" / "Inactive"; there is deliberately no TicketTypeStatus enum.
   */
  isActive: boolean;
  currentVersionNumber: number;
}

/**
 * Mirrors ExternalInterfaces/TicketTypeDetailDto.cs:9-44. FOURTEEN properties, in the C# order.
 * Returned by all six endpoints in the slice -- both GETs and all three mutations.
 */
export interface TicketTypeDetail {
  /** The ticket TYPE's id. Stable across every version. TicketTypeDetailDto.cs:12. */
  id: string;
  /**
   * The id of the specific VERSION this response projects -- a C# Guid, so a JSON string
   * (TicketTypeDetailDto.cs:30, set by TicketTypeMapper.cs:237).
   *
   * MIRRORED, NEVER DRAWN. It is in this interface because the DTO carries it and this mirror is
   * property-for-property, not because anything asked to see it: no TicketTypes screen renders it,
   * and the string `versionId` appears nowhere in the screen spec. It is the value a ticket
   * persists as tickets.ticket_type_version_id so that a later edit to the type cannot change what
   * an already-open ticket asked for, and dropping it would leave the future Tickets slice unable
   * to name the version it was handed -- `id` and `versionNumber` alone cannot (the DTO's own
   * comment, TicketTypeDetailDto.cs:14-29, spells out what the two workarounds cost).
   */
  versionId: string;
  /** Immutable after create, and ABSENT from EditTicketTypeRequest. */
  code: string;
  displayName: string;
  /** Non-nullable string server-side: '' when absent, never null. */
  description: string;
  category: string;
  allowEmployeeToOpen: boolean;
  /**
   * Authored, stored, displayed -- and read by NO handler anywhere. CreateTicketHandler.cs:93-95
   * restricts an Employee to a ticket about themselves unconditionally and :107 reads
   * AllowEmployeeToOpen but never this flag. Shown with a note saying so, because an Accountant
   * setting it needs to see that it was stored.
   */
  allowSubjectOtherThanCreator: boolean;
  isActive: boolean;
  /** The LATEST version that exists for this type. TicketTypeMapper.cs:245 -- type.VersionNumber. */
  currentVersionNumber: number;
  /** The version THESE fields came from. TicketTypeMapper.cs:246 -- version.VersionNumber. */
  versionNumber: number;
  /**
   * Already the caller's complete list: ToDetail strips Accountant-only descriptors for a
   * Customer-side caller (TicketTypeMapper.cs:228-230) and re-sorts by DisplayOrder (:249). The
   * client must NOT filter it again -- Screens/TicketTypesScreens.md section 6.8.
   */
  fields: FieldDescriptor[];
  /**
   * The TYPE's creation, not the version's (TicketTypeMapper.cs:247).
   * TicketTypeVersion.CreatedAt exists in the database
   * (20260829_001_CreateTicketTypesSchema.sql:26) and is projected into no DTO.
   *
   * A C# DateTime: UTC, but the offset suffix MAY BE ABSENT (section 10.2). Render through
   * shared/format/dates.ts, which treats a bare value as UTC.
   */
  createdAt: string;
  updatedAt: string;
}

/**
 * Mirrors Application/Dtos/CreateTicketTypeRequestDto.cs:5-14. Seven properties, `code` INCLUDED.
 *
 * Both booleans are `= true` server-side (:11-12), unlike the edit DTO's, which default to false.
 * Send both explicitly on both routes anyway -- see EditTicketTypeRequest.
 */
export interface CreateTicketTypeRequest {
  code: string;
  displayName: string;
  /** '' and never null: the C# property is a non-nullable string reaching a NOT NULL column. */
  description: string;
  category: string;
  allowEmployeeToOpen: boolean;
  allowSubjectOtherThanCreator: boolean;
  fields: CreateFieldDescriptor[];
}

/**
 * Mirrors Application/Dtos/EditTicketTypeRequestDto.cs:5-11. SEVEN properties, `code` ABSENT.
 *
 * TWO DEFAULTS THAT SILENTLY FLIP A FLAG. AllowEmployeeToOpen and AllowSubjectOtherThanCreator are
 * declared with NO initialiser here (:9-10) while the create DTO declares both `= true`
 * (CreateTicketTypeRequestDto.cs:11-12). So an edit payload that OMITS either flag turns it OFF,
 * and turning allowEmployeeToOpen off hides the type from every Employee's list and returns 404 on
 * their reads (ListTicketTypesHandler.cs:32-33; TicketTypeMapper.cs:30-31) -- a whole role loses a
 * whole type, from a property nobody typed. Both booleans are therefore REQUIRED here, not
 * optional: never build this object by spreading only dirty fields.
 *
 * `code` is absent from the DTO, so an unknown `code` property is silently ignored by
 * System.Text.Json -- no 400, no warning. Do not add one "for symmetry".
 */
export interface EditTicketTypeRequest {
  ticketTypeId: string;
  displayName: string;
  description: string;
  category: string;
  allowEmployeeToOpen: boolean;
  allowSubjectOtherThanCreator: boolean;
  /**
   * A FULL REPLACEMENT that mints a new version. EditTicketTypeHandler.cs:51-56 builds a new
   * TicketTypeVersion numbered Max(VersionNumber) + 1 and populates its FieldDescriptors from
   * req.Fields and nothing else -- it never reads the previous version's descriptors. Submit four
   * of five fields and the fifth is gone from v-next, with a 200 OK and no warning anywhere.
   */
  fields: CreateFieldDescriptor[];
}

/** Mirrors Application/Dtos/ToggleTicketTypeRequestDto.cs:5-8. */
export interface ToggleTicketTypeRequest {
  ticketTypeId: string;
  /**
   * Idempotent and silent about it: ToggleTicketTypeHandler.cs:44-45 returns early with a 200, no
   * transaction and no audit entry when the requested state already holds. A success response is
   * therefore not evidence that anything moved -- render from the RETURNED isActive.
   */
  newIsActive: boolean;
}

/**
 * Mirrors Application/Dtos/CreateTicketTypeRequestDto.cs:16-29 -- the REQUEST-side descriptor,
 * used by both /create and /edit. Not the same type as FieldDescriptor.
 */
export interface CreateFieldDescriptor {
  /**
   * NOT TRIMMED SERVER-SIDE. NormalizeFields trims exactly Label and GroupName
   * (TicketTypeMapper.cs:117-124); ValidateFields rejects a whitespace-only key via
   * IsNullOrWhiteSpace (:158) but " key " passes, and the uniqueness set is
   * OrdinalIgnoreCase (:155) -- case-insensitive and whitespace-SENSITIVE. So "key" and "key " are
   * two distinct fields in one version, both stored, indistinguishable on screen, and the second
   * unreachable by any conditionalVisibility.fieldKey a human would type. The client trim is the
   * only guard that exists (punch-list item 19).
   */
  key: string;
  /** Non-nullable string reaching `label VARCHAR(255) NOT NULL`. Never null -- see description. */
  label: string;
  helpText: string;
  /** One of the eleven strings in fieldDataTypes.ts. */
  dataType: string;
  /** Renumbered densely from 0 on every reorder; ToEntity copies it verbatim (:58). */
  displayOrder: number;
  groupName: string;
  isRequired: boolean;
  isVisibleToCustomer: boolean;
  /** null / omitted means "no options". >= 2 for a choice type, 0 for every other type. */
  choiceOptions?: ChoiceOption[] | null;
  /** null means "no rules at all". Genuinely nullable on the request side (:27). */
  validation?: FieldValidationRequest | null;
  /** null means "always shown". Genuinely nullable on the request side (:28). */
  conditionalVisibility?: ConditionalVisibility | null;
}

/**
 * The request-side FieldValidationDto (ExternalInterfaces/TicketTypeDetailDto.cs:70-81). The same
 * C# class serves both directions, so the shape matches FieldValidation exactly -- it is restated
 * rather than reused only so that "no rule" reads as `null` on the way out and as `null` OR `''`
 * OR `[]` on the way in.
 *
 * RegexPattern (:78) and AllowedFileTypes (:79) are NON-NULLABLE with '' and [] defaults: send
 * those, never null. The other seven members are `?` and null is the correct way to say "no rule".
 */
export interface FieldValidationRequest {
  minLength?: number | null;
  maxLength?: number | null;
  minValue?: number | null;
  maxValue?: number | null;
  /** "yyyy-MM-dd". A C# DateOnly -- no timezone, and never built from a Date. */
  earliestDate?: string | null;
  latestDate?: string | null;
  regexPattern: string;
  allowedFileTypes: string[];
  maxFileSizeBytes?: number | null;
}
