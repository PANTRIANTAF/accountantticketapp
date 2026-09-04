import type { ConditionalVisibility, FieldDescriptor } from './types';

/**
 * WHICH FIELDS ARE CURRENTLY VISIBLE. Screens/TicketTypesScreens.md section 6.5, implemented exactly.
 *
 * A pure function of (fields, values). No React, no imports beyond the types, so it can be reasoned
 * about and exercised on its own -- which is why it is written before buildZodSchema and DynamicForm.
 *
 * `{ fieldKey, value }` means: show this field only while the field named `fieldKey` currently holds a
 * value equal to `value`. THE RULE SIDE IS ALWAYS A STRING -- conditional_value is VARCHAR(500)
 * (20260829_001_CreateTicketTypesSchema.sql:54) -- so every comparison needs a defined coercion of the
 * controller's live value. The eight rows of the section 6.5 table are `matches` below.
 *
 * WHEN A RULE CANNOT BE EVALUATED, EVERY FIELD INVOLVED IS SHOWN, NEVER HIDDEN (rule 3). An
 * unexpectedly visible field is a cosmetic fault the user can see, describe and report. An
 * unexpectedly hidden one is a question nobody was asked, an empty Field Value on a ticket, and no
 * evidence anywhere that something was withheld.
 *
 * WHAT THIS MODULE DOES NOT GUARD AGAINST, ON PURPOSE (rule 1): a `fieldKey` equal to the field's own
 * key, and a `fieldKey` naming no field. TicketTypeMapper.cs:193-198 rejects both with a 422, so
 * neither can be stored. A guard for an impossible state is untested code that hides the real bug if
 * the server check ever regresses. The one branch below for a controller that is not in the array
 * exists because the lookup is typed as possibly-undefined, and it does what rule 3 prescribes --
 * shows the field and reports it -- rather than repairing anything.
 */

/** The hard bound on fixed-point passes. The effective cap is min(fields.length, 32). */
export const MAX_VISIBILITY_PASSES = 32;

export type VisibilityWarningKind =
  /** Two or more fields whose rules point at each other. Storable; see findCycleKeys. */
  | 'cycle'
  /** The controller is a DateRange or a FileUpload: there is no defined coercion. */
  | 'unevaluableController'
  /** The controller's dataType is not one of the eleven. A newer server, an older bundle. */
  | 'unknownController'
  /** The controller is not in the array at all. A 422 server-side, so this should be unreachable. */
  | 'missingController'
  /** The pass cap was reached without a fixed point. Should be unreachable once cycles are excluded. */
  | 'capReached';

export interface VisibilityWarning {
  kind: VisibilityWarningKind;
  /** The field keys involved, for a console.warn and for the preview's Alert. */
  keys: string[];
  message: string;
}

export interface VisibilityResult {
  /** Every key that should be rendered. A field with no rule is always in here. */
  visibleKeys: Set<string>;
  /**
   * Reported, never thrown and never logged from here: this function is pure, and it runs on every
   * value change, so logging inside it would print the same line on every keystroke. DynamicForm
   * console.warns these once per distinct set and renders them in mode="preview".
   */
  warnings: VisibilityWarning[];
}

/** The two data types with no defined coercion, from the last two rows of the section 6.5 table. */
const UNEVALUABLE_CONTROLLER_TYPES = new Set(['DateRange', 'FileUpload']);

const NUMERIC_CONTROLLER_TYPES = new Set(['WholeNumber', 'DecimalNumber', 'MoneyAmount']);

const TEXT_CONTROLLER_TYPES = new Set(['SingleLineText', 'MultiLineText']);

