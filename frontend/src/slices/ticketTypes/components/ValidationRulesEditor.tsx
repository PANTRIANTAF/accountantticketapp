import Alert from '@mui/material/Alert';
import Box from '@mui/material/Box';
import Stack from '@mui/material/Stack';
import TextField from '@mui/material/TextField';
import Typography from '@mui/material/Typography';
import { Controller, useFormContext, type Control, type FieldPath } from 'react-hook-form';
import { useFieldErrors } from '../formErrors';
import { appliesTo, validationMembersFor, type TicketTypeFormValues } from '../schemas';

/**
 * THE VALIDATION RULES ONE FIELD CAN ACTUALLY USE. Screens/TicketTypesScreens.md section 5.4 row
 * `validation`, with the applicability table of section 6.4.
 *
 * ONLY THE APPLICABLE MEMBERS ARE RENDERED, AND THE REST ARE CLEARED ON A DATA-TYPE CHANGE. The
 * server does NOT cross-check a validation member against the data type -- `ValidateFields` checks
 * each bound in isolation -- so a `minValue` left behind on a SingleLineText is accepted, stored,
 * meaningless, and will be applied by whatever future renderer trusts it. Hiding the control is half
 * the fix; `applyDataTypeChange` clearing the value is the other half, and `toValidationRequest`
 * dropping it on the way out is the belt and braces.
 *
 * WHAT THIS COMPONENT DOES NOT OFFER: `isRequired` (a field-level property, not a validation member,
 * and it lives in FieldDescriptorEditor) and any rule not in FieldValidationDto. There are nine
 * members and there is no tenth to add.
 */
export function ValidationRulesEditor({
  fieldIndex,
  dataType,
}: {
  fieldIndex: number;
  dataType: string;
}) {
  const { control, register } = useFormContext<TicketTypeFormValues>();
  const messageFor = useFieldErrors(control);

  const members = validationMembersFor(dataType);

  if (members.length === 0) {
    return (
      <Typography variant="body2" color="text.secondary">
        {/* Said rather than left blank: an empty region reads as a section that failed to load, and
            an author who expected a min/max here needs to know the data type is why there is none. */}
        This data type has no validation rules.
      </Typography>
    );
  }

  return (
    <Box>
      <Typography variant="subtitle2" gutterBottom>
        Validation
      </Typography>

      <Stack spacing={2}>
        {(appliesTo(dataType, 'minLength') || appliesTo(dataType, 'maxLength')) && (
          <Stack direction={{ xs: 'column', sm: 'row' }} spacing={2}>
            <NullableNumberField
              control={control}
              name={`fields.${fieldIndex}.validation.minLength`}
              label="Minimum length"
              step={1}
              message={messageFor(`fields.${fieldIndex}.validation.minLength`)}
            />
            <NullableNumberField
              control={control}
              name={`fields.${fieldIndex}.validation.maxLength`}
              label="Maximum length"
              step={1}
              message={messageFor(`fields.${fieldIndex}.validation.maxLength`)}
            />
          </Stack>
        )}

        {(appliesTo(dataType, 'minValue') || appliesTo(dataType, 'maxValue')) && (
          <Stack direction={{ xs: 'column', sm: 'row' }} spacing={2}>
            {/* step="any", because these two are a C# `decimal?` behind NUMERIC(18,4): a step of 1
                would make the browser's own validation refuse 12.50 on a MoneyAmount bound. */}
            <NullableNumberField
              control={control}
              name={`fields.${fieldIndex}.validation.minValue`}
              label="Minimum value"
              step="any"
              message={messageFor(`fields.${fieldIndex}.validation.minValue`)}
            />
            <NullableNumberField
              control={control}
              name={`fields.${fieldIndex}.validation.maxValue`}
              label="Maximum value"
              step="any"
              message={messageFor(`fields.${fieldIndex}.validation.maxValue`)}
            />
          </Stack>
        )}

        {(appliesTo(dataType, 'earliestDate') || appliesTo(dataType, 'latestDate')) && (
          <Stack direction={{ xs: 'column', sm: 'row' }} spacing={2}>
            {/* A NATIVE type="date" BOX, NOT A DatePicker, AND DELIBERATELY. `event.target.value` is
                already the "yyyy-MM-dd" string that a C# DateOnly wants, so nothing here ever
                constructs a Date -- and `new Date("2026-09-02")` is midnight UTC, which is the
                previous day west of it (GeneralUIArchitecture.md section 10.2). The DatePicker
                requirement in screen spec section 6.3 is about the RENDERER's Date field, which is an
                answer somebody gives; these two are bounds an author sets, and no document asks for a
                picker here. It also needs no LocalizationProvider. */}
            <TextField
              label="Earliest date"
              type="date"
              size="small"
              {...register(`fields.${fieldIndex}.validation.earliestDate`)}
              error={Boolean(messageFor(`fields.${fieldIndex}.validation.earliestDate`))}
              helperText={messageFor(`fields.${fieldIndex}.validation.earliestDate`)}
              slotProps={{ inputLabel: { shrink: true } }}
              fullWidth
            />
            <TextField
              label="Latest date"
              type="date"
              size="small"
              {...register(`fields.${fieldIndex}.validation.latestDate`)}
              error={Boolean(messageFor(`fields.${fieldIndex}.validation.latestDate`))}
              helperText={messageFor(`fields.${fieldIndex}.validation.latestDate`)}
              slotProps={{ inputLabel: { shrink: true } }}
              fullWidth
            />
          </Stack>
        )}

        {appliesTo(dataType, 'regexPattern') && (
          <TextField
            label="Pattern"
            size="small"
            /**
             * NOT TRIMMED BY THE SCHEMA, unlike every other string on this form: a leading or
             * trailing space is meaningful inside a regular expression, and the server does not trim
             * it either. The schema checks that it COMPILES IN THIS BROWSER, which the server's own
             * check cannot prove -- .NET accepts inline options, atomic groups, `\Z` and
             * `\p{IsGreek}`, all of which throw SyntaxError in `new RegExp`. Screen spec
             * section 5.5 rule G.
             */
            {...register(`fields.${fieldIndex}.validation.regexPattern`)}
            error={Boolean(messageFor(`fields.${fieldIndex}.validation.regexPattern`))}
            helperText={
              messageFor(`fields.${fieldIndex}.validation.regexPattern`) ??
              'A JavaScript regular expression, without the surrounding slashes. Leave empty for no pattern.'
            }
            fullWidth
          />
        )}

        {(appliesTo(dataType, 'allowedFileTypes') || appliesTo(dataType, 'maxFileSizeBytes')) && (
          <>
            {/* SAID PLAINLY, BECAUSE NOTHING ENFORCES EITHER RULE YET. The renderer draws a FileUpload
                as a DISABLED control with an explanatory note (screen spec section 6.9): there is no
                upload endpoint in this release. An author who sets a size limit and is told nothing
                would reasonably conclude uploads work. */}
            <Alert severity="info">
              These two are stored and shown, but nothing enforces them yet: file upload is not
              available in this release, so a file field appears on a form as a disabled control.
            </Alert>
            <TextField
              label="Allowed file types"
              size="small"
              /**
               * ONE COMMA-SEPARATED BOX, NOT A CHIP-PER-ENTRY CONTROL, because the server validates
               * the JOINED string: `RequireLength(string.Join(',', AllowedFileTypes), 500)`
               * (TicketTypeMapper.cs:174-176). A control that checked each extension separately would
               * accept sixty short ones and earn a 422 naming a limit every individual value is
               * inside. Split, trimmed and re-joined on submit, exactly as ToDetail splits it back.
               */
              {...register(`fields.${fieldIndex}.validation.allowedFileTypes`)}
              error={Boolean(messageFor(`fields.${fieldIndex}.validation.allowedFileTypes`))}
              helperText={
                messageFor(`fields.${fieldIndex}.validation.allowedFileTypes`) ??
                'Separate them with commas, for example: pdf, png, jpg'
              }
              fullWidth
            />
            <NullableNumberField
              control={control}
              name={`fields.${fieldIndex}.validation.maxFileSizeBytes`}
              label="Maximum file size (bytes)"
              step={1}
              message={messageFor(`fields.${fieldIndex}.validation.maxFileSizeBytes`)}
            />
          </>
        )}
      </Stack>
    </Box>
  );
}

