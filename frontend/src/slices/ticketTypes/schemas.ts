import * as z from 'zod';
import {
  CHOICE_DATA_TYPES,
  FIELD_DATA_TYPES,
  isChoiceDataType,
  isKnownDataType,
} from './fieldDataTypes';
import type {
  CreateFieldDescriptor,
  CreateTicketTypeRequest,
  EditTicketTypeRequest,
  FieldValidationRequest,
  TicketTypeDetail,
} from './types';

/**
 * THE EDITOR'S FORM SHAPE, ITS ZOD SCHEMA, AND THE TWO REQUEST BUILDERS.
 * Screens/TicketTypesScreens.md sections 5.3-5.5; plan sections 8.1-8.9.
 *
 * EVERY LIMIT HERE IS TicketTypeMapper.cs, MIRRORED EXACTLY -- no stricter, no looser
 * (GeneralUIArchitecture.md section 9.2). A stricter client blocks input the server would accept; a
 * looser one produces a 422 whose ProblemDetails carries no field map, so the message lands in a
 * form-level banner and the user has to guess which of twelve rows it means. The two deliberate
 * exceptions are `helpText` and `regexPattern`-compiles-in-JS, and both say so where they are
 * written.
 *
 * THE SCHEMA IS ALSO THE TRIMMER. `.trim()` runs as part of parsing, and zodResolver hands React
 * Hook Form the PARSED values, so `handleSubmit` receives trimmed strings and the request builders
 * below never trim anything themselves. Trim-then-length is what mirrors the server exactly, because
 * the value the server length-checks is the trimmed value we send.
 *
 * WHAT THE SERVER TRIMS, AND WHAT IT DOES NOT (TicketTypeMapper.cs:99-124). `NormalizeTicketType`
 * trims `Code`, `DisplayName` and `Category`; `NormalizeFields` trims exactly `Label` and
 * `GroupName`. NOT `Key`, NOT `HelpText`, NOT `Description`, NOT a choice option. `key` is the one
 * that matters: `ValidateFields` rejects a whitespace-only key (`:158`) but `" key "` passes, and the
 * uniqueness set is `OrdinalIgnoreCase` (`:155`) -- case-insensitive and whitespace-SENSITIVE. So
 * `"key"` and `"key "` are two distinct fields in one version, both stored, indistinguishable on
 * screen, and the second unreachable by any `conditionalVisibility.fieldKey` a human would type,
 * because `keys.Contains` (`:195`) is whitespace-sensitive too. THE CLIENT TRIM IS THE ONLY GUARD
 * THAT EXISTS (punch-list item 19).
 *
 * NO CHARACTER PATTERN ON `key`, DELIBERATELY. The server accepts any non-blank string of <= 100
 * characters, and section 9.2 forbids a client limit stricter than the server's. The React Hook Form
 * path-syntax hazard that a `.` or `[` in a key would otherwise cause is handled in the renderer by
 * the alias map (shared/dynamicForm/buildZodSchema.ts), not by narrowing what an author may type.
 */

// -------------------------------------------------------------------------------------------------
// Limits. Every constant is TicketTypeMapper.cs:81-95, by name.
// -------------------------------------------------------------------------------------------------

const CODE_MAX = 100; // CodeMaxLength
const DISPLAY_NAME_MAX = 255; // DisplayNameMaxLength
const CATEGORY_MAX = 100; // CategoryMaxLength
const FIELD_KEY_MAX = 100; // FieldKeyMaxLength
const FIELD_LABEL_MAX = 255; // FieldLabelMaxLength
const GROUP_NAME_MAX = 100; // GroupNameMaxLength
const REGEX_PATTERN_MAX = 500; // RegexPatternMaxLength
const ALLOWED_FILE_TYPES_MAX = 500; // AllowedFileTypesMaxLength -- on the JOINED string
const CONDITIONAL_VALUE_MAX = 500; // ConditionalValueMaxLength
const DESCRIPTION_MAX = 10_000; // DescriptionMaxLength

/**
 * HelpTextMaxLength (TicketTypeMapper.cs:95) IS DECLARED AND NEVER READ. Confirmed by grepping the
 * slice: the constant appears once, at its declaration. `ValidateFields` length-checks `label`,
 * `groupName`, `regexPattern`, both conditional-visibility members and the joined `allowedFileTypes`
 * -- never `helpText` -- and the column is `help_text TEXT NOT NULL`, which PostgreSQL does not
 * bound. So an over-long value does not fail; it is simply stored forever on a table nothing purges.
 *
 * Mirrored anyway, as a deliberate exception to section 9.2: it is the documented intent, the
 * constant exists, and a client cap is currently the only cap. Correction note T-11 records the
 * intent and the call was never added. Reported as a backend defect.
 */
const HELP_TEXT_MAX = 10_000;

// -------------------------------------------------------------------------------------------------
// Which validation members a data type can actually use. Screen spec section 6.4's table.
// -------------------------------------------------------------------------------------------------

/** The nine members of FieldValidationDto, as form-state keys. */
export type ValidationMember =
  | 'minLength'
  | 'maxLength'
  | 'minValue'
  | 'maxValue'
  | 'earliestDate'
  | 'latestDate'
  | 'regexPattern'
  | 'allowedFileTypes'
  | 'maxFileSizeBytes';

