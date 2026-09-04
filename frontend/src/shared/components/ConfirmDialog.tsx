import { useId, type ReactNode } from 'react';
import Button from '@mui/material/Button';
import CircularProgress from '@mui/material/CircularProgress';
import Dialog from '@mui/material/Dialog';
import DialogActions from '@mui/material/DialogActions';
import DialogContent from '@mui/material/DialogContent';
import DialogTitle from '@mui/material/DialogTitle';

/**
 * Confirmation for an operation that is irreversible OR COSTLY TO UNDO.
 * GeneralUIArchitecture.md section 8.3.
 *
 * IT NAMES THE CONSEQUENCE; IT DOES NOT ASK "ARE YOU SURE?". "Are you sure?" is a click the user
 * learns to make without reading. The reference case and its required copy are
 * Screens/EmployeesScreens.md section 8.1: POST /api/employees/depart suspends the account in the
 * same transaction, and it is reversible only as a CORRECTION, through /api/employees/reinstate --
 * somebody who genuinely left and came back is registered as a new record instead. The dialog must
 * say that.
 *
 * AND IT MUST NOT BE SOFTENED INTO "YOU CAN ALWAYS UNDO THIS." Reinstate exists, so the old
 * "irreversible" copy is wrong -- but the distinction between correcting a mistake and re-employing
 * a person is the whole feature, the server cannot tell the two apart, and the audit entry records
 * only which one the caller chose. A dialog that implies the operation is casually reversible loses
 * the distinction the copy exists to carry.
 *
 * NOTHING IN PHASE 0 USES IT. It exists so the first slice that needs one does not invent a local
 * dialog with softer copy.
 *
 * `children` is a ReactNode rather than a string on purpose: the reference dialog has three
 * paragraphs AND a required end-date field inside it.
 */
export function ConfirmDialog({
  open,
  title,
  children,
  confirmLabel,
  cancelLabel = 'Cancel',
  confirmColor = 'primary',
  confirmDisabled = false,
  isPending = false,
  onConfirm,
  onClose,
}: {
  open: boolean;
  /** Names the record and the consequence: "Mark Jane Doe as departed?" */
  title: string;
  children: ReactNode;
  /** The verb, matching the button that opened it: "Mark departed", not "OK". */
  confirmLabel: string;
  cancelLabel?: string;
  /**
   * `error` for the destructive direction only. A repair -- Reinstate, Restore access -- is not red
   * (Screens/EmployeesScreens.md section 8.2).
   */
  confirmColor?: 'primary' | 'error';
  /** For a dialog whose body holds a required field that is not valid yet. */
  confirmDisabled?: boolean;
  /** The mutation is in flight. */
  isPending?: boolean;
  onConfirm: () => void;
  onClose: () => void;
}) {
  const titleId = useId();
  const contentId = useId();

  return (
    <Dialog
      open={open}
      // Closing is always allowed -- including on backdrop click and Escape. A dialog the user
      // cannot back out of is how a destructive action gets confirmed by accident.
      onClose={isPending ? undefined : onClose}
      aria-labelledby={titleId}
      aria-describedby={contentId}
      maxWidth="sm"
      fullWidth
    >
      <DialogTitle id={titleId}>{title}</DialogTitle>
      <DialogContent id={contentId}>{children}</DialogContent>
      <DialogActions>
        <Button onClick={onClose} disabled={isPending}>
          {cancelLabel}
        </Button>
        {/* The button disables while ITS mutation is pending and shows a small spinner
            (section 7.4). The dialog body is never disabled: the user may still want to fix the
            end date while the request is in flight. */}
        <Button
          onClick={onConfirm}
          color={confirmColor}
          variant="contained"
          disabled={isPending || confirmDisabled}
          startIcon={isPending ? <CircularProgress size={16} color="inherit" /> : undefined}
        >
          {confirmLabel}
        </Button>
      </DialogActions>
    </Dialog>
  );
}
