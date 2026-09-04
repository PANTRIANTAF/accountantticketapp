import type { ReactElement } from 'react';
import Alert from '@mui/material/Alert';
import Box from '@mui/material/Box';
import Button from '@mui/material/Button';
import Checkbox from '@mui/material/Checkbox';
import FormControl from '@mui/material/FormControl';
import FormControlLabel from '@mui/material/FormControlLabel';
import FormGroup from '@mui/material/FormGroup';
import FormHelperText from '@mui/material/FormHelperText';
import FormLabel from '@mui/material/FormLabel';
import MenuItem from '@mui/material/MenuItem';
import Radio from '@mui/material/Radio';
import RadioGroup from '@mui/material/RadioGroup';
import Stack from '@mui/material/Stack';
import TextField from '@mui/material/TextField';
import Typography from '@mui/material/Typography';
import { DatePicker } from '@mui/x-date-pickers/DatePicker';
import { format, isValid, parse } from 'date-fns';
import { formatDate } from '../format/dates';
import { formatMoney } from '../format/money';
import type { DynamicFormMode, FieldDescriptor, FieldValidation } from './types';

/**
 * ONE CONTROL PER dataType. Screens/TicketTypesScreens.md section 6.3, whose eleven-row table is the
 * source for every choice below.
 *
 * A REGISTRY, NOT A CHAIN OF ifs. The eleven strings are the ones in
 * Slices/TicketTypes/ExternalInterfaces/FieldDataTypes.cs:28-38, in that file's order, and a registry
 * can be enumerated in a check against that list while a chain cannot.
 *
 * FOUR ENTRIES ARE DECISIONS, NOT PREFERENCES, and each states its failure mode where it is written:
 * YesNo is a RadioGroup and not a Checkbox; DateRange is two DatePickers because DateRangePicker is in
 * the MUI PRO package and is not a locked dependency; MoneyAmount carries NO currency symbol because
 * there is no currency anywhere in the schema; MultipleChoice is a FormGroup of Checkboxes and never a
 * native multi-select.
 *
 * THIS FILE TAKES NO ROLE, SESSION, TICKET OR TICKET-TYPE PROP AND ISSUES NO REQUESTS. SingleChoice's
 * options come from `choiceOptions` on the descriptor it was handed -- there is no options endpoint,
 * and adding one would put a network call inside a component that renders once per field.
 *
 * `isVisibleToCustomer` DOES NOT APPEAR IN THIS FILE, and that is deliberate: the server has already
 * removed the fields a Customer-side caller may not see (TicketTypeMapper.cs:228-230), so a client
 * filter here would be a mute button on the alarm -- if that Where is ever dropped, the fields arrive
 * over the wire, sit in the network tab and the query cache, and nothing looks wrong on screen.
 */

export interface FieldRendererProps {
  field: FieldDescriptor;
  /** The DOM id, derived from the RHF alias. Never the field's `key`, which may contain anything. */
  id: string;
  mode: DynamicFormMode;
  value: unknown;
  onChange: (value: unknown) => void;
  onBlur: () => void;
  /** Already looked up by alias by DynamicForm. */
  errorMessage?: string | undefined;
}

export type FieldRenderer = (props: FieldRendererProps) => ReactElement;

/** The two ends of a DateRange, as they live in form state. */
export interface DateRangeValue {
  from: string | null;
  to: string | null;
}

const ISO_DATE = 'yyyy-MM-dd';

// -------------------------------------------------------------------------------------------------
// Shared pieces
// -------------------------------------------------------------------------------------------------

/**
 * helpText GOES IN helperText, AND AN ERROR REPLACES IT (section 6.7 rule B). MUI has ONE slot, so
 * rendering both means a validation message either never appears or appears somewhere unexpected -- and
 * a message the user cannot find is the same as no message at all.
 */
function helper(props: FieldRendererProps): string | undefined {
  return props.errorMessage ?? (props.field.helpText || undefined);
}

/**
 * mode="read" RENDERS TEXT, NOT A DISABLED INPUT (section 6.7 rule E). A disabled TextField is
 * low-contrast, unselectable and unreadable at length, and a page of them looks broken rather than
 * informational. Nothing calls read mode yet -- the ticket detail screen will -- and it is built now
 * because retrofitting it later means touching all eleven renderers.
 */
function ReadValue({
  label,
  text,
  helpText,
}: {
  label: string;
  text: string;
  helpText: string;
}): ReactElement {
  return (
    <Box>
      <Typography variant="caption" color="text.secondary" component="div">
        {label}
      </Typography>
      {/* An em dash for an unanswered question, so a blank line is never read as a failed load. */}
      <Typography variant="body1">{text === '' ? '—' : text}</Typography>
      {helpText !== '' && (
        <Typography variant="caption" color="text.secondary" component="div">
          {helpText}
        </Typography>
      )}
    </Box>
  );
}