/**
 * THE APPLICABILITY TABLE, from screen spec section 6.4. A member absent from a data type's row is
 * dropped on submit and never rendered -- see clearInapplicableValidation and toValidationRequest.
 *
 * The server does NOT cross-check a member against the data type (`ValidateFields` validates each
 * bound in isolation), so `minValue` on a SingleLineText is accepted, stored, meaningless, and will
 * be applied by whatever future renderer trusts it. The editor is the only place it is prevented
 * (screen spec section 5.5 rule B), which is why this table lives on the request side of the slice
 * and not in the renderer.
 */
const VALIDATION_MEMBERS: Record<string, readonly ValidationMember[]> = {
  SingleLineText: ['minLength', 'maxLength', 'regexPattern'],
  MultiLineText: ['minLength', 'maxLength', 'regexPattern'],
  WholeNumber: ['minValue', 'maxValue'],
  DecimalNumber: ['minValue', 'maxValue'],
  MoneyAmount: ['minValue', 'maxValue'],
  Date: ['earliestDate', 'latestDate'],
  DateRange: ['earliestDate', 'latestDate'],
  YesNo: [],
  SingleChoice: [],
  MultipleChoice: [],
  FileUpload: ['allowedFileTypes', 'maxFileSizeBytes'],
};

/** [] for a dataType that is not one of the eleven, so an unknown type contributes no rule. */
export function validationMembersFor(dataType: string): readonly ValidationMember[] {
  return VALIDATION_MEMBERS[dataType] ?? [];
}

export function appliesTo(dataType: string, member: ValidationMember): boolean {
  return validationMembersFor(dataType).includes(member);
}

// -------------------------------------------------------------------------------------------------
// The form's value shape.
// -------------------------------------------------------------------------------------------------

export interface ChoiceOptionFormValues {
  label: string;
  value: string;
}

/**
 * A FLAT MIRROR OF FieldValidationDto, with EVERY member present regardless of the data type -- an
 * absent member and a member the type cannot use are different things in form state, and RHF cannot
 * register a control for a key that is not there. `toValidationRequest` is what drops the
 * inapplicable ones on the way out.
 *
 * Numbers are `number | null`; dates are `''`. A numeric input's `valueAsNumber` is NaN when it is
 * cleared and `Number('')` is 0 -- a zero the author never typed -- so the empty state has to be its
 * own value, and `null` is that value. `allowedFileTypes` is ONE comma-separated string here, not an
 * array, because the server validates the JOINED length (TicketTypeMapper.cs:174-176) and a control
 * that checks each entry separately accepts sixty short extensions and earns a 422 naming a limit
 * every individual value is inside.
 */
export interface ValidationFormValues {
  minLength: number | null;
  maxLength: number | null;
  minValue: number | null;
  maxValue: number | null;
  /** "yyyy-MM-dd" or '' -- a C# DateOnly, so never built from a Date. */
  earliestDate: string;
  latestDate: string;
  regexPattern: string;
  /** Comma-separated. Split, trimmed and re-joined by toValidationRequest. */
  allowedFileTypes: string;
  maxFileSizeBytes: number | null;
}

/**
 * `enabled` EXISTS BECAUSE THE DTO MEMBER IS NULLABLE AND A FORM CONTROL IS NOT. RHF cannot register
 * `fields.0.conditionalVisibility.fieldKey` while the object is null, and toggling the rule off by
 * nulling the object would discard the author's fieldKey and value the moment they untick it. So the
 * pair is always in form state and `enabled` decides whether it is sent.
 */
export interface ConditionalVisibilityFormValues {
  enabled: boolean;
  fieldKey: string;
  value: string;
}

export interface FieldFormValues {
  key: string;
  label: string;
  helpText: string;
  dataType: string;
  /**
   * IN FORM STATE ONLY SO THE LOADED VALUE IS NOT LOST BEFORE THE FIRST REORDER. It is REWRITTEN TO
   * THE ARRAY INDEX on every move and again on submit (toFieldRequest), because array position is
   * persisted nowhere: `display_order` is the only ordering the server stores, `ToEntity` copies it
   * verbatim (TicketTypeMapper.cs:58) and `ToDetail` re-sorts by it on the way out (:249). A reorder
   * that left the old numbers would render in the new order until reload and in the old order after.
   */
  displayOrder: number;
  groupName: string;
  isRequired: boolean;
  isVisibleToCustomer: boolean;
  choiceOptions: ChoiceOptionFormValues[];
  validation: ValidationFormValues;
  conditionalVisibility: ConditionalVisibilityFormValues;
}

/**
 * `code` is present in form state in BOTH modes but is read-only in edit mode and is NOT sent by
 * toEditRequest -- EditTicketTypeRequestDto.cs:5-11 has no Code property, and an unknown JSON
 * property is silently ignored by System.Text.Json, so there would be no 400 to catch. See
 * toEditRequest.
 */
export interface TicketTypeFormValues {
  code: string;
  displayName: string;
  description: string;
  category: string;
  allowEmployeeToOpen: boolean;
  allowSubjectOtherThanCreator: boolean;
  fields: FieldFormValues[];
}

