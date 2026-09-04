import ArrowDownwardIcon from '@mui/icons-material/ArrowDownward';
import ArrowUpwardIcon from '@mui/icons-material/ArrowUpward';
// `DeleteOutlined`, not `DeleteOutline`: this version of @mui/icons-material ships 2,140 `*Outlined`
// modules and exactly one `*Outline`, so the older alias is a build failure rather than a fallback.
import DeleteOutlinedIcon from '@mui/icons-material/DeleteOutlined';
import Divider from '@mui/material/Divider';
import FormControlLabel from '@mui/material/FormControlLabel';
import IconButton from '@mui/material/IconButton';
import MenuItem from '@mui/material/MenuItem';
import Paper from '@mui/material/Paper';
import Stack from '@mui/material/Stack';
import Switch from '@mui/material/Switch';
import TextField from '@mui/material/TextField';
import Tooltip from '@mui/material/Tooltip';
import Typography from '@mui/material/Typography';
import type { ReactNode } from 'react';
import { Controller, useFormContext, useWatch } from 'react-hook-form';
import { DATA_TYPE_LABELS, FIELD_DATA_TYPES, isChoiceDataType } from '../fieldDataTypes';
import { useFieldErrors } from '../formErrors';
import { applyDataTypeChange, type TicketTypeFormValues } from '../schemas';
import { ChoiceOptionsEditor } from './ChoiceOptionsEditor';
import { ConditionalVisibilityEditor } from './ConditionalVisibilityEditor';
import { ValidationRulesEditor } from './ValidationRulesEditor';

/**
 * ONE FIELD ROW OF THE EDITOR -- one `CreateFieldDescriptorDto`.
 * Screens/TicketTypesScreens.md section 5.4, with the rules of section 5.5.
 *
 * ALWAYS FULLY RENDERED, NEVER LAZY. Section 5.1 item 1: `/edit` builds the next version's descriptors
 * from `req.Fields` and nothing else, so every row must be in form state and submitted every time.
 * A row hidden behind an accordion that was never opened is fine only if its VALUES are still in form
 * state -- so nothing here is mounted conditionally except the parts that must not exist for the
 * chosen data type (choice options), and those are cleared from form state at the same moment by
 * `applyDataTypeChange`, not merely hidden.
 */
