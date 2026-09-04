import Alert from '@mui/material/Alert';
import DialogContentText from '@mui/material/DialogContentText';
import Stack from '@mui/material/Stack';
import { ConfirmDialog } from '../../../shared/components/ConfirmDialog';
import { ErrorBanner } from '../../../shared/components/ErrorBanner';
import { useReinstateEmployee } from '../queries';
import type { EmployeeDetail } from '../types';

/**
 * POST /api/employees/reinstate. EmployeesScreens.md section 8.1, plan section 12 rules A-C.
 *
 * THIS ENDPOINT WAS UNREACHABLE BY EVERY ROLE UNTIL 2026-09-02. Its handler calls
 * `RequireAsync(user, "ReinstateEmployee")`, the name was missing from `EmployeesActionCatalogue.cs`,
 * and `PermissionChecker` is fail-closed on an unrecognised name -- so the route answered 403 to
 * everybody, an AccountantAdmin included, and wrote a `PermissionDenied` audit entry against a person
 * who was entitled to the action. The catalogue now declares it at line 53 (AccountantAdmin,
 * AccountantUser, CustomerAdmin), which was VERIFIED IN THE WORKING TREE before this component was
 * written -- `Slices/Employees/` is untracked, so no commit stands behind the fix and a lost working
 * tree silently restores the bug. A `can()` of `true` against a guaranteed 403 is the exact defect
 * GeneralUIArchitecture.md section 6.2 rule B names, which is why the check was cheap and the omission
 * was not.
 *
 * A. IT IS A CORRECTION, NOT A RE-HIRE, AND THE COPY IS THE ONLY THING THAT CARRIES THAT.
 *    ReinstateEmployeeHandler.cs:24-27 and 02-AuthorizationMatrix.md section 4: somebody who genuinely
 *    left and came back is registered again as a NEW record, so the two periods of employment stay
 *    separate and each keeps its own tickets. The two choices are one click apart, the server cannot
 *    tell them apart, and the audit entry records only which one the caller chose. Nothing can enforce
 *    it. The copy is the whole control.
 *
 * B. IT IS NOT RED. Section 8.2 reserves `error` for the destructive direction; this is a repair. It
 *    sits in the *Employment* group next to *Mark departed*, and appears ONLY when
 *    `status === "Departed"`.
 *
 * C. IT RESTORES THE ACCOUNT ITSELF, so *Restore access* is not a second step -- and the account may
 *    come back as `Invited` rather than `Active`, which is what happens to somebody who was invited but
 *    never accepted before they were departed (ReinstateEmployeeHandler.cs:97-98). So NO SUCCESS COPY
 *    MAY CLAIM THEY CAN SIGN IN: the `Access:` chip after invalidation is the truth, and this dialog
 *    says only that access is restored in the same step.
 *
 * D. ERRORS, VERBATIM, DIALOG LEFT OPEN: 422 "This employee has not departed." (:67) -- a stale row,
 *    which the invalidation corrects; 422 "This customer is not active." (:74) -- a suspended Customer
 *    gains nobody, and only an Accountant can lift that, so a Customer Admin who sees it has to
 *    escalate rather than retry.
 */
export function ReinstateEmployeeDialog({
  open,
  employee,
  onClose,
  onReinstated,
}: {
  open: boolean;
  employee: EmployeeDetail;
  onClose: () => void;
  onReinstated: () => void;
}) {
  const reinstate = useReinstateEmployee();
  const displayName = `${employee.givenName} ${employee.familyName}`;

  const close = () => {
    reinstate.reset();
    onClose();
  };

  return (
    <ConfirmDialog
      open={open}
      title={`Reinstate ${displayName}?`}
      confirmLabel="Reinstate"
      /* Rule B: a repair is not red. */
      confirmColor="primary"
      isPending={reinstate.isPending}
      onConfirm={() => {
        reinstate.mutate(employee.id, {
          onSuccess: () => {
            onReinstated();
          },
        });
      }}
      onClose={close}
    >
      <Stack spacing={2}>
        {/* Rules A and C. */}
        <DialogContentText>
          Use this only to correct a departure that should not have been recorded. {employee.givenName}{' '}
          returns to Active and their access is restored in the same step — you do not also need{' '}
          <em>Restore access</em>.
        </DialogContentText>

        {/* Rule A, the half a builder is tempted to soften. */}
        <Alert severity="warning">
          If they left and have now come back, do not use this. Register them again as a new Employee, so
          the two periods of employment stay separate.
        </Alert>

        {/* Rule C. Restoring the account is not the same as being able to sign in. */}
        <DialogContentText variant="body2">
          If they were invited but never signed in, their access returns to Invited rather than Active.
          The Access chip on this page after reinstating is the accurate answer.
        </DialogContentText>

        {/* Rule D. */}
        <ErrorBanner error={reinstate.error} />
      </Stack>
    </ConfirmDialog>
  );
}