// -------------------------------------------------------------------------------------------------
// Blank rows and data-type transitions.
// -------------------------------------------------------------------------------------------------

export function blankValidation(): ValidationFormValues {
  return {
    minLength: null,
    maxLength: null,
    minValue: null,
    maxValue: null,
    earliestDate: '',
    latestDate: '',
    regexPattern: '',
    allowedFileTypes: '',
    maxFileSizeBytes: null,
  };
}

export function blankChoiceOption(): ChoiceOptionFormValues {
  return { label: '', value: '' };
}

/**
 * Both booleans default to `true`, matching CreateFieldDescriptorDto.cs:24-25. A row that defaulted
 * `isVisibleToCustomer` to false would hide every new field from every Customer-side caller, which
 * is a data-visibility decision made by a default nobody typed.
 */
export function blankField(displayOrder: number): FieldFormValues {
  return {
    key: '',
    label: '',
    helpText: '',
    dataType: 'SingleLineText',
    displayOrder,
    groupName: '',
    isRequired: true,
    isVisibleToCustomer: true,
    choiceOptions: [],
    validation: blankValidation(),
    conditionalVisibility: { enabled: false, fieldKey: '', value: '' },
  };
}

/**
 * The create form's starting point: one field row, both flags on (CreateTicketTypeRequestDto.cs:11-12
 * declares both `= true`) and one row rather than none, because `ValidateFields` returns
 * 422 "At least one field is required." for an empty array.
 */
export function blankTicketType(): TicketTypeFormValues {
  return {
    code: '',
    displayName: '',
    description: '',
    category: '',
    allowEmployeeToOpen: true,
    allowSubjectOtherThanCreator: true,
    fields: [blankField(0)],
  };
}

/**
 * CHANGING A FIELD'S DATA TYPE IS A STATE TRANSITION, NOT A PLAIN ASSIGNMENT (screen spec section 5.5
 * rule B). Three things move together:
 *
 *  - away from a choice type CLEARS choiceOptions, or the row still carries the two options it had
 *    and the save fails with 422 "Non-choice field 'x' cannot have choice options."
 *    (TicketTypeMapper.cs:184) -- a message naming a field the author just fixed;
 *  - to a choice type SEEDS TWO BLANK OPTION ROWS, because `< 2` is a 422 the other way (:182) and
 *    an empty options list gives the author nothing to type into;
 *  - any change CLEARS EVERY VALIDATION MEMBER THE NEW TYPE CANNOT USE. The server does not
 *    cross-check them, so a `minValue` left behind on a SingleLineText is stored, is meaningless, and
 *    will be applied by a future renderer that trusts it.
 *
 * conditionalVisibility is deliberately UNTOUCHED: it is a rule about ANOTHER field, and changing
 * this field's own type does not invalidate it. A rule pointing AT this field is a different matter
 * and is re-checked by the schema on every parse, so it surfaces as a validation error rather than
 * as a silent rewrite of somebody's rule.
 */
export function applyDataTypeChange(field: FieldFormValues, nextDataType: string): FieldFormValues {
  const wasChoice = isChoiceDataType(field.dataType);
  const isChoice = isChoiceDataType(nextDataType);

  let choiceOptions = field.choiceOptions;
  if (isChoice && !wasChoice) choiceOptions = [blankChoiceOption(), blankChoiceOption()];
  else if (!isChoice) choiceOptions = [];

  return {
    ...field,
    dataType: nextDataType,
    choiceOptions,
    validation: clearInapplicableValidation(field.validation, nextDataType),
  };
}

/** Every member the data type cannot use goes back to its empty value. */
export function clearInapplicableValidation(
  validation: ValidationFormValues,
  dataType: string,
): ValidationFormValues {
  const blank = blankValidation();
  const keep = validationMembersFor(dataType);
  const result = { ...blank };

  // Written member by member rather than by looping over keys, so the compiler checks that each
  // assignment's type matches. A generic loop would need a cast and would silently keep compiling if
  // a member's type ever changed.
  if (keep.includes('minLength')) result.minLength = validation.minLength;
  if (keep.includes('maxLength')) result.maxLength = validation.maxLength;
  if (keep.includes('minValue')) result.minValue = validation.minValue;
  if (keep.includes('maxValue')) result.maxValue = validation.maxValue;
  if (keep.includes('earliestDate')) result.earliestDate = validation.earliestDate;
  if (keep.includes('latestDate')) result.latestDate = validation.latestDate;
  if (keep.includes('regexPattern')) result.regexPattern = validation.regexPattern;
  if (keep.includes('allowedFileTypes')) result.allowedFileTypes = validation.allowedFileTypes;
  if (keep.includes('maxFileSizeBytes')) result.maxFileSizeBytes = validation.maxFileSizeBytes;

  return result;
}

/** displayOrder := array index, densely from 0, on every reorder and again on submit. */
export function renumber(fields: readonly FieldFormValues[]): FieldFormValues[] {
  return fields.map((field, index) => ({ ...field, displayOrder: index }));
}

