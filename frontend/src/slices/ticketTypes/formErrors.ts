import { useCallback } from 'react';
import { useFormState, type Control, type FieldPath } from 'react-hook-form';
import type { TicketTypeFormValues } from './schemas';

/**
 * ONE WAY TO READ ONE FIELD'S ERROR MESSAGE, used by all four editor components.
 *
 * WHY A HOOK RATHER THAN `formState.errors` AT EACH SITE. The editor's error paths are deep --
 * `fields.3.validation.maxLength`, `fields.3.conditionalVisibility.fieldKey`,
 * `fields.3.choiceOptions.1.value` -- and reaching into the nested `errors` object by hand needs a
 * cast at every step, because RHF types an array's error entry as an array of error objects and Zod's
 * array-level issues land on the array itself. `control.getFieldState(name, formState)` already does
 * that walk, is typed, and takes the path as a `FieldPath<TicketTypeFormValues>` -- so a typo in a
 * path is a compile error rather than an error message that silently never appears.
 *
 * WHY useFormState AND NOT THE FORM'S OWN formState. Subscribing here subscribes THIS component,
 * so a validation change re-renders the rows that changed rather than the whole editor. A twelve-row
 * type with four controls a row is otherwise a full re-render on every keystroke that changes a
 * message.
 *
 * `getFieldState` needs a subscribed formState to be passed explicitly when the caller has not
 * subscribed itself -- that is what its second parameter is for, and it is why the two are always
 * used together here.
 */
export function useFieldErrors(
  control: Control<TicketTypeFormValues>,
): (name: FieldPath<TicketTypeFormValues>) => string | undefined {
  const formState = useFormState({ control });

  return useCallback(
    (name: FieldPath<TicketTypeFormValues>) =>
      control.getFieldState(name, formState).error?.message,
    [control, formState],
  );
}