function asString(value: unknown): string {
  return typeof value === 'string' ? value : '';
}

function asNumber(value: unknown): number | null {
  return typeof value === 'number' && !Number.isNaN(value) ? value : null;
}

function asStringArray(value: unknown): string[] {
  return Array.isArray(value) ? value.filter((entry): entry is string => typeof entry === 'string') : [];
}

function asDateRange(value: unknown): DateRangeValue {
  if (value === null || typeof value !== 'object') return { from: null, to: null };
  const record = value as Record<string, unknown>;
  return {
    from: typeof record.from === 'string' ? record.from : null,
    to: typeof record.to === 'string' ? record.to : null,
  };
}

/**
 * "yyyy-MM-dd" <-> Date, for the pickers only. NEVER toISOString(): it converts to UTC, so a date
 * picked as 1 March is submitted as 28 February anywhere east of UTC and the shift is invisible on the
 * authoring machine (GeneralUIArchitecture.md section 10.2). date-fns `parse` and `format` both work in
 * local time, so the round trip is lossless.
 */
function isoToDate(value: string): Date | null {
  if (value === '') return null;
  const parsed = parse(value, ISO_DATE, new Date());
  return isValid(parsed) ? parsed : null;
}

function dateToIso(value: Date | null): string | null {
  if (value === null || !isValid(value)) return null;
  return format(value, ISO_DATE);
}

// -------------------------------------------------------------------------------------------------
// 1. SingleLineText   2. MultiLineText
// -------------------------------------------------------------------------------------------------

function TextInput(props: FieldRendererProps & { multiline: boolean }): ReactElement {
  const { field, id, mode, value, onChange, onBlur, multiline } = props;
  const text = asString(value);

  if (mode === 'read') {
    return <ReadValue label={field.label} text={text} helpText={field.helpText} />;
  }

  return (
    <TextField
      id={id}
      // A real <label>, from `label`, via MUI's label prop. A placeholder is not a label.
      label={field.label}
      // isRequired DRIVES BOTH the asterisk and the Zod rule. This prop only draws the asterisk;
      // buildZodSchema adds the rule. One without the other is either a form that accepts a blank
      // required answer or one that rejects it with no visual cue that it was mandatory.
      required={field.isRequired}
      value={text}
      onChange={(event) => onChange(event.target.value)}
      onBlur={onBlur}
      error={Boolean(props.errorMessage)}
      helperText={helper(props)}
      fullWidth
      multiline={multiline}
      // Four rows to start and never past twelve: an auto-growing box that swallows the page moves
      // the submit button off screen while the user is still typing.
      {...(multiline ? { minRows: 4, maxRows: 12 } : {})}
    />
  );
}

// -------------------------------------------------------------------------------------------------
// 3. WholeNumber   4. DecimalNumber   5. MoneyAmount
// -------------------------------------------------------------------------------------------------

/**
 * NUMBERS STAY NUMBERS IN FORM STATE (section 6.7 rule G), and an empty box is `null`, never `0`:
 * `Number('')` is 0, a zero the user never typed and indistinguishable from one they did (rule F).
 * The value read out of the DOM is the input's own `valueAsNumber`, so no string parsing is invented
 * here, and NaN -- which is what an empty or unparseable numeric input yields -- becomes null.
 *
 * MoneyAmount CARRIES NO CURRENCY SYMBOL. There is no currency column anywhere in the schema
 * (min_value NUMERIC(18,4) and nothing else), so a symbol would be a guess rendered as a fact.
 */
function NumberInput(
  props: FieldRendererProps & { step: number | 'any'; wholeNumber: boolean; alignRight: boolean },
): ReactElement {
  const { field, id, mode, value, onChange, onBlur, step, wholeNumber, alignRight } = props;
  const numeric = asNumber(value);

  if (mode === 'read') {
    const text =
      numeric === null
        ? ''
        : field.dataType === 'MoneyAmount'
          ? formatMoney(numeric)
          : numberFormatter.format(numeric);
    return <ReadValue label={field.label} text={text} helpText={field.helpText} />;
  }

  return (
    <TextField
      id={id}
      label={field.label}
      required={field.isRequired}
      type="number"
      value={numeric === null ? '' : String(numeric)}
      onChange={(event) => {
        const raw = event.target.value;
        if (raw === '') {
          onChange(null);
          return;
        }
        const parsed = (event.target as HTMLInputElement).valueAsNumber;
        onChange(Number.isNaN(parsed) ? null : parsed);
      }}
      onBlur={onBlur}
      error={Boolean(props.errorMessage)}
      helperText={helper(props)}
      fullWidth
      slotProps={{
        // A non-integer is rejected in Zod, never by masking keystrokes: masking eats a pasted value
        // and gives the user nothing to correct.
        htmlInput: {
          inputMode: wholeNumber ? 'numeric' : 'decimal',
          step,
          ...(alignRight ? { style: { textAlign: 'right' as const } } : {}),
        },
      }}
    />
  );
}