// -------------------------------------------------------------------------------------------------
// Loading a detail response into form state.
// -------------------------------------------------------------------------------------------------

/**
 * THE WHOLE FIELD ARRAY, ALWAYS. `/edit` builds the new version's descriptors from `req.Fields` and
 * nothing else (EditTicketTypeHandler.cs:51-56), so a row missing from form state is a row missing
 * from the next version, with a 200 OK and no warning anywhere. Never populate this from a subset,
 * never lazy-load rows behind a closed accordion, and never build the payload from RHF's
 * dirtyFields.
 *
 * A member that arrives as `''` (regexPattern) or `[]` (allowedFileTypes) means "no rule" and loads
 * as the empty control. `conditionalVisibility` arrives as null when the stored fieldKey is blank
 * (TicketTypeMapper.cs:278-284), which becomes `enabled: false` with both boxes empty.
 */
export function toFormValues(detail: TicketTypeDetail): TicketTypeFormValues {
  return {
    code: detail.code,
    displayName: detail.displayName,
    description: detail.description,
    category: detail.category,
    allowEmployeeToOpen: detail.allowEmployeeToOpen,
    allowSubjectOtherThanCreator: detail.allowSubjectOtherThanCreator,
    // Renumbered on load as well as on submit: the response is already sorted by display_order
    // (TicketTypeMapper.cs:249) but the numbers themselves can be sparse or duplicated, and the
    // reorder buttons write indices.
    fields: renumber(
      detail.fields.map((field) => ({
        key: field.key,
        label: field.label,
        helpText: field.helpText,
        dataType: field.dataType,
        displayOrder: field.displayOrder,
        groupName: field.groupName,
        isRequired: field.isRequired,
        isVisibleToCustomer: field.isVisibleToCustomer,
        choiceOptions: field.choiceOptions.map((option) => ({
          label: option.label,
          value: option.value,
        })),
        validation: {
          minLength: field.validation.minLength ?? null,
          maxLength: field.validation.maxLength ?? null,
          minValue: field.validation.minValue ?? null,
          maxValue: field.validation.maxValue ?? null,
          earliestDate: field.validation.earliestDate ?? '',
          latestDate: field.validation.latestDate ?? '',
          regexPattern: field.validation.regexPattern,
          allowedFileTypes: field.validation.allowedFileTypes.join(', '),
          maxFileSizeBytes: field.validation.maxFileSizeBytes ?? null,
        },
        conditionalVisibility: {
          enabled: field.conditionalVisibility !== null,
          fieldKey: field.conditionalVisibility?.fieldKey ?? '',
          value: field.conditionalVisibility?.value ?? '',
        },
      })),
    ),
  };
}

// -------------------------------------------------------------------------------------------------
// The Zod schema.
// -------------------------------------------------------------------------------------------------

/**
 * A number control's empty state. `valueAsNumber` is NaN when the box is cleared, and NaN must read
 * as "no rule" and not as a type error the author cannot act on -- there is nothing wrong with an
 * empty optional bound. `z.number()` rejects NaN and Infinity outright (zod/v4/core/schemas.cjs:627),
 * so the NaN case has to be caught explicitly.
 *
 * A UNION AND NOT `z.preprocess`, DELIBERATELY, AND THE REASON IS A TYPE AND NOT A TASTE.
 * `z.preprocess` types its INPUT as `unknown`, which makes the schema's input type differ from
 * `TicketTypeFormValues` -- and RHF's `useForm<TFieldValues>` requires
 * `Resolver<TFieldValues, unknown, TFieldValues>`, so an `unknown` leaf makes the resolver
 * unassignable and every component typed on `Control<TicketTypeFormValues>` disagree with the form
 * that provides it. A union of `number | null | NaN` keeps input and output both `number | null`,
 * which is exactly what the interface declares.
 */
const nullableNumber = (label: string) =>
  z.union([z.number(), z.null(), z.nan().transform(() => null)], {
    error: `${label} must be a number.`,
  });

/**
 * `.int()` MIRRORS THE WIRE TYPE, NOT A BUSINESS RULE. MinLength and MaxLength are `int?` and
 * MaxFileSizeBytes is `long?` (TicketTypeDetailDto.cs:72-73, 80), and System.Text.Json rejects
 * `1.5` for an int with a JsonException, which the framework returns as a 400 with wording no
 * screen can improve on. There is deliberately no `.min(0)`: the server accepts a negative bound,
 * and section 9.2 forbids a client rule the server does not have.
 */
const nullableInteger = (label: string) =>
  z.union([z.number().int(), z.null(), z.nan().transform(() => null)], {
    error: `${label} must be a whole number.`,
  });

const ISO_DATE = /^\d{4}-\d{2}-\d{2}$/;

/**
 * '' or "yyyy-MM-dd", and nothing else. A C# `DateOnly?` accepts null or that exact shape; an empty
 * string reaches the model binder as a JsonException and a 400, so `toValidationRequest` converts ''
 * to null and this refusal keeps a half-typed date from ever getting that far.
 */
const isoDateOrBlank = z
  .string()
  .refine((value) => value === '' || ISO_DATE.test(value), 'Enter a date, or leave this empty.');

