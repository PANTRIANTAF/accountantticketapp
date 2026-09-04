import * as z from 'zod';
import { formatDate } from '../format/dates';
import type { FieldDescriptor } from './types';

/**
 * THE ZOD SCHEMA FOR ONE VERSION'S VISIBLE FIELDS. Screens/TicketTypesScreens.md section 6.4,
 * implemented from its ten-row table and its four numbered rules.
 *
 * IT TAKES THE VISIBLE SET, AND THAT IS THE STRUCTURAL FIX FOR THE WORST BUG IN THIS RENDERER
 * (section 6.5 rule 4 / plan section 6.9 item 1). A hidden field with isRequired: true would otherwise
 * contribute a required key: RHF's resolver fails, handleSubmit never calls onSubmit, and the error
 * attaches to a control that is NOT RENDERED. Submit then does nothing at all -- no request, no
 * banner, no red outline, nothing in the console -- and the user presses it repeatedly. It is
 * unreportable, because from their side the button is simply broken. Hidden fields are therefore
 * OMITTED FROM THE SCHEMA ENTIRELY, and the schema is rebuilt whenever visibility changes.
 *
 * THE KEYS OF THE RETURNED OBJECT ARE ALIASES (f0, f1, ...), NOT FIELD KEYS. React Hook Form parses
 * `.` and `[` in a name as path syntax, and the server accepts ANY non-blank string of <= 100
 * characters as a key -- TicketTypeMapper.cs:158 checks blankness and length and nothing about the
 * character set. A key `salary.amount` registered directly becomes nested state
 * `{ salary: { amount } }` and submits under the wrong shape, with nothing erroring. Both this module
 * and DynamicForm derive the alias from the field's INDEX in the same array, through `fieldAlias`
 * below, so the two agree by construction. Punch-list item 19.
 *
 * MIRROR THE SERVER'S LIMITS EXACTLY (GeneralUIArchitecture.md section 9.2). Every limit in the table
 * is enforced at TicketTypeMapper.cs:150-199 and returns a 422. A client limit STRICTER than the
 * server's blocks legitimate input; a LOOSER one produces an unattributable banner, because
 * ProblemDetails here carries no field map. REGEX_INPUT_CEILING is the one deliberate exception, and
 * it says so where it is declared.
 *
 * RULES THAT DO NOT APPLY TO A DATA TYPE ARE IGNORED, NOT ERRORS (rule 3). `minValue` on a
 * SingleLineText is legal server-side -- ValidateFields never cross-checks a validation member against
 * the data type -- so it WILL occur. The editor is where it is prevented.
 */

/**
 * THE INPUT-LENGTH CEILING FOR A USER-SUPPLIED PATTERN, AND WHY IT IS NOT NEGOTIABLE.
 *
 * A regexPattern is authored by an Accountant, stored in VARCHAR(500), and then compiled and run in
 * the browsers of Customer-side users against values they typed. It may backtrack catastrophically,
 * and JAVASCRIPT HAS NO REGEX TIMEOUT. The backend author saw the same hazard and mitigated it
 * server-side: Shared/Validation/UserSuppliedRegex.cs:41 declares
 * `public static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);` and the class
 * comment above it (UserSuppliedRegex.cs:24-28) names `(a+)+$` and calls it "a request-side denial of
 * service on the whole worker process". There is no RegExp equivalent in any browser: a pattern that
 * hangs the thread hangs THE TAB, with no error, no recovery and nothing in the console.
 *
 * Catastrophic backtracking is exponential in input length, so a hard ceiling is the only bound
 * available without a timeout. A value longer than this FAILS -- it does not silently skip the rule,
 * because skipping would accept unvalidated input while running is what hangs the tab.
 *
 * THIS IS A DELIBERATE EXCEPTION to the mirror-the-server-exactly rule: the column behind an answer is
 * TEXT and the server would accept a 10,000-character value that this client refuses to pattern-check.
 * The exception is stated rather than hidden. The proper fix -- a server-side complexity or length
 * bound on patterned fields, or validation in a terminable Web Worker -- is flagged in the report.
 * Do not remove the ceiling on the grounds that it is stricter than the server.
 */
export const REGEX_INPUT_CEILING = 4096;