/**
 * A NUMERIC BOUND WHOSE EMPTY STATE IS `null`, NEVER `0`.
 *
 * `Number('')` is 0 -- a bound the author never typed, indistinguishable from one they did, and one
 * that would then reject every value below zero on every ticket of this type. So the value read out
 * of the DOM is the input's own `valueAsNumber`, and NaN (which is what an empty or unparseable
 * numeric input yields) becomes null. GeneralUIArchitecture.md section 9.3 rule F in control form.
 *
 * A Controller rather than `register(..., { valueAsNumber: true })`: that option yields NaN for an
 * empty box and NaN is not `null`, so it would reach Zod as a type error on a bound the author has
 * simply left blank.
 */
function NullableNumberField({
  control,
  name,
  label,
  step,
  message,
}: {
  control: Control<TicketTypeFormValues>;
  name: FieldPath<TicketTypeFormValues>;
  label: string;
  step: number | 'any';
  message: string | undefined;
}) {
  return (
    <Controller
      control={control}
      name={name}
      render={({ field }) => (
        <TextField
          label={label}
          type="number"
          size="small"
          value={typeof field.value === 'number' ? String(field.value) : ''}
          onChange={(event) => {
            const raw = event.target.value;
            if (raw === '') {
              field.onChange(null);
              return;
            }
            const parsed = (event.target as HTMLInputElement).valueAsNumber;
            field.onChange(Number.isNaN(parsed) ? null : parsed);
          }}
          onBlur={field.onBlur}
          inputRef={field.ref}
          error={Boolean(message)}
          helperText={message ?? 'Leave empty for no limit.'}
          slotProps={{ htmlInput: { step } }}
          fullWidth
        />
      )}
    />
  );
}