const numberFormatter = new Intl.NumberFormat();

// -------------------------------------------------------------------------------------------------
// 6. Date
// -------------------------------------------------------------------------------------------------

function DateInput(props: FieldRendererProps): ReactElement {
  const { field, id, mode, value, onChange, onBlur } = props;
  const iso = asString(value);

  if (mode === 'read') {
    return <ReadValue label={field.label} text={formatDate(iso)} helpText={field.helpText} />;
  }

  return (
    <DatePicker
      label={field.label}
      value={isoToDate(iso)}
      // The plain date string is what lives in state and what is submitted.
      onChange={(next) => onChange(dateToIso(next) ?? '')}
      slotProps={{
        textField: {
          id,
          required: field.isRequired,
          onBlur,
          error: Boolean(props.errorMessage),
          helperText: helper(props),
          fullWidth: true,
        },
      }}
    />
  );
}

// -------------------------------------------------------------------------------------------------
// 7. DateRange
// -------------------------------------------------------------------------------------------------

/**
 * TWO DatePickers IN ONE FIELDSET. MUI's DateRangePicker is in the PRO package and is not a locked
 * dependency, so it may not be used and may not be added.
 *
 * The group label is a FormLabel inside a FormControl, because two inputs under one caption would
 * otherwise be announced by a screen reader as two unlabelled date boxes (section 6.7 rule A).
 */
function DateRangeInput(props: FieldRendererProps): ReactElement {
  const { field, id, mode, value, onChange, onBlur } = props;
  const range = asDateRange(value);

  if (mode === 'read') {
    const from = formatDate(range.from);
    const to = formatDate(range.to);
    const text = from === '' && to === '' ? '' : `${from || '—'} to ${to || '—'}`;
    return <ReadValue label={field.label} text={text} helpText={field.helpText} />;
  }

  const message = helper(props);

  return (
    <FormControl component="fieldset" error={Boolean(props.errorMessage)} fullWidth>
      <FormLabel component="legend" required={field.isRequired}>
        {field.label}
      </FormLabel>
      <Stack direction={{ xs: 'column', sm: 'row' }} spacing={2} sx={{ mt: 1 }}>
        <DatePicker
          label="From"
          value={isoToDate(range.from ?? '')}
          onChange={(next) => onChange({ ...range, from: dateToIso(next) })}
          slotProps={{ textField: { id: `${id}-from`, onBlur, fullWidth: true } }}
        />
        <DatePicker
          label="To"
          value={isoToDate(range.to ?? '')}
          onChange={(next) => onChange({ ...range, to: dateToIso(next) })}
          slotProps={{ textField: { id: `${id}-to`, onBlur, fullWidth: true } }}
        />
      </Stack>
      {message !== undefined && <FormHelperText>{message}</FormHelperText>}
    </FormControl>
  );
}

// -------------------------------------------------------------------------------------------------
// 8. YesNo
// -------------------------------------------------------------------------------------------------

/**
 * A RadioGroup, AND NOT A CHECKBOX. A checkbox cannot represent "not answered", so an optional YesNo
 * built from one would submit `false` for a question nobody read -- a recorded answer that was never
 * given. `null` is a first-class state here and stays null until the user picks a side.
 */
function YesNoInput(props: FieldRendererProps): ReactElement {
  const { field, id, mode, value, onChange, onBlur } = props;
  const selected = typeof value === 'boolean' ? value : null;

  if (mode === 'read') {
    return (
      <ReadValue
        label={field.label}
        text={selected === null ? '' : selected ? 'Yes' : 'No'}
        helpText={field.helpText}
      />
    );
  }

  const message = helper(props);

  return (
    <FormControl component="fieldset" error={Boolean(props.errorMessage)}>
      <FormLabel component="legend" required={field.isRequired}>
        {field.label}
      </FormLabel>
      <RadioGroup
        row
        id={id}
        value={selected === null ? '' : selected ? 'true' : 'false'}
        onChange={(_event, next) => onChange(next === 'true')}
        onBlur={onBlur}
      >
        <FormControlLabel value="true" control={<Radio />} label="Yes" />
        <FormControlLabel value="false" control={<Radio />} label="No" />
      </RadioGroup>
      {message !== undefined && <FormHelperText>{message}</FormHelperText>}
    </FormControl>
  );
}

