import { useEffect, useMemo, useRef, type ReactElement } from 'react';
import { Controller, useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import Alert from '@mui/material/Alert';
import Box from '@mui/material/Box';
import Button from '@mui/material/Button';
import Stack from '@mui/material/Stack';
import Typography from '@mui/material/Typography';
import { AdapterDateFns } from '@mui/x-date-pickers/AdapterDateFns';
import { LocalizationProvider } from '@mui/x-date-pickers/LocalizationProvider';
import { buildZodSchema, fieldAlias } from './buildZodSchema';
import { rendererFor, UnsupportedDataType } from './fieldRegistry';
import type { DynamicFormProps, FieldDescriptor } from './types';
import { computeVisibility } from './visibility';

/**
 * THE DYNAMIC FORM. Screens/TicketTypesScreens.md section 6, and the plan's section 6.8.
 *
 * IT COMPOSES THE OTHER THREE MODULES AND ADDS NOTHING TO THE CONTRACT. Its props are `fields`,
 * `mode`, `values` and `onSubmit`, and there is deliberately no `role`, `session`, `ticketId`,
 * `ticketTypeId` or `isAccountant`: shared/ may not import a slice, and a renderer that knows the role
 * is a renderer somebody will add a filter to. It never filters on `isVisibleToCustomer` -- that name
 * does not appear in this file -- because the server has already removed the fields a Customer-side
 * caller may not see, and a client copy of that filter would hide a regression instead of exposing it.
 *
 * IT HOLDS NO SERVER STATE AND ISSUES NO REQUESTS. No useQuery, no import from shared/api/, no fetch.
 *
 * THE RHF NAME IS AN ALIAS (f0, f1, ...), NEVER THE FIELD'S key. React Hook Form parses `.` and `[` in
 * a name as path syntax and the server accepts any non-blank string of <= 100 characters as a key, so
 * `salary.amount` registered directly becomes nested state `{ salary: { amount } }` and submits under
 * the wrong shape with nothing erroring. This indirection is why the component looks roundabout where a
 * direct register(field.key) would read better. Punch-list item 19.
 */
export function DynamicForm({ fields, mode, values, onSubmit }: DynamicFormProps): ReactElement {
  /**
   * defaultValues SEEDS THE FORM ONCE. A later change to the `values` prop is deliberately not applied:
   * RHF would need a reset, and a reset discards whatever the user has typed since -- so a background
   * refetch of the same record would silently wipe a half-written answer.
   */
  const defaultValues = useMemo(() => buildDefaultValues(fields, values), [fields, values]);

  /**
   * THE SCHEMA CHANGES WITH VISIBILITY, SO THE RESOLVER READS IT THROUGH A REF.
   *
   * The dependency runs in a circle: the schema depends on which fields are visible, which depends on
   * the current values, which come from the form -- and the form is created with the resolver. The ref
   * is where that circle is cut. Its type is written out (rather than inferred from `schema` below)
   * because an inferred one would be circular too, and TypeScript resolves a circular inference to
   * `any`, which would silently erase the typing of everything downstream.
   *
   * It is assigned during render, immediately after the schema is built. That is safe here because
   * nothing reads it during render: `mode: 'onBlur'` means the resolver runs from an event handler,
   * which cannot fire before the render that set it has committed.
   */
  const schemaRef = useRef<ReturnType<typeof buildZodSchema> | null>(null);

  const form = useForm<Record<string, unknown>>({
    // Section 9.3 rule A. It also bounds how often a user-supplied regexPattern runs: roughly once per
    // field visit instead of once per character. That is not a safety guarantee -- see
    // REGEX_INPUT_CEILING -- but it is the difference between one evaluation and forty.
    mode: 'onBlur',
    defaultValues,
    resolver: (formValues, context, options) => {
      const current = schemaRef.current;
      // Unreachable: the first render assigns the ref before any event can run the resolver. Passing
      // the values through unvalidated rather than throwing, because a throw here would take out the
      // form on a path that should not exist.
      if (current === null) return { values: formValues, errors: {} };
      return zodResolver(current)(formValues, context, options);
    },
  });

  // Subscribes to every change, which is what visibility needs: a rule on a controller the user is
  // typing into has to be re-evaluated as they type, not on blur.
  const watched = form.watch();

  const valuesByKey = useMemo(() => {
    const byKey: Record<string, unknown> = {};
    fields.forEach((field, index) => {
      byKey[field.key] = watched[fieldAlias(index)];
    });
    return byKey;
  }, [fields, watched]);

  const visibility = useMemo(() => computeVisibility(fields, valuesByKey), [fields, valuesByKey]);

  /**
   * A STABLE Set, SO THE SCHEMA IS NOT REBUILT ON EVERY KEYSTROKE. computeVisibility returns a fresh
   * Set each time it runs, and rebuilding the schema means recompiling every regexPattern -- which
   * section 6.4 rule 4 requires to happen once. The signature is the sorted key list, so the memo below
   * reuses the previous Set for as long as the same fields are visible. Reading `visibility` from a
   * closure the dependency list does not mention is intentional here: an unchanged signature means an
   * identical set of keys, so the "stale" value is equal to the current one.
   */
  const visibleSignature = [...visibility.visibleKeys].sort().join('\u0000');
  const visibleKeys = useMemo(() => new Set(visibility.visibleKeys), [visibleSignature]);

  /**
   * THE SCHEMA IS DERIVED FROM THE VISIBLE SET AND REBUILT WHEN IT CHANGES. This is the structural fix
   * for the worst bug available here: a hidden isRequired field contributing a required key makes the
   * resolver fail, so handleSubmit never calls onSubmit and the error attaches to a control that is not
   * rendered -- Submit does nothing at all, with no request, no banner and nothing in the console.
   */
  const schema = useMemo(() => buildZodSchema(fields, visibleKeys), [fields, visibleKeys]);
  schemaRef.current = schema;

  /**
   * An unevaluable rule or a cycle is logged ONCE per distinct set of warnings, not once per render:
   * computeVisibility runs on every keystroke, so logging inside it would fill the console. Same
   * intentional stale closure as above -- an unchanged message list is an unchanged set of warnings.
   */
  const warningSignature = visibility.warnings.map((warning) => warning.message).join('\u0000');
  useEffect(() => {
    for (const warning of visibility.warnings) console.warn(warning.message);
  }, [warningSignature]);

  const groups = useMemo(() => buildGroups(fields), [fields]);

  const body = (
    <Stack spacing={3}>
      {/* PREVIEW IS THE ONLY PLACE AN AUTHOR CAN DISCOVER THEY BUILT A LOOP, so the warning is shown
          here and only here -- a Customer-side user cannot act on it and should not be alarmed by it. */}
      {mode === 'preview' && visibility.warnings.length > 0 && (
        <Alert severity="warning">
          <Stack spacing={0.5}>
            {visibility.warnings.map((warning) => (
              <span key={warning.message}>{warning.message}</span>
            ))}
          </Stack>
        </Alert>
      )}

      {groups.map((group) => {
        const visibleMembers = group.members.filter((member) => visibleKeys.has(member.field.key));

        /**
         * A GROUP WITH NO VISIBLE MEMBERS RENDERS NOTHING -- no heading, no divider, no empty card. A
         * heading with nothing under it reads as a failed load, and every field in a group can
         * legitimately be conditional.
         */
        if (visibleMembers.length === 0) return null;

        return (
          <Box key={group.name}>
            {/* groupName === '' is the LEADING UNNAMED GROUP: no heading and no card. Blank is the DTO
                default and most types will have nothing else, so an unnamed group must not render an
                empty heading or a "General" caption nobody wrote. */}
            {group.name !== '' && (
              <Typography variant="subtitle1" component="h3" gutterBottom>
                {group.name}
              </Typography>
            )}
            <Stack spacing={2}>
              {visibleMembers.map((member) => (
                <FieldSlot
                  key={member.field.key}
                  field={member.field}
                  alias={member.alias}
                  mode={mode}
                  control={form.control}
                />
              ))}
            </Stack>
          </Box>
        );
      })}
    </Stack>
  );

  /**
   * mode="read" renders text, so there is no form element, no resolver run and no submit. mode="preview"
   * renders LIVE, FOCUSABLE CONTROLS AND NO SUBMIT BUTTON: a disabled form cannot demonstrate that a
   * conditionalVisibility rule works, and that is the single thing an author most needs to check before
   * saving. Nothing typed into a preview is persisted or read back, and it MUST NOT grow a Submit
   * button -- there is no ticket endpoint in this release for it to post to.
   */
  if (mode !== 'input' || onSubmit === undefined) {
    return <LocalizationProvider dateAdapter={AdapterDateFns}>{body}</LocalizationProvider>;
  }

  return (
    <LocalizationProvider dateAdapter={AdapterDateFns}>
      <form
        onSubmit={form.handleSubmit((submitted) => {
          onSubmit(assembleSubmission(fields, visibleKeys, submitted));
        })}
        noValidate
      >
        {body}
        <Box sx={{ mt: 3 }}>
          {/* Disabled ONLY while the submission is in flight -- which for this component means never,
              because it does not own the request. Never disabled on `!isValid`: a button greyed out
              before the user has touched anything gives them nothing to fix. */}
          <Button type="submit" variant="contained" disabled={form.formState.isSubmitting}>
            Submit
          </Button>
        </Box>
      </form>
    </LocalizationProvider>
  );
}

/**
 * One field's control, wrapped in a Controller so the registry stays free of React Hook Form. Errors
 * are looked up BY ALIAS, because that is the name they were registered under.
 */
function FieldSlot({
  field,
  alias,
  mode,
  control,
}: {
  field: FieldDescriptor;
  alias: string;
  mode: DynamicFormProps['mode'];
  control: ReturnType<typeof useForm<Record<string, unknown>>>['control'];
}): ReactElement {
  const renderer = rendererFor(field.dataType);

  // Occupies the field's position and contributes no rule and no value. Never skipped, never a text
  // fallback: see UnsupportedDataType.
  if (renderer === undefined) return <UnsupportedDataType field={field} />;

  return (
    <Controller
      name={alias}
      control={control}
      render={({ field: rhf, fieldState }) =>
        renderer({
          field,
          id: `dynamic-${alias}`,
          mode,
          value: rhf.value,
          onChange: rhf.onChange,
          onBlur: rhf.onBlur,
          errorMessage: fieldState.error?.message,
        })
      }
    />
  );
}

interface GroupMember {
  field: FieldDescriptor;
  alias: string;
}

interface FieldGroup {
  name: string;
  members: GroupMember[];
}

/**
 * LAYOUT, per section 6.6. The unnamed group leads; named groups follow, ordered by the MINIMUM
 * displayOrder among their members and tie-broken by groupName case-insensitively -- so section order
 * is controlled by the same number that orders fields, with nothing new for an author to learn.
 * Ordering groups alphabetically instead would put "Bank details" before "Personal details" whatever
 * the author intended, and there is no groupOrder column to appeal to.
 *
 * groupName IS COMPARED EXACTLY: "Bank Details" and "bank details" are two groups, because the server
 * trims it and does not case-fold it (TicketTypeMapper.cs:122). Folding them here would merge two
 * groups an author can see are separate in the editor, and the merge would be invisible there.
 *
 * NOTHING BELOW MUTATES THE ARRAY IT WAS GIVEN. `fields.sort(...)` sorts in place, and this array is the
 * one inside the TanStack Query cache entry -- so sorting it would reorder what the detail screen's
 * fields table sees, with no state change anywhere to explain it.
 */
function buildGroups(fields: readonly FieldDescriptor[]): FieldGroup[] {
  const byName = new Map<string, GroupMember[]>();

  fields.forEach((field, index) => {
    const members = byName.get(field.groupName) ?? [];
    members.push({ field, alias: fieldAlias(index) });
    byName.set(field.groupName, members);
  });

  const groups: FieldGroup[] = [...byName.entries()].map(([name, members]) => ({
    name,
    // displayOrder THEN key. display_order has no uniqueness constraint and ToDetail's OrderBy is a
    // stable sort, so ties keep whatever order the rows arrived in -- which can differ between a fresh
    // fetch and a cache read, making the form visibly reshuffle between renders. `key` is unique per
    // version, so displayOrder then key is total and deterministic.
    members: [...members].sort((a, b) => compareFields(a.field, b.field)),
  }));

  return groups.sort((a, b) => {
    if (a.name === '' && b.name !== '') return -1;
    if (b.name === '' && a.name !== '') return 1;

    const byOrder = minDisplayOrder(a) - minDisplayOrder(b);
    if (byOrder !== 0) return byOrder;

    return a.name.localeCompare(b.name, undefined, { sensitivity: 'base' });
  });
}

function minDisplayOrder(group: FieldGroup): number {
  return group.members.reduce(
    (lowest, member) => Math.min(lowest, member.field.displayOrder),
    Number.POSITIVE_INFINITY,
  );
}

function compareFields(a: FieldDescriptor, b: FieldDescriptor): number {
  if (a.displayOrder !== b.displayOrder) return a.displayOrder - b.displayOrder;
  const byBase = a.key.localeCompare(b.key, undefined, { sensitivity: 'base' });
  if (byBase !== 0) return byBase;
  // Ordinal tie-break, so two keys differing only in case do not tie.
  return a.key < b.key ? -1 : a.key > b.key ? 1 : 0;
}

/**
 * The initial value for each alias, by data type. A number starts as `null` and not `0`, a YesNo as
 * `null` and not `false`, a MultipleChoice as `[]`: every one of those distinguishes "not answered" from
 * an answer somebody gave.
 */
function buildDefaultValues(
  fields: readonly FieldDescriptor[],
  values: Record<string, unknown> | undefined,
): Record<string, unknown> {
  const defaults: Record<string, unknown> = {};

  fields.forEach((field, index) => {
    const supplied = values?.[field.key];
    defaults[fieldAlias(index)] = supplied === undefined ? emptyValueFor(field.dataType) : supplied;
  });

  return defaults;
}

function emptyValueFor(dataType: string): unknown {
  switch (dataType) {
    case 'WholeNumber':
    case 'DecimalNumber':
    case 'MoneyAmount':
    case 'YesNo':
    case 'FileUpload':
      return null;
    case 'DateRange':
      return { from: null, to: null };
    case 'MultipleChoice':
      return [];
    default:
      // Text, Date and SingleChoice all live in state as a string.
      return '';
  }
}

/**
 * THE SUBMITTED OBJECT, KEYED BY THE FIELDS' OWN keys AND BUILT FROM THE VISIBLE SET ONLY.
 *
 * A HIDDEN FIELD'S KEY IS ABSENT, NOT null. Present-and-null and absent are different answers: the
 * first says "asked, not answered", the second says "not asked", and only the second is true. This is
 * also why values are NOT cleared when a field hides -- a user who ticks *Other*, types a reason,
 * unticks and re-ticks by accident has not asked to lose the sentence, so it stays in form state and
 * this function decides what leaves.
 *
 * AN UNTOUCHED OPTIONAL FIELD SUBMITS null, never '', [] or NaN (section 9.3 rule F). The resolver has
 * already turned an empty optional into undefined, and undefined becomes null here.
 */
function assembleSubmission(
  fields: readonly FieldDescriptor[],
  visibleKeys: ReadonlySet<string>,
  submitted: Record<string, unknown>,
): Record<string, unknown> {
  const result: Record<string, unknown> = {};

  fields.forEach((field, index) => {
    if (!visibleKeys.has(field.key)) return;

    // FileUpload submits null, always: there is no upload path on this side of the wire yet.
    if (field.dataType === 'FileUpload') {
      result[field.key] = null;
      return;
    }

    // An unrecognised dataType contributes no value at all -- there is no control to have produced one.
    if (rendererFor(field.dataType) === undefined) return;

    result[field.key] = normalizeAnswer(submitted[fieldAlias(index)]);
  });

  return result;
}

function normalizeAnswer(value: unknown): unknown {
  if (value === undefined || value === null) return null;
  if (typeof value === 'string' && value.trim() === '') return null;
  if (typeof value === 'number' && Number.isNaN(value)) return null;
  if (Array.isArray(value) && value.length === 0) return null;

  // A DateRange with neither end answered is no answer at all.
  if (typeof value === 'object' && value !== null && 'from' in value && 'to' in value) {
    const range = value as { from: unknown; to: unknown };
    const emptyEnd = (end: unknown) => end === null || end === undefined || end === '';
    if (emptyEnd(range.from) && emptyEnd(range.to)) return null;
  }

  return value;
}