export function FieldDescriptorEditor({
  fieldIndex,
  fieldCount,
  onMoveUp,
  onMoveDown,
  onRemove,
}: {
  fieldIndex: number;
  fieldCount: number;
  onMoveUp: () => void;
  onMoveDown: () => void;
  onRemove: () => void;
}) {
  const { control, register, getValues, setValue } = useFormContext<TicketTypeFormValues>();
  const messageFor = useFieldErrors(control);

  const dataType = useWatch({ control, name: `fields.${fieldIndex}.dataType` });
  const key = useWatch({ control, name: `fields.${fieldIndex}.key` });

  /**
   * CHANGING THE DATA TYPE IS A TRANSITION ACROSS THE WHOLE ROW, so the row is read, transformed by
   * `applyDataTypeChange` (screen spec section 5.5 rule B: seed or clear `choiceOptions`, clear every
   * inapplicable `validation` member) and written back as one object.
   *
   * `setValue` on the ROW path and not eleven separate calls: RHF walks an object value key by key
   * (`setFieldValues`, index.esm.mjs:2638-2658) and, when it reaches a path registered as a field
   * array -- `fields.N.choiceOptions` -- emits on `_subjects.array`, which is the subscription
   * `useFieldArray` uses to rebuild its rows (:1188-1202). `_formValues` is already updated before
   * that emit (`_setValue`, :2667), so ChoiceOptionsEditor sees the seeded or cleared options rather
   * than the previous ones.
   */
  function handleDataTypeChange(nextDataType: string) {
    const current = getValues(`fields.${fieldIndex}`);
    setValue(`fields.${fieldIndex}`, applyDataTypeChange(current, nextDataType), {
      shouldDirty: true,
    });
  }

  return (
    <Paper variant="outlined" sx={{ p: 2 }}>
      <Stack
        direction="row"
        spacing={1}
        sx={{ alignItems: 'center', justifyContent: 'space-between', mb: 2 }}
      >
        <Typography variant="subtitle1" component="h3">
          {/* The position, not an editable displayOrder box -- see the note on the reorder buttons. */}
          Field {fieldIndex + 1} of {fieldCount}
          {key.trim() === '' ? '' : `: ${key.trim()}`}
        </Typography>

        <Stack direction="row" spacing={0.5}>
          {/* MOVE BUTTONS RATHER THAN A displayOrder BOX. Section 5.5 rule E requires every row's
              displayOrder to be rewritten to its array index on every change, so a box the author
              types into is a control whose value is overwritten before it is sent -- and two rows
              sharing a number is an order the server stores and `ToDetail` sorts by arbitrarily.
              The parent renumbers after each move; `toFieldRequest` renumbers again on submit. */}
          <Tooltip title="Move up">
            <span>
              <IconButtonLike
                ariaLabel={`Move field ${String(fieldIndex + 1)} up`}
                disabled={fieldIndex === 0}
                onClick={onMoveUp}
              >
                <ArrowUpwardIcon fontSize="small" />
              </IconButtonLike>
            </span>
          </Tooltip>
          <Tooltip title="Move down">
            <span>
              <IconButtonLike
                ariaLabel={`Move field ${String(fieldIndex + 1)} down`}
                disabled={fieldIndex === fieldCount - 1}
                onClick={onMoveDown}
              >
                <ArrowDownwardIcon fontSize="small" />
              </IconButtonLike>
            </span>
          </Tooltip>
          {/* NEVER DISABLED AT ONE ROW. "A ticket type needs at least one field." is a Zod message
              that says what is wrong; a button that stops responding says only that something is. */}
          <Tooltip title="Remove this field">
            <span>
              <IconButtonLike
                ariaLabel={`Remove field ${String(fieldIndex + 1)}`}
                onClick={onRemove}
              >
                <DeleteOutlinedIcon fontSize="small" />
              </IconButtonLike>
            </span>
          </Tooltip>
        </Stack>
      </Stack>

      <Stack spacing={2}>
        <Stack direction={{ xs: 'column', sm: 'row' }} spacing={2}>
          <TextField
            label="Key"
            size="small"
            required
            /**
             * TRIMMED BY THE SCHEMA, and this is the only thing preventing a real bug: the server
             * trims `label` and `groupName` and NOTHING ELSE on a field (`NormalizeFields`), while the
             * uniqueness HashSet is OrdinalIgnoreCase -- case-insensitive and whitespace-SENSITIVE.
             * So "key" and "key " are two distinct fields in one version, both stored, both rendered,
             * indistinguishable on screen. Screen spec section 5.5 rule C; flagged in its section 10
             * item 3.
             */
            {...register(`fields.${fieldIndex}.key`)}
            error={Boolean(messageFor(`fields.${fieldIndex}.key`))}
            helperText={
              messageFor(`fields.${fieldIndex}.key`) ??
              'How answers are stored. Unique within this ticket type.'
            }
            fullWidth
          />
          <TextField
            select
            label="Data type"
            size="small"
            /**
             * A Select over the eleven strings, NEVER a free-text box (section 5.4). The stored values
             * are compared ordinally by a CHECK constraint, so this is also the one place a raw
             * dataType value may be used -- and even here the LABEL is shown and the value submitted.
             */
            value={dataType}
            onChange={(event) => {
              handleDataTypeChange(event.target.value);
            }}
            error={Boolean(messageFor(`fields.${fieldIndex}.dataType`))}
            helperText={messageFor(`fields.${fieldIndex}.dataType`)}
            fullWidth
          >
            {FIELD_DATA_TYPES.map((candidate) => (
              <MenuItem key={candidate} value={candidate}>
                {DATA_TYPE_LABELS[candidate]}
              </MenuItem>
            ))}
          </TextField>
        </Stack>

        <Stack direction={{ xs: 'column', sm: 'row' }} spacing={2}>
          <TextField
            label="Label"
            size="small"
            {...register(`fields.${fieldIndex}.label`)}
            error={Boolean(messageFor(`fields.${fieldIndex}.label`))}
            helperText={
              messageFor(`fields.${fieldIndex}.label`) ?? 'Shown above the field on the form.'
            }
            fullWidth
          />
          <TextField
            label="Group name"
            size="small"
            {...register(`fields.${fieldIndex}.groupName`)}
            error={Boolean(messageFor(`fields.${fieldIndex}.groupName`))}
            helperText={
              messageFor(`fields.${fieldIndex}.groupName`) ??
              // Section 6.6: blank is not "no group", it is the leading unnamed group, which is
              // rendered first and without a heading.
              'Leave empty to put this field at the top, above the first heading.'
            }
            fullWidth
          />
        </Stack>

        <TextField
          label="Help text"
          size="small"
          multiline
          minRows={2}
          {...register(`fields.${fieldIndex}.helpText`)}
          error={Boolean(messageFor(`fields.${fieldIndex}.helpText`))}
          helperText={
            messageFor(`fields.${fieldIndex}.helpText`) ??
            'Shown under the field. Optional, up to 10,000 characters.'
          }
          fullWidth
        />

        <Stack direction={{ xs: 'column', sm: 'row' }} spacing={{ xs: 0, sm: 3 }}>
          <Controller
            control={control}
            name={`fields.${fieldIndex}.isRequired`}
            render={({ field }) => (
              <FormControlLabel
                control={
                  <Switch
                    checked={field.value}
                    onChange={(event) => {
                      field.onChange(event.target.checked);
                    }}
                  />
                }
                // Defaults on, matching CreateFieldDescriptorDto.cs:24.
                label="Required"
              />
            )}
          />

          <Controller
            control={control}
            name={`fields.${fieldIndex}.isVisibleToCustomer`}
            render={({ field }) => (
              /**
               * AN INVERTED SWITCH: "Accountant only" is `isVisibleToCustomer === false`
               * (section 5.4). The raw property name is in the tooltip so the copy on screen and the
               * property in the code stay reconcilable -- inverted booleans are where a later reader
               * guesses wrong.
               *
               * This is a SERVER-SIDE filter, not a client one: the API removes these descriptors
               * from a Customer's copy of the form (section 0.1), and nothing in this SPA hides a
               * field it received.
               */
              <Tooltip title="Accountant only means isVisibleToCustomer = false">
                <FormControlLabel
                  control={
                    <Switch
                      checked={!field.value}
                      onChange={(event) => {
                        field.onChange(!event.target.checked);
                      }}
                    />
                  }
                  label="Accountant only"
                />
              </Tooltip>
            )}
          />
        </Stack>

        {/* Present ONLY for the two choice types (section 5.4). `applyDataTypeChange` has already
            seeded two blank rows on the way in and emptied the array on the way out, so this is a
            display decision that cannot disagree with form state. */}
        {isChoiceDataType(dataType) && (
          <>
            <Divider />
            <ChoiceOptionsEditor fieldIndex={fieldIndex} />
          </>
        )}

        <Divider />
        <ValidationRulesEditor fieldIndex={fieldIndex} dataType={dataType} />

        <Divider />
        <ConditionalVisibilityEditor fieldIndex={fieldIndex} />
      </Stack>
    </Paper>
  );
}

/**
 * A plain MUI IconButton with a mandatory accessible name. A disabled IconButton swallows its own
 * mouse events, so the Tooltips above wrap it in a `<span>` -- otherwise the tooltip explaining why
 * the button is disabled is the one tooltip that never appears.
 */
function IconButtonLike({
  ariaLabel,
  disabled,
  onClick,
  children,
}: {
  ariaLabel: string;
  disabled?: boolean;
  onClick: () => void;
  children: ReactNode;
}) {
  return (
    <IconButton
      aria-label={ariaLabel}
      disabled={disabled ?? false}
      onClick={onClick}
      size="small"
    >
      {children}
    </IconButton>
  );
}