export function computeVisibility(
  fields: readonly FieldDescriptor[],
  values: Record<string, unknown>,
): VisibilityResult {
  const byKey = new Map(fields.map((field) => [field.key, field]));
  const warnings: VisibilityWarning[] = [];

  /**
   * CYCLES ARE EXCLUDED BEFORE THE LOOP RUNS, WHICH IS WHAT GUARANTEES TERMINATION.
   * A cycle is storable: TicketTypeMapper.cs:193-198 checks only that each fieldKey differs from its
   * own key and names a field in the request, so `A -> B, B -> A` passes validation intact. Their
   * members are forced visible and their rules are never evaluated -- rule 3.
   */
  const cycleKeys = findCycleKeys(fields);
  if (cycleKeys.size > 0) {
    const keys = [...cycleKeys].sort();
    warnings.push({
      kind: 'cycle',
      keys,
      message: `These fields' visibility rules refer to each other in a loop, so all of them are shown: ${keys.join(', ')}.`,
    });
  }

  // A field with no rule is unconditionally visible, and so is every member of a cycle.
  const visible = new Set<string>();
  for (const field of fields) {
    if (field.conditionalVisibility === null || cycleKeys.has(field.key)) visible.add(field.key);
  }

  const conditional = fields.filter(
    (field) => field.conditionalVisibility !== null && !cycleKeys.has(field.key),
  );

  /**
   * CHAINS ARE REAL: A shows B, B shows C is legal and normal, and one pass would leave C's answer
   * depending on a stale reading of B. Hence a fixed point -- and a CAPPED one, because an uncapped
   * loop over a cycle is an infinite render (rule 2).
   */
  const cap = Math.min(fields.length, MAX_VISIBILITY_PASSES);
  let settled = cap === 0;

  for (let pass = 0; pass < cap; pass += 1) {
    let changed = false;

    for (const field of conditional) {
      const rule = field.conditionalVisibility;
      if (rule === null) continue; // Filtered out above; narrows the type.

      const shown = evaluate(field, rule, byKey, values, visible, warnings);
      if (shown && !visible.has(field.key)) {
        visible.add(field.key);
        changed = true;
      } else if (!shown && visible.has(field.key)) {
        visible.delete(field.key);
        changed = true;
      }
    }

    if (!changed) {
      settled = true;
      break;
    }
  }

  if (!settled) {
    // Unreachable while cycles are excluded above; reported rather than ignored, because the
    // alternative to reporting it is a form that quietly settles on whichever pass ran last.
    const keys = conditional.map((field) => field.key);
    warnings.push({
      kind: 'capReached',
      keys,
      message: `Visibility rules did not settle after ${cap} passes; the fields involved are shown.`,
    });
    for (const key of keys) visible.add(key);
  }

  return { visibleKeys: visible, warnings: dedupe(warnings) };
}

function evaluate(
  field: FieldDescriptor,
  rule: ConditionalVisibility,
  byKey: Map<string, FieldDescriptor>,
  values: Record<string, unknown>,
  visible: Set<string>,
  warnings: VisibilityWarning[],
): boolean {
  const controller = byKey.get(rule.fieldKey);

  if (controller === undefined) {
    warnings.push({
      kind: 'missingController',
      keys: [field.key, rule.fieldKey],
      message: `“${field.key}” depends on “${rule.fieldKey}”, which is not in this form, so it is shown.`,
    });
    return true;
  }

  /**
   * A FIELD WHOSE CONTROLLER IS ITSELF HIDDEN IS HIDDEN. A rule on an invisible question can never be
   * satisfied by an answer nobody was asked for (rule 2's pseudocode). This is the one place a field
   * is hidden for a reason other than its own rule failing, and it is not an unevaluable case: the
   * controller's state is known, and it is "not asked".
   */
  if (!visible.has(controller.key)) return false;

  const outcome = matches(controller, rule, values);

  if (outcome === 'unevaluable') {
    warnings.push({
      kind: UNEVALUABLE_CONTROLLER_TYPES.has(controller.dataType)
        ? 'unevaluableController'
        : 'unknownController',
      keys: [field.key, controller.key],
      message: `“${field.key}” depends on “${controller.key}”, whose type (${controller.dataType}) cannot be compared, so it is shown.`,
    });
    return true;
  }

  return outcome;
}

/**
 * THE EIGHT-ROW COERCION TABLE of section 6.5, in the table's own order. Returns 'unevaluable' for the
 * two comparable-by-nothing types and for an unrecognised dataType -- never `false`, because `false`
 * would hide the dependent field.
 */