// -------------------------------------------------------------------------------------------------
// 9. SingleChoice
// -------------------------------------------------------------------------------------------------

/** Render the option's `label`, submit its `value`. The server guarantees >= 2 options for this type. */
function SingleChoiceInput(props: FieldRendererProps): ReactElement {
  const { field, id, mode, value, onChange, onBlur } = props;
  const selected = asString(value);

  if (mode === 'read') {
    const option = field.choiceOptions.find((entry) => entry.value === selected);
    // The stored VALUE is shown when no option matches it: an author who renamed an option should see
    // what was actually recorded, not a blank where an answer used to be.
    return (
      <ReadValue
        label={field.label}
        text={option?.label ?? selected}
        helpText={field.helpText}
      />
    );
  }

  return (
    <TextField
      id={id}
      select
      label={field.label}
      required={field.isRequired}
      value={selected}
      onChange={(event) => onChange(event.target.value)}
      onBlur={onBlur}
      error={Boolean(props.errorMessage)}
      helperText={helper(props)}
      fullWidth
    >
      {field.choiceOptions.map((option) => (
        <MenuItem key={option.value} value={option.value}>
          {option.label}
        </MenuItem>
      ))}
    </TextField>
  );
}

// -------------------------------------------------------------------------------------------------
// 10. MultipleChoice
// -------------------------------------------------------------------------------------------------

/**
 * A FormGroup of Checkboxes, NEVER a native multi-select: a multi-select needs ctrl-click to add a
 * second option and is unusable on touch, where it silently replaces the selection instead.
 */
function MultipleChoiceInput(props: FieldRendererProps): ReactElement {
  const { field, id, mode, value, onChange, onBlur } = props;
  const selected = asStringArray(value);

  if (mode === 'read') {
    const labels = selected.map(
      (entry) => field.choiceOptions.find((option) => option.value === entry)?.label ?? entry,
    );
    return <ReadValue label={field.label} text={labels.join(', ')} helpText={field.helpText} />;
  }

  const message = helper(props);

  return (
    <FormControl component="fieldset" error={Boolean(props.errorMessage)}>
      <FormLabel component="legend" required={field.isRequired}>
        {field.label}
      </FormLabel>
      <FormGroup id={id} onBlur={onBlur}>
        {field.choiceOptions.map((option) => (
          <FormControlLabel
            key={option.value}
            control={
              <Checkbox
                checked={selected.includes(option.value)}
                onChange={(event) => {
                  // A new array every time: the one in state may be the array inside a query cache
                  // entry, and push/splice would mutate it.
                  onChange(
                    event.target.checked
                      ? [...selected, option.value]
                      : selected.filter((entry) => entry !== option.value),
                  );
                }}
              />
            }
            label={option.label}
          />
        ))}
      </FormGroup>
      {message !== undefined && <FormHelperText>{message}</FormHelperText>}
    </FormControl>
  );
}

// -------------------------------------------------------------------------------------------------
// 11. FileUpload
// -------------------------------------------------------------------------------------------------

/**
 * DISABLED, WITH AN EXPLANATORY NOTE, AND NOT OMITTED. Screens/TicketTypesScreens.md section 6.9.
 *
 * The endpoints are NOT what is missing: Documents is built and registered and Tickets already owns
 * /api/documents/upload, /list, /download and /delete (Slices/Tickets/TicketsEndpoints.cs:250-356).
 * What is missing is on this side of the wire -- there is no Tickets UI, no ticket form and therefore
 * no ticket id, and /api/documents/upload takes one.
 *
 * It contributes NO Zod rule, NOT EVEN isRequired: a required-but-impossible field would make every
 * ticket of that type unsubmittable the moment a Tickets UI ships. It submits null, always. And it is
 * NOT omitted -- a ticket type author can define a file field today and needs to see it; omitting it
 * makes their own field invisible and they will add it a second time.
 */