/**
 * The RHF field name for the field at `index`. Never the field's `key`; see the header. Both this
 * module and DynamicForm call it, so the mapping cannot drift between them.
 */
export function fieldAlias(index: number): string {
  return `f${String(index)}`;
}

const REQUIRED_MESSAGE = 'This field is required.';

const TEXT_TYPES = new Set(['SingleLineText', 'MultiLineText']);
const NUMBER_TYPES = new Set(['WholeNumber', 'DecimalNumber', 'MoneyAmount']);

/**
 * Hidden fields are absent from the shape. So are FileUpload and any unrecognised dataType, which
 * contribute no rule and no value at all.
 */
export function buildZodSchema(
  fields: readonly FieldDescriptor[],
  visibleKeys: ReadonlySet<string>,
) {
  const shape: Record<string, z.ZodType> = {};

  fields.forEach((field, index) => {
    if (!visibleKeys.has(field.key)) return;
    const schema = schemaForField(field);
    if (schema !== undefined) shape[fieldAlias(index)] = schema;
  });

  return z.object(shape);
}

function schemaForField(field: FieldDescriptor): z.ZodType | undefined {
  const { dataType } = field;

  if (TEXT_TYPES.has(dataType)) return textSchema(field);
  if (NUMBER_TYPES.has(dataType)) return numberSchema(field);
  if (dataType === 'Date') return dateSchema(field);
  if (dataType === 'DateRange') return dateRangeSchema(field);
  if (dataType === 'YesNo') return yesNoSchema(field);
  if (dataType === 'SingleChoice') return singleChoiceSchema(field);
  if (dataType === 'MultipleChoice') return multipleChoiceSchema(field);

  /**
   * FileUpload contributes NO RULE, NOT EVEN isRequired (section 6.9). A required-but-impossible field
   * would make every ticket of that type unsubmittable the moment a Tickets UI ships. It submits null,
   * always.
   *
   * An unrecognised dataType likewise contributes no rule and no value: the registry renders a visible
   * error placeholder in its position instead. Skipping it silently while KEEPING a required key is
   * exactly the unsubmittable-form bug described in the header.
   */
  return undefined;
}

// -------------------------------------------------------------------------------------------------
// Text
// -------------------------------------------------------------------------------------------------

function textSchema(field: FieldDescriptor): z.ZodType {
  const v = field.validation;
  let schema = z.string();

  // The required check is added FIRST so that on an empty value its message is the first issue, and
  // RHF -- which shows one message per field -- shows "This field is required." rather than a length
  // rule the user has not reached yet.
  if (field.isRequired) schema = schema.min(1, REQUIRED_MESSAGE);
  if (present(v.minLength)) {
    schema = schema.min(v.minLength, `Enter at least ${String(v.minLength)} characters.`);
  }
  if (present(v.maxLength)) {
    schema = schema.max(v.maxLength, `Enter at most ${String(v.maxLength)} characters.`);
  }

  /**
   * COMPILED ONCE, HERE, AND CAPTURED IN THE CLOSURE -- never inside the refinement, which runs on
   * every validation pass. buildZodSchema itself is memoised on [fields, visibleKeys] by DynamicForm.
   */
  const pattern = compilePattern(v.regexPattern, field.key);
  if (pattern !== undefined) {
    schema = schema.superRefine((value, ctx) => {
      if (value.length > REGEX_INPUT_CEILING) {
        // Fails closed on LENGTH, which is safe. Skipping would accept unvalidated input; running
        // would risk hanging the tab. See REGEX_INPUT_CEILING.
        ctx.addIssue('This value is too long to check against the required format.');
        return;
      }
      if (!pattern.test(value)) ctx.addIssue('This value is not in the required format.');
    });
  }

  return field.isRequired ? schema : optionalFrom(schema, isBlankString);
}

