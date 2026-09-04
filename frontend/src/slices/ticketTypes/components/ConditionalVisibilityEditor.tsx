import Alert from '@mui/material/Alert';
import Box from '@mui/material/Box';
import FormControlLabel from '@mui/material/FormControlLabel';
import MenuItem from '@mui/material/MenuItem';
import Stack from '@mui/material/Stack';
import Switch from '@mui/material/Switch';
import TextField from '@mui/material/TextField';
import Typography from '@mui/material/Typography';
import { Controller, useFormContext, useWatch } from 'react-hook-form';
import { useFieldErrors } from '../formErrors';
import { dataTypeLabel, isChoiceDataType } from '../fieldDataTypes';
import type { TicketTypeFormValues } from '../schemas';

/**
 * *SHOWN ONLY WHEN* -- ONE FIELD'S conditionalVisibility RULE.
 * Screens/TicketTypesScreens.md section 5.4 row `conditionalVisibility`, and section 5.5 rule D.
 *
 * TWO CONTROLS, AND THE VALUE ONE IS NOT A FREE-TEXT BOX. This is the highest-yield authoring mistake
 * in the slice: an author types `Yes` against a YesNo field, the server accepts it -- `ValidateFields`
 * validates the REFERENCE and never the VALUE -- and the dependent field then never appears for
 * anybody, ever, with no error on any screen and nothing in any log. The renderer compares the rule's
 * string against a coerced live value (screen spec section 6.5), and a YesNo coerces to `"true"` or
 * `"false"` and to nothing else. So the value control's SHAPE is decided by the REFERENCED field's
 * data type, per the four-row table in section 5.5 rule D.
 *
 * THE FIELD Select OFFERS ONLY THE OTHER ROWS CURRENTLY IN THE FORM. TicketTypeMapper.cs:193-198
 * rejects a self-reference and a dangling reference with a 422, so a free-text field key is a
 * guaranteed round trip for a typo the client could have prevented.
 */
export function ConditionalVisibilityEditor({ fieldIndex }: { fieldIndex: number }) {
  const { control, register, setValue } = useFormContext<TicketTypeFormValues>();
  const messageFor = useFieldErrors(control);

  /**
   * THE WHOLE ARRAY, because this control is about the relationship BETWEEN rows: it needs every
   * other row's `key` to fill the Select, and the referenced row's `dataType` and `choiceOptions` to
   * decide the value control's shape. Watching it re-renders this component when any row's key
   * changes, which is exactly when the Select's options change.
   */
  const allFields = useWatch({ control, name: 'fields' });
  const enabled = useWatch({ control, name: `fields.${fieldIndex}.conditionalVisibility.enabled` });
  const referencedKey = useWatch({
    control,
    name: `fields.${fieldIndex}.conditionalVisibility.fieldKey`,
  });

  const ownKey = allFields[fieldIndex]?.key ?? '';

  // Other rows only, and only rows that have a key to be named by. A blank key cannot be referenced:
  // `keys.Contains('')` is false server-side, and the renderer has nothing to look up.
  const candidates = allFields.filter(
    (candidate, index) => index !== fieldIndex && candidate.key.trim() !== '',
  );

  const referenced = allFields.find(
    (candidate) => candidate.key.trim().toLowerCase() === referencedKey.trim().toLowerCase(),
  );

  return (
    <Box>
      <Typography variant="subtitle2" gutterBottom>
        Shown only when
      </Typography>

      <Controller
        control={control}
        name={`fields.${fieldIndex}.conditionalVisibility.enabled`}
        render={({ field }) => (
          <FormControlLabel
            control={
              <Switch
                checked={field.value}
                onChange={(event) => {
                  field.onChange(event.target.checked);
                  /**
                   * TURNING IT OFF DOES NOT ERASE THE PAIR. An author who unticks by accident and
                   * reticks has not asked to lose the rule they wrote, and `toFieldRequest` sends
                   * `conditionalVisibility: null` while `enabled` is false regardless of what the two
                   * boxes hold -- so nothing untrue can reach the server from a disabled rule.
                   */
                }}
              />
            }
            label="This field depends on another field's answer"
          />
        )}
      />

      {enabled && (
        <Stack spacing={2} sx={{ mt: 1 }}>
          {candidates.length === 0 ? (
            <Alert severity="warning">
              {/* A rule needs another named field to point at. Said rather than showing an empty
                  Select, which reads as a list that failed to load. */}
              Give another field a key first — a rule has to name the field it depends on.
            </Alert>
          ) : (
            <TextField
              select
              label="Field"
              size="small"
              // A Select over the other rows' keys, never a text box. See the header.
              {...register(`fields.${fieldIndex}.conditionalVisibility.fieldKey`)}
              value={referencedKey}
              onChange={(event) => {
                /**
                 * CHANGING THE REFERENCED FIELD CLEARS THE VALUE. The value control's shape is
                 * decided by the referenced field's data type, so a `"true"` left over from a YesNo
                 * becomes a permanently unsatisfiable rule against a SingleChoice -- accepted by the
                 * server, and invisible on every screen afterwards.
                 */
                setValue(
                  `fields.${fieldIndex}.conditionalVisibility.fieldKey`,
                  event.target.value,
                  { shouldDirty: true, shouldValidate: true },
                );
                setValue(`fields.${fieldIndex}.conditionalVisibility.value`, '', {
                  shouldDirty: true,
                });
              }}
              error={Boolean(
                messageFor(`fields.${fieldIndex}.conditionalVisibility.fieldKey`),
              )}
              helperText={messageFor(`fields.${fieldIndex}.conditionalVisibility.fieldKey`)}
              fullWidth
            >
              {candidates.map((candidate) => (
                <MenuItem key={candidate.key} value={candidate.key}>
                  {candidate.key} — {dataTypeLabel(candidate.dataType)}
                </MenuItem>
              ))}
            </TextField>
          )}

          {/* The value control, by the referenced field's data type. */}
          {referenced !== undefined && (
            <ValueControl
              fieldIndex={fieldIndex}
              referencedDataType={referenced.dataType}
              referencedOptions={referenced.choiceOptions}
            />
          )}

          {/* Neither reference nor value is wrong here, so this is not a validation error -- it is a
              rule the RENDERER cannot evaluate. A DateRange and a FileUpload have no defined coercion
              to a string (screen spec section 6.5, last two rows), so the renderer shows the dependent
              field rather than hiding it. Told to the author here because this editor is the only
              place they can act on it, and because a rule that silently never applies looks exactly
              like a rule that does. */}
          {referenced !== undefined &&
            (referenced.dataType === 'DateRange' || referenced.dataType === 'FileUpload') && (
              <Alert severity="warning">
                A {dataTypeLabel(referenced.dataType)} answer cannot be compared to a value, so this
                rule will not be applied and the field will always be shown. Point the rule at a
                different field.
              </Alert>
            )}

          {ownKey !== '' &&
            referencedKey.trim().toLowerCase() === ownKey.trim().toLowerCase() && (
              <Alert severity="error">A field cannot depend on itself.</Alert>
            )}
        </Stack>
      )}
    </Box>
  );
}