function matches(
  controller: FieldDescriptor,
  rule: ConditionalVisibility,
  values: Record<string, unknown>,
): boolean | 'unevaluable' {
  const value = values[controller.key];
  const { dataType } = controller;

  // The string as-is: `===`, case-sensitive, after trimming BOTH sides.
  if (TEXT_CONTROLLER_TYPES.has(dataType)) {
    return typeof value === 'string' && value.trim() === rule.value.trim();
  }

  /**
   * THE RULE IS COERCED WITH Number(), NEVER THE VALUE WITH String().
   * String(1.50) is "1.5", so a rule an author wrote as "1.50" would never match a value the user
   * entered as 1.50 -- a rule that is silently, permanently unsatisfiable. Both sides must parse to a
   * finite number; otherwise they are never equal (NaN === NaN is false, but this is explicit so the
   * reason is on the page).
   */
  if (NUMERIC_CONTROLLER_TYPES.has(dataType)) {
    if (typeof value !== 'number' || !Number.isFinite(value)) return false;
    const ruleNumber = Number(rule.value);
    return Number.isFinite(ruleNumber) && value === ruleNumber;
  }

  // "true" / "false", lower-case. null -- unanswered -- never matches.
  if (dataType === 'YesNo') {
    if (typeof value !== 'boolean') return false;
    return (value ? 'true' : 'false') === rule.value;
  }

  // The "yyyy-MM-dd" string, compared as a string. No Date is constructed: see buildZodSchema.
  if (dataType === 'Date') {
    return typeof value === 'string' && value === rule.value;
  }

  // The selected option's VALUE. Never the label -- the label is what an author renamed last week.
  if (dataType === 'SingleChoice') {
    return typeof value === 'string' && value === rule.value;
  }

  // array.includes -- not `===`, and not `join()`, which would make ["a","b"] match a rule "a,b".
  if (dataType === 'MultipleChoice') {
    return Array.isArray(value) && value.includes(rule.value);
  }

  // DateRange and FileUpload have no defined coercion; an unknown dataType has no coercion at all.
  return 'unevaluable';
}

/**
 * THE KEYS IN EVERY CYCLE. Section 6.5 rule 2 asks for the strongly-connected components of the
 * `key -> fieldKey` edge list with size > 1.
 *
 * A field carries AT MOST ONE conditionalVisibility, so out-degree is at most 1 and the graph is
 * FUNCTIONAL. In a functional graph every SCC of size > 1 is a simple cycle, and every one of them is
 * found by walking each node forward until the walk meets a node it has already seen on this walk --
 * so this is Tarjan's result without Tarjan's machinery, and it is linear.
 */
function findCycleKeys(fields: readonly FieldDescriptor[]): Set<string> {
  const edges = new Map<string, string>();
  for (const field of fields) {
    if (field.conditionalVisibility !== null) {
      edges.set(field.key, field.conditionalVisibility.fieldKey);
    }
  }

  const cycleKeys = new Set<string>();
  const settled = new Set<string>();

  for (const start of edges.keys()) {
    if (settled.has(start)) continue;

    const path: string[] = [];
    const positionInPath = new Map<string, number>();
    let node: string | undefined = start;

    while (node !== undefined && !settled.has(node)) {
      const seenAt = positionInPath.get(node);
      if (seenAt !== undefined) {
        // Everything from the first sighting onward is on the cycle; anything before it merely leads
        // into the cycle and is not part of it.
        for (const key of path.slice(seenAt)) cycleKeys.add(key);
        break;
      }
      positionInPath.set(node, path.length);
      path.push(node);
      node = edges.get(node);
    }

    for (const key of path) settled.add(key);
  }

  return cycleKeys;
}

/** One line per distinct message: the same unevaluable controller is reported once, not once per pass. */
function dedupe(warnings: readonly VisibilityWarning[]): VisibilityWarning[] {
  const seen = new Set<string>();
  const unique: VisibilityWarning[] = [];
  for (const warning of warnings) {
    if (seen.has(warning.message)) continue;
    seen.add(warning.message);
    unique.push(warning);
  }
  return unique;
}