function FileUploadPlaceholder(props: FieldRendererProps): ReactElement {
  const { field, mode } = props;

  if (mode === 'read') {
    return <ReadValue label={field.label} text="" helpText={field.helpText} />;
  }

  const fileRules = describeFileRules(field.validation);
  const notes = [field.helpText, fileRules].filter((entry) => entry !== '');

  return (
    <Box sx={{ border: 1, borderColor: 'divider', borderRadius: 1, p: 2 }}>
      <Typography variant="body2" gutterBottom>
        {field.label}
        {field.isRequired ? ' *' : ''}
      </Typography>
      <Button variant="outlined" size="small" disabled>
        Choose file
      </Button>
      <Typography variant="caption" color="text.secondary" component="div" sx={{ mt: 1 }}>
        File uploads are not available yet.
      </Typography>
      {notes.map((note) => (
        <Typography key={note} variant="caption" color="text.secondary" component="div">
          {note}
        </Typography>
      ))}
    </Box>
  );
}

/**
 * allowedFileTypes and maxFileSizeBytes CONTRIBUTE NO RULE TODAY, so they are surfaced as help text --
 * "PDF, JPG, PNG, up to 5 MB" rather than raw -- so an author can confirm the rules they set were
 * actually stored. Returns '' when neither is set.
 */
export function describeFileRules(validation: FieldValidation): string {
  const parts: string[] = [];

  if (validation.allowedFileTypes.length > 0) {
    parts.push(validation.allowedFileTypes.map((entry) => entry.toUpperCase()).join(', '));
  }

  const maxBytes = validation.maxFileSizeBytes;
  if (maxBytes !== null && maxBytes !== undefined) {
    parts.push(`up to ${formatBytes(maxBytes)}`);
  }

  return parts.join(', ');
}

function formatBytes(bytes: number): string {
  if (bytes >= 1024 * 1024) return `${numberFormatter.format(Math.round(bytes / (1024 * 1024)))} MB`;
  if (bytes >= 1024) return `${numberFormatter.format(Math.round(bytes / 1024))} KB`;
  return `${numberFormatter.format(bytes)} bytes`;
}

// -------------------------------------------------------------------------------------------------
// The registry, and the placeholder for a dataType that is not in it
// -------------------------------------------------------------------------------------------------

/**
 * The eleven strings of FieldDataTypes.cs:28-38, IN THAT FILE'S ORDER. Adding a twelfth on the server
 * without adding it here is not a crash: see UnsupportedDataType below.
 */
export const fieldRegistry: Record<string, FieldRenderer> = {
  SingleLineText: (props) => <TextInput {...props} multiline={false} />,
  MultiLineText: (props) => <TextInput {...props} multiline />,
  WholeNumber: (props) => <NumberInput {...props} step={1} wholeNumber alignRight={false} />,
  DecimalNumber: (props) => (
    <NumberInput {...props} step="any" wholeNumber={false} alignRight={false} />
  ),
  MoneyAmount: (props) => <NumberInput {...props} step={0.01} wholeNumber={false} alignRight />,
  Date: (props) => <DateInput {...props} />,
  DateRange: (props) => <DateRangeInput {...props} />,
  YesNo: (props) => <YesNoInput {...props} />,
  SingleChoice: (props) => <SingleChoiceInput {...props} />,
  MultipleChoice: (props) => <MultipleChoiceInput {...props} />,
  FileUpload: (props) => <FileUploadPlaceholder {...props} />,
};

/**
 * AN UNRECOGNISED dataType RENDERS A VISIBLE ERROR PLACEHOLDER AND NOTHING ELSE. It occupies the
 * field's position in the layout and contributes no Zod rule and no value.
 *
 * NOT SKIPPED SILENTLY, AND NOT FALLEN BACK TO SingleLineText. A silently skipped isRequired field
 * produces a schema whose required key can never be satisfied, so Submit fails against a control that
 * is not on screen -- a form that cannot be submitted with nothing anywhere indicating why. A text
 * fallback is worse: it collects a string where a number or a date was specified, and the wrongness
 * surfaces in whatever consumes the Field Value, long after the person who typed it has gone.
 *
 * This is reachable WITHOUT a deployment mismatch: TicketTypeMapper.cs:162 validates DataType against
 * FieldDataTypes.All, so a twelfth data type added on the server ships to a browser holding an older
 * bundle.
 */
export function UnsupportedDataType({ field }: { field: FieldDescriptor }): ReactElement {
  return (
    <Alert severity="error">
      Field “{field.label}” ({field.key}) has an unsupported data type “{field.dataType}” and cannot be
      shown.
    </Alert>
  );
}

/** Present so DynamicForm never indexes the registry with a string it has not checked. */
export function rendererFor(dataType: string): FieldRenderer | undefined {
  return Object.prototype.hasOwnProperty.call(fieldRegistry, dataType)
    ? fieldRegistry[dataType]
    : undefined;
}