/**
 * THE FOUR-ROW TABLE OF screen spec section 5.5 rule D, implemented:
 *
 *   YesNo                        -> a Select of exactly "true" / "false", lower-case
 *   SingleChoice, MultipleChoice -> a Select over the referenced field's option VALUES
 *   anything else                -> a TextField capped at 500 (ConditionalValueMaxLength)
 *
 * The choice Select shows the label and submits the VALUE, because the renderer compares the
 * selected option's value and never its label (section 6.5) -- the label is what somebody renames
 * next week.
 */
function ValueControl({
  fieldIndex,
  referencedDataType,
  referencedOptions,
}: {
  fieldIndex: number;
  referencedDataType: string;
  referencedOptions: readonly { label: string; value: string }[];
}) {
  const { control, register } = useFormContext<TicketTypeFormValues>();
  const messageFor = useFieldErrors(control);

  const name = `fields.${fieldIndex}.conditionalVisibility.value` as const;
  const message = messageFor(name);

  if (referencedDataType === 'YesNo') {
    return (
      <TextField
        select
        label="Is"
        size="small"
        {...register(name)}
        error={Boolean(message)}
        helperText={message}
        fullWidth
      >
        {/* Lower-case, and exactly these two strings: the renderer coerces a YesNo answer to
            `value ? 'true' : 'false'` and compares that. "Yes" would never match. */}
        <MenuItem value="true">Yes</MenuItem>
        <MenuItem value="false">No</MenuItem>
      </TextField>
    );
  }

  if (isChoiceDataType(referencedDataType)) {
    const usable = referencedOptions.filter((option) => option.value.trim() !== '');

    if (usable.length === 0) {
      return (
        <Alert severity="warning">
          Give that field some options first — a rule has to name one of its option values.
        </Alert>
      );
    }

    return (
      <TextField
        select
        label="Is"
        size="small"
        {...register(name)}
        error={Boolean(message)}
        helperText={message}
        fullWidth
      >
        {usable.map((option) => (
          <MenuItem key={option.value} value={option.value}>
            {option.label === '' ? option.value : option.label}
          </MenuItem>
        ))}
      </TextField>
    );
  }

  return (
    <TextField
      label="Is"
      size="small"
      {...register(name)}
      error={Boolean(message)}
      helperText={
        message ??
        // Stated because the comparison is exact after trimming both sides, and a date is compared as
        // the "yyyy-MM-dd" string it is stored as.
        'Compared exactly against the answer. A date is written as 2026-09-02.'
      }
      fullWidth
    />
  );
}