/**
 * The server proved this pattern compiles in .NET (TicketTypeMapper.ValidateRegexCompiles,
 * TicketTypeMapper.cs:210-224). IT PROVED NOTHING ABOUT JAVASCRIPT. The dialects differ: .NET accepts
 * inline options (?i), conditionals (?(cond)a|b), atomic groups (?>...), balancing groups, \Z and \z
 * anchors, (?#comments) and \p{IsGreek}. Every one of those throws SyntaxError in `new RegExp`, and an
 * uncaught throw here happens DURING RENDER -- so it takes out the whole form, every field including
 * the ones with no pattern, leaving a blank region with the failure only in the console.
 *
 * On failure the rule is DROPPED and the field stays usable. Never fail closed here: a field whose
 * pattern cannot be compiled would otherwise reject every value with no message that names a cause.
 *
 * No `u` flag -- it makes previously valid patterns throw. No `g` -- a stateful lastIndex makes .test
 * alternate between true and false on identical input.
 */
function compilePattern(pattern: string, key: string): RegExp | undefined {
  // '' means "no rule". `new RegExp('')` matches every string, so treating '' as a rule is harmless
  // but pointless; treating [] as a whitelist, by contrast, would reject everything (section 6.2).
  if (pattern === '') return undefined;
  try {
    return new RegExp(pattern);
  } catch {
    console.warn(`Field "${key}": regexPattern is not a valid JavaScript regular expression.`);
    return undefined;
  }
}

// -------------------------------------------------------------------------------------------------
// Numbers
// -------------------------------------------------------------------------------------------------

function numberSchema(field: FieldDescriptor): z.ZodType {
  const v = field.validation;

  /**
   * A required numeric field carries its message on the TYPE check, because that is the check an empty
   * numeric input fails: TextField type="number" with valueAsNumber yields NaN when cleared, and
   * z.number() rejects NaN as an invalid type. One message covers "nothing entered" and "not a
   * number", which are the same thing to somebody typing into a numeric box.
   */
  let schema = field.isRequired ? z.number({ error: REQUIRED_MESSAGE }) : z.number();

  // WholeNumber rejects a non-integer in Zod, never by masking keystrokes -- masking eats a pasted
  // value and cannot be undone.
  if (field.dataType === 'WholeNumber') schema = schema.int('Enter a whole number.');

  // Arrives as a JSON NUMBER from a C# decimal (NUMERIC(18,4)); never compared as a string.
  if (present(v.minValue)) schema = schema.gte(v.minValue, `Enter ${String(v.minValue)} or more.`);
  if (present(v.maxValue)) schema = schema.lte(v.maxValue, `Enter ${String(v.maxValue)} or less.`);

  return field.isRequired ? schema : optionalFrom(schema, isEmptyNumber);
}

// -------------------------------------------------------------------------------------------------
// Dates. "yyyy-MM-dd" strings, compared AS STRINGS.
// -------------------------------------------------------------------------------------------------

/**
 * ISO date strings compare correctly lexicographically, so no Date is constructed for a bound.
 * Parsing to a Date re-introduces the timezone shift of GeneralUIArchitecture.md section 10.2 --
 * `new Date("2026-09-02")` is midnight UTC and is the previous day west of it -- for no benefit.
 * formatDate is used only to render a bound INTO A MESSAGE, and it formats the parts directly.
 */
function dateSchema(field: FieldDescriptor): z.ZodType {
  const v = field.validation;
  let schema = z.string();

  if (field.isRequired) schema = schema.min(1, REQUIRED_MESSAGE);

  const earliest = v.earliestDate;
  if (earliest) {
    schema = schema.refine(
      (value) => value === '' || value >= earliest,
      `Choose a date on or after ${formatDate(earliest)}.`,
    );
  }
  const latest = v.latestDate;
  if (latest) {
    schema = schema.refine(
      (value) => value === '' || value <= latest,
      `Choose a date on or before ${formatDate(latest)}.`,
    );
  }

  return field.isRequired ? schema : optionalFrom(schema, isBlankString);
}

/**
 * TWO pickers, so two values. The bounds apply to BOTH ends, and `from <= to` is added here because
 * THE SERVER DOES NOT CHECK IT: ValidateFields validates each bound against the field, not the pair a
 * user enters.
 *
 * Issues are added at the object's root so they surface once, under the fieldset's own label, rather
 * than under one of two date boxes the reader must guess between.
 */