const choiceOptionSchema = z.object({
  // Trimmed here because the server trims neither: a choice option is stored as JSON
  // (TicketTypeMapper.cs:62) and NormalizeFields never looks inside it. An option whose value is
  // " yes " can never be matched by a conditionalVisibility rule an author would type.
  label: z.string().trim(),
  value: z.string().trim().min(1, 'Every option needs a value.'),
});

const validationSchema = z
  .object({
    minLength: nullableInteger('Minimum length'),
    maxLength: nullableInteger('Maximum length'),
    // A C# `decimal?` (NUMERIC(18,4)), so a fractional bound is legitimate here and .int() is not.
    minValue: nullableNumber('Minimum value'),
    maxValue: nullableNumber('Maximum value'),
    earliestDate: isoDateOrBlank,
    latestDate: isoDateOrBlank,
    /**
     * NEVER TRIMMED. A leading or trailing space is meaningful inside a regular expression, and the
     * server does not trim it either -- so trimming here would silently change the rule the author
     * wrote and store a pattern that matches different strings.
     */
    regexPattern: z.string().max(REGEX_PATTERN_MAX, `Use at most ${String(REGEX_PATTERN_MAX)} characters.`),
    allowedFileTypes: z.string(),
    maxFileSizeBytes: nullableInteger('Maximum file size'),
  })
  .superRefine((validation, ctx) => {
    /**
     * THE THREE RANGE CHECKS, exactly as TicketTypeMapper.cs:186-190 writes them: each fires only
     * when BOTH ends are present, because in C# `null > x` is false. The server reports all three
     * with one message -- "Invalid validation range for field 'x'." -- which names no bound, so the
     * client's messages are per-bound and attached to the box that is wrong.
     */
    if (present(validation.minLength) && present(validation.maxLength)) {
      if (validation.minLength > validation.maxLength) {
        ctx.addIssue({
          code: 'custom',
          path: ['maxLength'],
          message: 'The maximum length must be at least the minimum length.',
        });
      }
    }
    if (present(validation.minValue) && present(validation.maxValue)) {
      if (validation.minValue > validation.maxValue) {
        ctx.addIssue({
          code: 'custom',
          path: ['maxValue'],
          message: 'The maximum value must be at least the minimum value.',
        });
      }
    }
    // ISO date strings compare correctly as strings. No Date is constructed -- `new Date("2026-09-02")`
    // is midnight UTC and is the previous day west of it (GeneralUIArchitecture.md section 10.2).
    if (validation.earliestDate !== '' && validation.latestDate !== '') {
      if (validation.earliestDate > validation.latestDate) {
        ctx.addIssue({
          code: 'custom',
          path: ['latestDate'],
          message: 'The latest date must be on or after the earliest date.',
        });
      }
    }

    /**
     * THE JOINED LENGTH, NOT EACH ENTRY. `RequireLength(string.Join(',', AllowedFileTypes), 500)`
     * (TicketTypeMapper.cs:174-176). A client that checked each extension separately would accept
     * sixty short ones and earn a 422 naming a limit every individual value is inside.
     */
    if (joinFileTypes(validation.allowedFileTypes).length > ALLOWED_FILE_TYPES_MAX) {
      ctx.addIssue({
        code: 'custom',
        path: ['allowedFileTypes'],
        message: `The whole list must be at most ${String(ALLOWED_FILE_TYPES_MAX)} characters once joined.`,
      });
    }

    /**
     * THE PATTERN MUST COMPILE IN THIS BROWSER, not only in .NET. `ValidateRegexCompiles`
     * (TicketTypeMapper.cs:210-224) proves it compiles with `new Regex`, and the dialects differ:
     * .NET accepts inline options `(?i)`, conditionals, atomic groups, balancing groups, `\Z`,
     * `(?#comments)` and `\p{IsGreek}`, every one of which throws SyntaxError in `new RegExp`.
     * Without this check the pattern saves, and then every ticket form built on it drops the rule
     * with a console warning nobody reads. Screen spec section 5.5 rule G.
     */
    if (validation.regexPattern !== '' && !compilesInBrowser(validation.regexPattern)) {
      ctx.addIssue({
        code: 'custom',
        path: ['regexPattern'],
        message: 'This is not a valid regular expression.',
      });
    }
  });

