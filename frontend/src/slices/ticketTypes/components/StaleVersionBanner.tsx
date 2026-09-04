import Alert from '@mui/material/Alert';
import AlertTitle from '@mui/material/AlertTitle';
import Button from '@mui/material/Button';
import Stack from '@mui/material/Stack';
import Typography from '@mui/material/Typography';
import type { TicketTypeDetail } from '../types';

/**
 * THE BLOCKING BANNER OF THE STALE CHECK. Screens/TicketTypesScreens.md section 5.6 and
 * GeneralUIArchitecture.md section 9.4.
 *
 * THIS IS A MITIGATION WITH AN OPEN RACE, NOT A FIX, AND IT SAYS SO IN ITS OWN COPY. There is no
 * concurrency token anywhere in the built backend -- `ticket_types` has no version column and
 * `EditTicketTypeHandler` saves with no version predicate -- so between the pre-submit read and the
 * POST another Accountant can still save, and both callers still receive 200 OK. What this banner
 * catches is the common case: two people who opened the same version minutes apart. What it cannot
 * catch is two people who press Save in the same second. The proper fix is a row-version column and a
 * 409, which is item 7 in UI/BACKEND_CHANGES_REQUIRED.md.
 *
 * EXACTLY TWO BUTTONS, AND NEITHER IS *SAVE ANYWAY*. `fields` is a full replacement, so the entire
 * content of the losing save is the other person's work; there is no merge to offer and no way to
 * offer half of one.
 */
export function StaleVersionBanner({
  loadedVersion,
  latest,
  loadedFieldKeys,
  loadedDisplayName,
  loadedCategory,
  loadedAllowEmployeeToOpen,
  loadedAllowSubjectOtherThanCreator,
  onReload,
  onKeepEditing,
}: {
  loadedVersion: number;
  latest: TicketTypeDetail;
  loadedFieldKeys: readonly string[];
  loadedDisplayName: string;
  loadedCategory: string;
  loadedAllowEmployeeToOpen: boolean;
  loadedAllowSubjectOtherThanCreator: boolean;
  onReload: () => void;
  onKeepEditing: () => void;
}) {
  const changes = summarise({
    latest,
    loadedFieldKeys,
    loadedDisplayName,
    loadedCategory,
    loadedAllowEmployeeToOpen,
    loadedAllowSubjectOtherThanCreator,
  });

  return (
    <Alert severity="warning" sx={{ mb: 3 }}>
      {/* Both numbers, named. "This has changed" without them leaves the author unable to tell a
          colleague which version they were working from. */}
      <AlertTitle>
        You are editing version {loadedVersion}. Version {latest.currentVersionNumber} now exists.
      </AlertTitle>

      <Typography variant="body2" sx={{ mb: 1 }}>
        Somebody else saved this ticket type while you were working. Saving now would replace their
        version with yours, and everything they changed would be lost — the field list is replaced
        whole, not merged.
      </Typography>

      {changes.length > 0 && (
        <>
          <Typography variant="body2" sx={{ mb: 0.5 }}>
            What changed since you opened this:
          </Typography>
          <Typography variant="body2" component="ul" sx={{ mb: 1, pl: 3 }}>
            {changes.map((change) => (
              <li key={change}>{change}</li>
            ))}
          </Typography>
        </>
      )}

      {/* ALWAYS SHOWN, whether or not the list above has entries. A field's label, help text,
          validation, group or order can all change with the key set untouched -- eleven properties a
          row -- and a diff of all of them is a screen of its own. Implying the list is exhaustive is
          worse than admitting it is not. */}
      <Typography variant="body2" sx={{ mb: 1 }}>
        {changes.length === 0
          ? 'The version number changed, so something changed — nothing that can be summarised here did.'
          : 'Individual fields may also have been changed in ways not listed here.'}
      </Typography>

      {/* Said to the user, not only in a comment: presenting this as making the problem go away is
          how somebody comes to trust it in the one case it cannot catch. */}
      <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>
        This check reduces the chance of overwriting somebody's work; it cannot rule it out. If two
        people save at the same moment, both saves still report success.
      </Typography>

      <Stack direction="row" spacing={1}>
        <Button variant="contained" color="warning" onClick={onReload}>
          Reload and discard my changes
        </Button>
        {/* Leaves submit BLOCKED. It is a way to copy work out of the form, not a way past the
            check. */}
        <Button onClick={onKeepEditing}>Keep editing</Button>
      </Stack>
    </Alert>
  );
}

/**
 * The summary of section 5.6 item 2: displayName, category, the two flags, and the field-key set added
 * and removed -- everything computable by diffing the freshly read detail against what was loaded.
 *
 * `description` is deliberately absent: it is up to 10,000 characters, and "the description changed"
 * next to a diff of it is either useless or a wall of text. The four scalars above are short enough to
 * quote.
 */
function summarise(input: {
  latest: TicketTypeDetail;
  loadedFieldKeys: readonly string[];
  loadedDisplayName: string;
  loadedCategory: string;
  loadedAllowEmployeeToOpen: boolean;
  loadedAllowSubjectOtherThanCreator: boolean;
}): readonly string[] {
  const { latest } = input;
  const changes: string[] = [];

  if (latest.displayName !== input.loadedDisplayName) {
    changes.push(`Display name is now "${latest.displayName}" (was "${input.loadedDisplayName}").`);
  }
  if (latest.category !== input.loadedCategory) {
    changes.push(`Category is now "${latest.category}" (was "${input.loadedCategory}").`);
  }
  if (latest.allowEmployeeToOpen !== input.loadedAllowEmployeeToOpen) {
    changes.push(
      latest.allowEmployeeToOpen
        ? 'Employees may now open this type.'
        : 'Employees may no longer open this type.',
    );
  }
  if (latest.allowSubjectOtherThanCreator !== input.loadedAllowSubjectOtherThanCreator) {
    changes.push(
      latest.allowSubjectOtherThanCreator
        ? '"Allow subject other than creator" was turned on.'
        : '"Allow subject other than creator" was turned off.',
    );
  }

  /**
   * KEY SETS, COMPARED CASE-INSENSITIVELY, because that is how the server compares them: the
   * uniqueness set is OrdinalIgnoreCase, so "Amount" and "amount" are the same field to it. Comparing
   * them case-sensitively here would report one field added and one removed for a change of case.
   */
  const loaded = new Set(input.loadedFieldKeys.map((key) => key.trim().toLowerCase()));
  const now = new Set(latest.fields.map((field) => field.key.trim().toLowerCase()));

  const added = latest.fields.filter((field) => !loaded.has(field.key.trim().toLowerCase()));
  const removed = input.loadedFieldKeys.filter((key) => !now.has(key.trim().toLowerCase()));

  if (added.length > 0) {
    changes.push(`Fields added: ${added.map((field) => field.key).join(', ')}.`);
  }
  if (removed.length > 0) {
    changes.push(`Fields removed: ${removed.join(', ')}.`);
  }

  return changes;
}
