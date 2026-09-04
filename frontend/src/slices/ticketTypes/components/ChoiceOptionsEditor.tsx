import AddIcon from '@mui/icons-material/Add';
// `DeleteOutlined`, not `DeleteOutline`: this version of @mui/icons-material ships 2,140 `*Outlined`
// modules and exactly one `*Outline`, so the older alias is a build failure rather than a fallback.
import DeleteOutlinedIcon from '@mui/icons-material/DeleteOutlined';
import Box from '@mui/material/Box';
import Button from '@mui/material/Button';
import FormHelperText from '@mui/material/FormHelperText';
import IconButton from '@mui/material/IconButton';
import Stack from '@mui/material/Stack';
import TextField from '@mui/material/TextField';
import Typography from '@mui/material/Typography';
import { useFieldArray, useFormContext } from 'react-hook-form';
import { useFieldErrors } from '../formErrors';
import { blankChoiceOption, type TicketTypeFormValues } from '../schemas';

/**
 * THE OPTION ROWS OF A SingleChoice OR MultipleChoice FIELD.
 * Screens/TicketTypesScreens.md section 5.4, row `choiceOptions`.
 *
 * RENDERED ONLY FOR THE TWO CHOICE TYPES -- FieldDescriptorEditor decides that, because the same
 * decision drives the data-type transition that seeds and clears these rows (schemas.ts
 * applyDataTypeChange) and the two must not be able to disagree.
 *
 * `value` IS WHAT IS STORED AND COMPARED; `label` IS WHAT IS SHOWN. A conditionalVisibility rule
 * matches against the option's VALUE (screen spec section 6.5), a stored answer holds the VALUE, and
 * the label is what somebody renames next week. So `value` is required here and `label` is not --
 * a blank label falls back to nothing on screen, which is cosmetic, while a blank value is an answer
 * that cannot be told apart from "not answered".
 */
export function ChoiceOptionsEditor({ fieldIndex }: { fieldIndex: number }) {
  const { control, register } = useFormContext<TicketTypeFormValues>();
  const messageFor = useFieldErrors(control);

  const { fields, append, remove } = useFieldArray({
    control,
    name: `fields.${fieldIndex}.choiceOptions`,
  });

  /**
   * The array-level message -- "A choice field needs at least two options." -- lands on the array
   * itself, not on a row, because there is no row for it to land on. Both directions of the rule are
   * a 422 server-side (TicketTypeMapper.cs:180-184).
   */
  const arrayMessage = messageFor(`fields.${fieldIndex}.choiceOptions`);

  return (
    <Box>
      <Typography variant="subtitle2" gutterBottom>
        Options
      </Typography>

      <Stack spacing={1}>
        {fields.map((option, optionIndex) => (
          <Stack key={option.id} direction="row" spacing={1} sx={{ alignItems: 'flex-start' }}>
            <TextField
              label="Label"
              size="small"
              // Trimmed by the schema, because the server never trims a choice option: options are
              // stored as one JSON string (TicketTypeMapper.cs:62) and NormalizeFields does not look
              // inside it.
              {...register(`fields.${fieldIndex}.choiceOptions.${optionIndex}.label`)}
              error={Boolean(
                messageFor(`fields.${fieldIndex}.choiceOptions.${optionIndex}.label`),
              )}
              helperText={messageFor(`fields.${fieldIndex}.choiceOptions.${optionIndex}.label`)}
              fullWidth
            />
            <TextField
              label="Value"
              size="small"
              {...register(`fields.${fieldIndex}.choiceOptions.${optionIndex}.value`)}
              error={Boolean(
                messageFor(`fields.${fieldIndex}.choiceOptions.${optionIndex}.value`),
              )}
              helperText={
                messageFor(`fields.${fieldIndex}.choiceOptions.${optionIndex}.value`) ??
                'Stored with every answer. Rules compare against this, not the label.'
              }
              fullWidth
            />
            {/* NEVER DISABLED AT TWO ROWS. The rule is enforced in Zod, where the message says what
                is wrong; a button that stops responding says only that something is. Removing the
                second option produces an inline error, and the author can see which row to add. */}
            <IconButton
              aria-label={`Remove option ${String(optionIndex + 1)}`}
              onClick={() => {
                remove(optionIndex);
              }}
              sx={{ mt: 0.5 }}
            >
              <DeleteOutlinedIcon />
            </IconButton>
          </Stack>
        ))}
      </Stack>

      {arrayMessage !== undefined && <FormHelperText error>{arrayMessage}</FormHelperText>}

      <Button
        size="small"
        startIcon={<AddIcon />}
        onClick={() => {
          append(blankChoiceOption());
        }}
        sx={{ mt: 1 }}
      >
        Add option
      </Button>
    </Box>
  );
}