const fieldSchema = z
  .object({
    /**
     * TRIMMED FIRST, THEN LENGTH-CHECKED AND BLANK-CHECKED, because the trimmed value is what we
     * send and therefore what the server checks. See the header for why the trim is load-bearing.
     */
    key: z
      .string()
      .trim()
      .min(1, 'A field key is required.')
      .max(FIELD_KEY_MAX, `Use at most ${String(FIELD_KEY_MAX)} characters.`),
    label: z.string().trim().max(FIELD_LABEL_MAX, `Use at most ${String(FIELD_LABEL_MAX)} characters.`),
    // Capped at a limit the server declares and never enforces. See HELP_TEXT_MAX.
    helpText: z.string().trim().max(HELP_TEXT_MAX, `Use at most ${String(HELP_TEXT_MAX)} characters.`),
    /**
     * ONE OF THE ELEVEN. `ValidateFields` rejects anything else with 422 "Unknown field data type
     * 'x'." (TicketTypeMapper.cs:167) and the column carries a CHECK constraint naming the same
     * eleven, so the comparison is ordinal and case-sensitive -- "yesno" would write a row the
     * constraint rejects.
     *
     * A REFINED string RATHER THAN z.enum, so that the schema's input type stays `string` and matches
     * FieldFormValues. A narrowed input type would make the form unable to hold a dataType the server
     * sent but this bundle does not know -- which the CHECK constraint makes unlikely and does not
     * make impossible -- and the failure mode would be a cast rather than the row-level error below.
     */
    dataType: z.string().refine(isKnownDataType, 'Choose a data type.'),
    displayOrder: z.number().int(),
    groupName: z.string().trim().max(GROUP_NAME_MAX, `Use at most ${String(GROUP_NAME_MAX)} characters.`),
    isRequired: z.boolean(),
    isVisibleToCustomer: z.boolean(),
    choiceOptions: z.array(choiceOptionSchema),
    validation: validationSchema,
    conditionalVisibility: z.object({
      enabled: z.boolean(),
      fieldKey: z.string().trim().max(FIELD_KEY_MAX),
      /**
       * NO MINIMUM. `''` is a rule the server accepts and one the renderer can satisfy -- "show
       * this field while the controller is blank" -- so requiring a value here would be a client
       * limit stricter than the server's.
       */
      value: z.string().max(CONDITIONAL_VALUE_MAX, `Use at most ${String(CONDITIONAL_VALUE_MAX)} characters.`),
    }),
  })
  .superRefine((field, ctx) => {
    /**
     * BOTH DIRECTIONS OF THE CHOICE-OPTIONS RULE, both of which are a 422
     * (TicketTypeMapper.cs:180-184). The second is the one that bites, and it is prevented in the
     * data-type Select's own handler (applyDataTypeChange) as well as reported here -- by the time
     * a banner says "Non-choice field 'x' cannot have choice options." the author has already fixed
     * the thing the message names.
     */
    const isChoice = isChoiceDataType(field.dataType);
    if (isChoice && field.choiceOptions.length < 2) {
      ctx.addIssue({
        code: 'custom',
        path: ['choiceOptions'],
        message: 'A choice field needs at least two options.',
      });
    }
    if (!isChoice && field.choiceOptions.length > 0) {
      ctx.addIssue({
        code: 'custom',
        path: ['choiceOptions'],
        message: 'Only a single-choice or multiple-choice field can have options.',
      });
    }

    // Duplicate option VALUES, not labels: the value is what a conditionalVisibility rule compares
    // against and what a stored answer holds, so two options sharing one value are indistinguishable
    // in every ticket ever raised. The server checks neither.
    const seen = new Set<string>();
    field.choiceOptions.forEach((option, index) => {
      if (seen.has(option.value)) {
        ctx.addIssue({
          code: 'custom',
          path: ['choiceOptions', index, 'value'],
          message: 'Two options cannot share one value.',
        });
      }
      seen.add(option.value);
    });

    if (field.conditionalVisibility.enabled && field.conditionalVisibility.fieldKey === '') {
      ctx.addIssue({
        code: 'custom',
        path: ['conditionalVisibility', 'fieldKey'],
        message: 'Choose the field this one depends on.',
      });
    }
  });

/**
 * THE WHOLE FORM. `code` is validated in both modes and SENT ONLY BY toCreateRequest -- see there.
 *
 * The cross-row rules are here because they need the whole array: key uniqueness, and the
 * conditional-visibility reference, which must name a DIFFERENT row that exists.
 */