function dateRangeSchema(field: FieldDescriptor): z.ZodType {
  const v = field.validation;
  const part = z.string().nullable().optional();

  return z.object({ from: part, to: part }).superRefine((value, ctx) => {
    const from = value.from ?? '';
    const to = value.to ?? '';

    if (field.isRequired && (from === '' || to === '')) {
      ctx.addIssue('Enter both dates.');
      return;
    }

    const earliest = v.earliestDate;
    if (earliest) {
      if (from !== '' && from < earliest) {
        ctx.addIssue(`The start date must be on or after ${formatDate(earliest)}.`);
      }
      if (to !== '' && to < earliest) {
        ctx.addIssue(`The end date must be on or after ${formatDate(earliest)}.`);
      }
    }

    const latest = v.latestDate;
    if (latest) {
      if (from !== '' && from > latest) {
        ctx.addIssue(`The start date must be on or before ${formatDate(latest)}.`);
      }
      if (to !== '' && to > latest) {
        ctx.addIssue(`The end date must be on or before ${formatDate(latest)}.`);
      }
    }

    if (from !== '' && to !== '' && from > to) {
      ctx.addIssue('The end date must be on or after the start date.');
    }
  });
}

// -------------------------------------------------------------------------------------------------
// Yes / No, choices
// -------------------------------------------------------------------------------------------------

/**
 * A RadioGroup, never a Checkbox, so `null` is a real state -- "not answered". A required YesNo
 * therefore fails the TYPE check on null, which is where its message goes.
 */
function yesNoSchema(field: FieldDescriptor): z.ZodType {
  if (field.isRequired) return z.boolean({ error: REQUIRED_MESSAGE });
  return optionalFrom(z.boolean(), (value) => value === null || value === '');
}

/** The option's `value` is what lives in state and what is validated. The label is never compared. */
function singleChoiceSchema(field: FieldDescriptor): z.ZodType {
  const schema = z.string();
  if (field.isRequired) return schema.min(1, REQUIRED_MESSAGE);
  return optionalFrom(schema, isBlankString);
}

function multipleChoiceSchema(field: FieldDescriptor): z.ZodType {
  const schema = z.array(z.string());
  // .min(1) on the ARRAY. A .nonempty() on a boolean or a .min(1) on a string would be the wrong
  // shape here, and the section 6.4 table calls this row out for that reason.
  if (field.isRequired) return schema.min(1, 'Choose at least one option.');
  return optionalFrom(schema, (value) => Array.isArray(value) && value.length === 0);
}

// -------------------------------------------------------------------------------------------------
// Shared helpers
// -------------------------------------------------------------------------------------------------

/**
 * `.optional()` LAST, AND THE EMPTY VALUE TRANSFORMED BEFORE VALIDATION (rule 1).
 * `z.string().max(255).optional()` is right and `.optional().max(255)` does not typecheck. And an
 * OPTIONAL text field carrying a minLength fails on '' unless '' becomes undefined first -- otherwise
 * leaving a field alone trips a rule that was only ever meant to apply to an answer. This is
 * GeneralUIArchitecture.md section 9.3 rule F in schema form: null, not ''.
 */
function optionalFrom(schema: z.ZodType, isEmpty: (value: unknown) => boolean): z.ZodType {
  return z.preprocess(
    (value) => (value === undefined || value === null || isEmpty(value) ? undefined : value),
    schema.optional(),
  );
}

function isBlankString(value: unknown): boolean {
  return typeof value === 'string' && value.trim() === '';
}

/**
 * '' and NaN are both "nothing entered" from a numeric input. Number('') is 0 -- a zero the user never
 * typed and cannot be distinguished from one they did -- which is why the raw value is tested rather
 * than converted (section 6.7 rule F).
 */
function isEmptyNumber(value: unknown): boolean {
  if (typeof value === 'string') return value.trim() === '';
  return typeof value === 'number' && Number.isNaN(value);
}

/**
 * A member is a rule only when it is actually there. `if (field.validation)` is ALWAYS true -- the C#
 * property is `= new()` (TicketTypeDetailDto.cs:57) -- so every member is tested individually. And a
 * `0` IS a rule: minValue 0 and maxLength 0 are real bounds, so this tests against null and undefined
 * rather than for truthiness.
 */
function present(value: number | null | undefined): value is number {
  return value !== null && value !== undefined;
}