export const ticketTypeFormSchema = z
  .object({
    /**
     * Non-blank and <= 100. The blank check is CreateTicketTypeHandler.cs:38-39 (`IsNullOrWhiteSpace`,
     * 422 "Ticket type code is required.") rather than `ValidateTicketType`, which length-checks the
     * code and never blank-checks it -- because `/edit` passes `string.Empty` where the code would
     * go (EditTicketTypeHandler.cs:41) and a blank check there would refuse every edit.
     */
    code: z
      .string()
      .trim()
      .min(1, 'A code is required.')
      .max(CODE_MAX, `Use at most ${String(CODE_MAX)} characters.`),
    displayName: z
      .string()
      .trim()
      .min(1, 'A display name is required.')
      .max(DISPLAY_NAME_MAX, `Use at most ${String(DISPLAY_NAME_MAX)} characters.`),
    // Trimmed although the server does not trim it: the value we send is the value it length-checks,
    // so trim-then-check mirrors it exactly.
    description: z.string().trim().max(DESCRIPTION_MAX, `Use at most ${String(DESCRIPTION_MAX)} characters.`),
    category: z
      .string()
      .trim()
      .min(1, 'A category is required.')
      .max(CATEGORY_MAX, `Use at most ${String(CATEGORY_MAX)} characters.`),
    allowEmployeeToOpen: z.boolean(),
    allowSubjectOtherThanCreator: z.boolean(),
    /**
     * AT LEAST ONE ROW. `ValidateFields` returns 422 "At least one field is required."
     * (TicketTypeMapper.cs:152-153), and an author who composes a nine-field type, deletes them all
     * by mistake and learns from a banner has lost the work.
     */
    fields: z.array(fieldSchema).min(1, 'A ticket type needs at least one field.'),
  })
  .superRefine((values, ctx) => {
    /**
     * CASE-INSENSITIVE KEY UNIQUENESS, matching `new HashSet<string>(StringComparer.OrdinalIgnoreCase)`
     * (TicketTypeMapper.cs:155). The comparison is on the TRIMMED keys, which is the client's
     * addition -- server-side the set is whitespace-sensitive, so `"key"` and `"key "` are two rows
     * it accepts. The trim above is what prevents that; this is what prevents the case collision the
     * server does catch, with the error attached to the row instead of in a banner.
     */
    const firstIndexByKey = new Map<string, number>();
    values.fields.forEach((field, index) => {
      const folded = field.key.toLowerCase();
      if (field.key === '') return; // already reported by the row's own min(1)
      if (firstIndexByKey.has(folded)) {
        ctx.addIssue({
          code: 'custom',
          path: ['fields', index, 'key'],
          message: 'Another field already uses this key. Keys are compared without case.',
        });
        return;
      }
      firstIndexByKey.set(folded, index);
    });

    /**
     * THE REFERENCE MUST NAME A DIFFERENT ROW THAT EXISTS, both checks case-insensitive, matching
     * TicketTypeMapper.cs:193-198. The field Select offers only the other rows, so neither should be
     * reachable through the UI -- they are here because a typo is a guaranteed round trip and
     * because a Select's value can go stale when the row it named is renamed or removed. That last
     * case is the real one: rename a controller and every rule pointing at it becomes dangling, and
     * the server's answer is a 422 in a banner.
     */
    values.fields.forEach((field, index) => {
      const rule = field.conditionalVisibility;
      if (!rule.enabled || rule.fieldKey === '') return;

      const folded = rule.fieldKey.toLowerCase();
      if (folded === field.key.toLowerCase()) {
        ctx.addIssue({
          code: 'custom',
          path: ['fields', index, 'conditionalVisibility', 'fieldKey'],
          message: 'A field cannot depend on itself.',
        });
        return;
      }
      if (!firstIndexByKey.has(folded)) {
        ctx.addIssue({
          code: 'custom',
          path: ['fields', index, 'conditionalVisibility', 'fieldKey'],
          message: 'No field in this form has that key.',
        });
      }
    });
  });

// -------------------------------------------------------------------------------------------------
// The request builders.
// -------------------------------------------------------------------------------------------------

/**
 * BOTH BOOLEANS ARE ALWAYS SENT, ON BOTH ROUTES. EditTicketTypeRequestDto.cs:9-10 declares
 * AllowEmployeeToOpen and AllowSubjectOtherThanCreator with NO initialiser, so both default to
 * `false`, while CreateTicketTypeRequestDto.cs:11-12 declares the same two `= true`. An edit payload
 * that OMITS either flag therefore turns it OFF -- and turning allowEmployeeToOpen off hides the type
 * from every Employee's list and returns 404 on their reads (ListTicketTypesHandler.cs:32-33;
 * TicketTypeMapper.cs:30-31). A whole role loses a whole type, from a property nobody typed. This is
 * why neither builder spreads a partial object and why neither boolean is optional in the request
 * types.
 */
export function toCreateRequest(values: TicketTypeFormValues): CreateTicketTypeRequest {
  return {
    code: values.code,
    displayName: values.displayName,
    // '' and never null. The C# property is a non-nullable string reaching a NOT NULL column, and
    // nullable reference types are not enforced at runtime -- so a null would be assigned, survive
    // normalisation, hit the column and come back as a bare 500. See toFieldRequest.
    description: values.description,
    category: values.category,
    allowEmployeeToOpen: values.allowEmployeeToOpen,
    allowSubjectOtherThanCreator: values.allowSubjectOtherThanCreator,
    fields: values.fields.map(toFieldRequest),
  };
}

/**
 * NO `code`. EditTicketTypeRequestDto.cs:5-11 has no Code property, and `System.Text.Json`'s default
 * binding ignores an unknown one -- so sending it produces no 400, no warning and no change. A form
 * that let the author edit it would accept the edit, report success, and show the old value again
 * once the cache is seeded from the response, which reads as a save that silently failed.
 */
export function toEditRequest(
  ticketTypeId: string,
  values: TicketTypeFormValues,
): EditTicketTypeRequest {
  return {
    ticketTypeId,
    displayName: values.displayName,
    description: values.description,
    category: values.category,
    allowEmployeeToOpen: values.allowEmployeeToOpen,
    allowSubjectOtherThanCreator: values.allowSubjectOtherThanCreator,
    /**
     * EVERY ROW, EVERY TIME. `/edit` builds the new version's descriptors from req.Fields and never
     * reads the previous version's (EditTicketTypeHandler.cs:51-56), so a row omitted here is a row
     * deleted from v-next with a 200 OK and no warning anywhere.
     */
    fields: values.fields.map(toFieldRequest),
  };
}

/**
 * `label`, `helpText` and `groupName` are sent as `''` and NEVER null, for the same reason as
 * `description`: all three are non-nullable C# strings, `NormalizeFields` calls `.Trim()` on Label
 * and GroupName unconditionally (TicketTypeMapper.cs:121-122) -- a null there is a
 * NullReferenceException and a bare 500 -- and HelpText reaches a NOT NULL column. This is the
 * documented exception to GeneralUIArchitecture.md section 9.3 rule F ("send null, not ''"): rule F
 * still governs choiceOptions, validation, conditionalVisibility and the seven nullable members of
 * FieldValidationDto, where null genuinely means "no rule".
 */
function toFieldRequest(field: FieldFormValues, index: number): CreateFieldDescriptor {
  const isChoice = isChoiceDataType(field.dataType);
  const validation = toValidationRequest(field.validation, field.dataType);

  return {
    key: field.key,
    label: field.label,
    helpText: field.helpText,
    dataType: field.dataType,
    // The ARRAY INDEX, densely from 0, recomputed here rather than trusted from form state -- see
    // FieldFormValues.displayOrder.
    displayOrder: index,
    groupName: field.groupName,
    isRequired: field.isRequired,
    isVisibleToCustomer: field.isVisibleToCustomer,
    // null, not [], for a non-choice field: `ChoiceOptions is { Count: > 0 }` is the server's test
    // (TicketTypeMapper.cs:184), so [] would pass too -- null is sent because it is what "this field
    // has no options" means, and ToEntity serialises `?? []` either way (:62).
    choiceOptions: isChoice ? field.choiceOptions.map((o) => ({ label: o.label, value: o.value })) : null,
    validation,
    conditionalVisibility: field.conditionalVisibility.enabled
      ? {
          fieldKey: field.conditionalVisibility.fieldKey,
          value: field.conditionalVisibility.value,
        }
      : null,
  };
}

/**
 * null WHEN THE FIELD HAS NO RULES AT ALL, and otherwise only the members this data type can use.
 * ToEntity reads every member through `field.Validation?.` (TicketTypeMapper.cs:63-71), so null is
 * safe and is what "no rules" means.
 *
 * DROPPING THE INAPPLICABLE MEMBERS IS THE POINT. The server stores whatever it is given without
 * cross-checking it against the data type, so a `minValue` left on a text field after a data-type
 * change would persist, mean nothing, and be applied by a future renderer that trusts it.
 */
function toValidationRequest(
  validation: ValidationFormValues,
  dataType: string,
): FieldValidationRequest | null {
  const kept = clearInapplicableValidation(validation, dataType);
  const allowedFileTypes = splitFileTypes(kept.allowedFileTypes);

  const request: FieldValidationRequest = {
    minLength: kept.minLength,
    maxLength: kept.maxLength,
    minValue: kept.minValue,
    maxValue: kept.maxValue,
    // '' becomes null: a C# DateOnly? takes null or "yyyy-MM-dd", and an empty string is a
    // JsonException and a 400 with framework wording.
    earliestDate: kept.earliestDate === '' ? null : kept.earliestDate,
    latestDate: kept.latestDate === '' ? null : kept.latestDate,
    // RegexPattern and AllowedFileTypes are the two NON-NULLABLE members of FieldValidationDto
    // (TicketTypeDetailDto.cs:78-79), with '' and [] defaults: send those, never null.
    regexPattern: kept.regexPattern,
    allowedFileTypes,
    maxFileSizeBytes: kept.maxFileSizeBytes,
  };

  const empty =
    !present(request.minLength) &&
    !present(request.maxLength) &&
    !present(request.minValue) &&
    !present(request.maxValue) &&
    request.earliestDate === null &&
    request.latestDate === null &&
    request.regexPattern === '' &&
    allowedFileTypes.length === 0 &&
    !present(request.maxFileSizeBytes);

  return empty ? null : request;
}

/** Split on commas, trim each entry, drop the empties -- exactly ToDetail's own split (:273-275). */
function splitFileTypes(value: string): string[] {
  return value
    .split(',')
    .map((entry) => entry.trim())
    .filter((entry) => entry !== '');
}

/** The string the server length-checks: `string.Join(',', AllowedFileTypes)`. */
function joinFileTypes(value: string): string {
  return splitFileTypes(value).join(',');
}

/** A `0` IS a bound. Tests against null and undefined, never for truthiness. */
function present(value: number | null | undefined): value is number {
  return value !== null && value !== undefined;
}

/**
 * Compiled in a try/catch, with no `u` flag (it makes previously valid patterns throw) and no `g`
 * (a stateful lastIndex makes `.test` alternate on identical input). The result is discarded: this
 * asks only whether `new RegExp` accepts it, and it is never run against a value, so no
 * catastrophic-backtracking bound is needed here -- the renderer's REGEX_INPUT_CEILING is what
 * bounds the running of it.
 */
function compilesInBrowser(pattern: string): boolean {
  try {
    new RegExp(pattern);
    return true;
  } catch {
    return false;
  }
}

/** The eleven strings, for the data-type Select. Re-exported so the editor has one import site. */
export { FIELD_DATA_TYPES, CHOICE_DATA_TYPES };
