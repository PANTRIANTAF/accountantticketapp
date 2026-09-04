import { useState, type ReactNode } from 'react';
import { useParams } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import Box from '@mui/material/Box';
import Button from '@mui/material/Button';
import Divider from '@mui/material/Divider';
import IconButton from '@mui/material/IconButton';
import ListItemIcon from '@mui/material/ListItemIcon';
import ListSubheader from '@mui/material/ListSubheader';
import Menu from '@mui/material/Menu';
import MenuItem from '@mui/material/MenuItem';
import Paper from '@mui/material/Paper';
import Snackbar from '@mui/material/Snackbar';
import Stack from '@mui/material/Stack';
import Typography from '@mui/material/Typography';
import DialogContentText from '@mui/material/DialogContentText';
import AlternateEmailIcon from '@mui/icons-material/AlternateEmail';
import BadgeIcon from '@mui/icons-material/Badge';
import LockIcon from '@mui/icons-material/Lock';
import LockOpenIcon from '@mui/icons-material/LockOpen';
import MailOutlinedIcon from '@mui/icons-material/MailOutlined';
import PersonOffIcon from '@mui/icons-material/PersonOff';
import RestoreIcon from '@mui/icons-material/Restore';
import VisibilityIcon from '@mui/icons-material/Visibility';
import VisibilityOffIcon from '@mui/icons-material/VisibilityOff';
import { ConfirmDialog } from '../../../shared/components/ConfirmDialog';
import { ErrorBanner } from '../../../shared/components/ErrorBanner';
import { LoadingRegion } from '../../../shared/components/LoadingRegion';
import { PageHeader } from '../../../shared/components/PageHeader';
import { useAuthenticatedSession } from '../../../shared/auth/useSession';
import { formatDate, formatDateTime } from '../../../shared/format/dates';
import { ROLE_LABELS, UserRole } from '../../../shared/format/enums';
import { can } from '../../../shared/permissions/can';
import { getCustomer } from '../../customers/api';
import {
  useEmployeeDetail,
  useOwnEmployeeRecord,
  useReactivateAccount,
  useSuspendAccount,
} from '../queries';
import { ChangeLoginEmailDialog } from '../components/ChangeLoginEmailDialog';
import { DepartEmployeeDialog } from '../components/DepartEmployeeDialog';
import { EditEmployeeDialog } from '../components/EditEmployeeDialog';
import { EmployeeStatusPair } from '../components/EmployeeStatusPair';
import { InviteEmployeeDialog } from '../components/InviteEmployeeDialog';
import { ReinstateEmployeeDialog } from '../components/ReinstateEmployeeDialog';
import { SetRoleDialog } from '../components/SetRoleDialog';

/**
 * `/employees/:employeeId`. EmployeesScreens.md section 5, plan section 8.
 *
 * A. TWO RESPONSE SHAPES, CHOSEN BY THE SESSION ROLE BEFORE THE CALL. `POST /api/employees/get`
 *    returns `EmployeeDetailDto` to AA/AU/CA and the much narrower `EmployeeSelfDto` to an `Employee`,
 *    and the two hooks below hold them under two different cache keys. NEVER sniffed from the payload:
 *    `'status' in response` works today and breaks SILENTLY the first time a field moves, by sending a
 *    full record down the narrow branch (section 2.3).
 *
 * B. THE `Employee` BRANCH IS NOT A SUBSET TOGGLE. `EmployeeSelf` has no `status`, no `accountStatus`,
 *    no `role`, no `createdAt`, no `employmentEndDate` and NEITHER IDENTIFYING NUMBER -- so there are no
 *    chips, no Identification card and no Actions menu, because none of those fields exists in the
 *    response and an "empty" chip is a rendered `undefined`. The card is rendered from the presence of
 *    the field, not from `can()` (section 5.4).
 *
 * C. A COLLEAGUE'S ID IS A 404, BY DESIGN. GetEmployeeHandler.cs:70 adds a second
 *    `UserAccountId == accountId` filter for the `Employee` role precisely so a colleague's tax number
 *    cannot be read by guessing an id. `ErrorBanner` renders "Not found." for a 404 -- NEVER "forbidden"
 *    and never "no permission", which would tell a user to go and ask for access to a record that does
 *    not exist for them (section 9 rule C).
 *
 * D. THE EMPLOYER NAME IS A SEPARATE QUERY, and its failure is contained. `EmployeeDetail` carries a
 *    `customerId` and no name, so the name comes from `slices/customers/api.ts` -- the one legitimate
 *    cross-slice import (section 1.4 rules C and D), written against that slice's `api.ts` with its
 *    query key reproduced literally so the two share a cache entry without importing its `queries.ts`.
 *    A 404 or a 403 on the Customer SUPPRESSES THE NAME; it never blanks this page and never surfaces as
 *    this record's error.
 *
 * E. THE IDENTIFICATION MASK IS NOT A SECURITY CONTROL and must never be presented as one in review.
 *    Both numbers are in the response and in the network tab. The per-field *Show* toggle is per mount
 *    and never persisted; it stops a tax number sitting on screen during a screen-share, which is the
 *    realistic exposure (section 5.4).
 *
 * F. EVERY MENU ENTRY IS HIDDEN, NOT DISABLED, when the server would refuse it -- and the refusals are
 *    422s about the data's state, not 403s about the caller (section 8.4 rule A, section 8.5). Nothing
 *    here predicts the at-least-one-active-Customer-Admin invariant: the client cannot see other pages,
 *    `EmployeeSummary` has no `accountStatus`, and the guard has an accepted concurrency window, so a
 *    button greyed out on a wrong guess is worse than a 422 the user can at least read.
 *
 * G. *Suspend access* AND *Mark departed* ARE DELIBERATELY FAR APART: different menu groups, a
 *    `Divider` between them, different icons, and only *Mark departed* is red -- the only red button on
 *    the screen. `suspend-account` revokes access WITHOUT ending employment; `depart` does both, in one
 *    transaction, and is reversible only as a correction. They are never a shared "Change status"
 *    submenu and never one toggle (section 8.2).
 *
 * H. NOTHING ON THIS SCREEN RESETS A PASSWORD, in any role, by any route. There is no administrative
 *    password reset in this application. *Restore access* explicitly does not reset one and does not
 *    clear a lockout, and its success copy says so (section 8.5 rule B).
 */
export function EmployeeDetailScreen() {
  const params = useParams();
  const employeeId = params.employeeId ?? '';
  const session = useAuthenticatedSession();
  const isEmployeeRole = session.role === UserRole.Employee;

  // Rule A. Exactly one of these two is enabled, decided above by the role.
  const detail = useEmployeeDetail(employeeId);
  const own = useOwnEmployeeRecord(employeeId);
  const active = isEmployeeRole ? own : detail;

  const record = detail.data;
  const selfRecord = own.data;

  /** Rule D. */
  const customerId = isEmployeeRole ? selfRecord?.customerId : record?.customerId;
  const employer = useQuery({
    queryKey: ['customers', 'detail', customerId ?? ''],
    queryFn: () => getCustomer(customerId ?? ''),
    enabled:
      customerId !== undefined && customerId !== '' && can(session.role, 'ViewCustomer'),
  });
  const employerName = employer.data?.legalName ?? null;

  const [menuAnchor, setMenuAnchor] = useState<HTMLElement | null>(null);
  const [dialog, setDialog] = useState<
    'edit' | 'invite' | 'role' | 'suspend' | 'depart' | 'reinstate' | 'login-email' | null
  >(null);
  const [message, setMessage] = useState<string | null>(null);
  const [showTaxId, setShowTaxId] = useState(false);
  const [showSocialSecurity, setShowSocialSecurity] = useState(false);

  const suspend = useSuspendAccount();
  const reactivate = useReactivateAccount();

  const closeMenu = () => {
    setMenuAnchor(null);
  };
  const openDialog = (which: NonNullable<typeof dialog>) => {
    setMenuAnchor(null);
    setDialog(which);
  };

  if (active.isLoading) return <LoadingRegion label="Loading employee…" minHeight="40vh" />;

  // Rule C. 404 renders "Not found.", never "forbidden".
  if (active.error !== null) {
    return (
      <Box>
        <PageHeader title="Employee" />
        <ErrorBanner error={active.error} />
      </Box>
    );
  }

  // ------------------------------------------------------------------------------------------
  // Rule B: the `Employee` branch. A different screen, not a subset of the one below.
  // ------------------------------------------------------------------------------------------
  if (isEmployeeRole) {
    if (selfRecord === undefined) return null;

    return (
      <Box>
        <PageHeader
          title={`${selfRecord.givenName} ${selfRecord.familyName}`}
          subtitle={selfRecord.jobTitle ?? undefined}
        />
        <Paper variant="outlined" sx={{ p: 3 }}>
          <Stack spacing={2}>
            <Typography variant="h6" component="h2">
              My details
            </Typography>
            <DetailField label="Work email" value={selfRecord.workEmail} />
            <DetailField label="Phone" value={selfRecord.contactPhone} />
            <DetailField label="Started" value={formatDate(selfRecord.employmentStartDate)} />
            {/*
              NO chips, NO identification card, NO Actions menu -- rule B. And no contact-details form:
              `UpdateOwnContactRequestDto` is a full replacement of its two fields, and the form for it
              belongs to /profile, which is blocked on BACKEND_CHANGES_REQUIRED item 12 (plan section 11
              rule A). Reported rather than half-built.
            */}
            <Typography variant="body2" color="text.secondary">
              To correct any of this, ask a Customer Admin at your company.
            </Typography>
          </Stack>
        </Paper>
      </Box>
    );
  }

  if (record === undefined) return null;

  const displayName = `${record.givenName} ${record.familyName}`;
  const isDeparted = record.status === 'Departed';

  /**
   * Rule F. Every one of these mirrors a 422 the server would answer, so the entry is HIDDEN rather
   * than shown and refused. `can()` gates the caller; these gate the record's state. Both must pass.
   */
  const showInvite =
    can(session.role, 'InviteEmployee') &&
    !record.hasAccount &&
    !isDeparted &&
    // 422 "No email address on file for this employee." -- blocked, per section 9.3 rule C.
    record.workEmail !== null;
  const showSetRole = can(session.role, 'SetEmployeeRole') && record.hasAccount;
  const showSuspend =
    can(session.role, 'SuspendEmployeeAccount') &&
    record.hasAccount &&
    record.accountStatus !== 'Suspended';
  const showRestore =
    can(session.role, 'ReactivateEmployeeAccount') &&
    record.hasAccount &&
    // 422 "A departed employee's account cannot be reactivated…" -- *Reinstate* is offered instead, and
    // it restores the account itself, so the two are never both needed (section 8.5 rule A).
    !isDeparted &&
    record.accountStatus === 'Suspended';
  const showChangeLoginEmail =
    can(session.role, 'ChangeEmployeeLoginEmail') && record.hasAccount && !isDeparted;
  const showDepart = can(session.role, 'DepartEmployee') && !isDeparted;
  const showReinstate = can(session.role, 'ReinstateEmployee') && isDeparted;

  const hasAccessGroup = showInvite || showSetRole || showSuspend || showRestore || showChangeLoginEmail;
  const hasEmploymentGroup = showDepart || showReinstate;
  const hasActions = hasAccessGroup || hasEmploymentGroup;

  return (
    <Box>
      <PageHeader
        title={displayName}
        /* Rule D: the employer name when it resolved, and silence when it did not. */
        subtitle={
          employerName === null
            ? (record.jobTitle ?? undefined)
            : `${record.jobTitle === null ? '' : `${record.jobTitle} · `}${employerName}`
        }
        action={
          <Stack direction="row" spacing={1}>
            {/* A Departed record is STILL EDITABLE, deliberately: correcting a misspelled name or a
                wrong tax number after somebody has left is ordinary work (section 5.5 rule D). */}
            {can(session.role, 'UpdateEmployee') && (
              <Button
                variant="outlined"
                onClick={() => {
                  setDialog('edit');
                }}
              >
                Edit details
              </Button>
            )}
            {hasActions && (
              <Button
                variant="contained"
                aria-haspopup="menu"
                onClick={(event) => {
                  setMenuAnchor(event.currentTarget);
                }}
              >
                Actions
              </Button>
            )}
          </Stack>
        }
      />

      <Stack spacing={3}>
        {/* The two vocabularies, always labelled, always from the payload. */}
        <Paper variant="outlined" sx={{ p: 2 }}>
          <EmployeeStatusPair status={record.status} accountStatus={record.accountStatus} />
        </Paper>

        <Stack direction={{ xs: 'column', md: 'row' }} spacing={3} sx={{ alignItems: 'flex-start' }}>
          <Paper variant="outlined" sx={{ p: 3, flex: 1, width: '100%' }}>
            <Stack spacing={2}>
              <Typography variant="h6" component="h2">
                Contact
              </Typography>
              <DetailField label="Work email" value={record.workEmail} />
              <DetailField label="Phone" value={record.contactPhone} />
            </Stack>
          </Paper>

          <Paper variant="outlined" sx={{ p: 3, flex: 1, width: '100%' }}>
            <Stack spacing={2}>
              <Typography variant="h6" component="h2">
                Employment
              </Typography>
              <DetailField label="Started" value={formatDate(record.employmentStartDate)} />
              <DetailField
                label="Ended"
                value={
                  record.employmentEndDate === null ? null : formatDate(record.employmentEndDate)
                }
              />
              {/* `role` is a NULLABLE INTEGER and AccountantAdmin is 0, which is falsy: an explicit
                  `=== null`, and "Not invited" rather than a defaulted "Employee". */}
              <DetailField
                label="Role"
                value={record.role === null ? 'Not invited' : ROLE_LABELS[record.role]}
              />
              <DetailField label="Record created" value={formatDateTime(record.createdAt)} />
            </Stack>
          </Paper>
        </Stack>

        {/*
          Rule E. Rendered because the FIELDS ARE PRESENT -- if they arrived, the caller was entitled to
          them. `can()` is not the gate here, because the `Employee` shape has no such fields at all.
        */}
        <Paper variant="outlined" sx={{ p: 3 }}>
          <Stack spacing={2}>
            <Typography variant="h6" component="h2">
              Identification
            </Typography>
            <Typography variant="body2" color="text.secondary">
              Hidden by default so these numbers are not on screen during a screen-share. This is not a
              security control — anyone who can see this page can read them.
            </Typography>
            <MaskedField
              label="Tax identification number"
              value={record.taxIdentificationNumber}
              shown={showTaxId}
              onToggle={() => {
                setShowTaxId((previous) => !previous);
              }}
            />
            <MaskedField
              label="Social security number"
              value={record.socialSecurityNumber}
              shown={showSocialSecurity}
              onToggle={() => {
                setShowSocialSecurity((previous) => !previous);
              }}
            />
          </Stack>
        </Paper>
      </Stack>

      {/* Rule G. Two groups, a Divider between them, *Mark departed* last. */}
      <Menu open={menuAnchor !== null} anchorEl={menuAnchor} onClose={closeMenu}>
        {hasAccessGroup && <ListSubheader component="li">Access</ListSubheader>}
        {showInvite && (
          <MenuItem
            onClick={() => {
              openDialog('invite');
            }}
          >
            <ListItemIcon>
              <MailOutlinedIcon fontSize="small" />
            </ListItemIcon>
            Invite
          </MenuItem>
        )}
        {showSetRole && (
          <MenuItem
            onClick={() => {
              openDialog('role');
            }}
          >
            <ListItemIcon>
              <BadgeIcon fontSize="small" />
            </ListItemIcon>
            Change role
          </MenuItem>
        )}
        {showChangeLoginEmail && (
          <MenuItem
            onClick={() => {
              openDialog('login-email');
            }}
          >
            <ListItemIcon>
              <AlternateEmailIcon fontSize="small" />
            </ListItemIcon>
            Change login email
          </MenuItem>
        )}
        {showSuspend && (
          <MenuItem
            onClick={() => {
              openDialog('suspend');
            }}
          >
            <ListItemIcon>
              <LockIcon fontSize="small" />
            </ListItemIcon>
            Suspend access
          </MenuItem>
        )}
        {showRestore && (
          /* No ConfirmDialog for this one (section 8: "Restore access — No"), so it fires directly and
             the snackbar carries the copy that matters. */
          <MenuItem
            onClick={() => {
              closeMenu();
              reactivate.mutate(record.id, {
                onSuccess: () => {
                  // Section 8.5 rule B, verbatim in substance: it does NOT reset a password and does
                  // NOT clear a lockout, so it must not promise that anybody can sign in.
                  setMessage(
                    'Access restored. If they cannot sign in, they can reset their own password from the sign-in page — restoring access does not reset a password or clear a lockout.',
                  );
                },
              });
            }}
          >
            <ListItemIcon>
              <LockOpenIcon fontSize="small" />
            </ListItemIcon>
            Restore access
          </MenuItem>
        )}

        {hasAccessGroup && hasEmploymentGroup && <Divider />}

        {hasEmploymentGroup && <ListSubheader component="li">Employment</ListSubheader>}
        {showReinstate && (
          /* Next to *Mark departed* and NOT red: it is a repair (section 8.1). */
          <MenuItem
            onClick={() => {
              openDialog('reinstate');
            }}
          >
            <ListItemIcon>
              <RestoreIcon fontSize="small" />
            </ListItemIcon>
            Reinstate
          </MenuItem>
        )}
        {showDepart && (
          <MenuItem
            onClick={() => {
              openDialog('depart');
            }}
          >
            <ListItemIcon>
              <PersonOffIcon fontSize="small" color="error" />
            </ListItemIcon>
            <Typography color="error">Mark departed</Typography>
          </MenuItem>
        )}
      </Menu>

      {dialog === 'edit' && (
        <EditEmployeeDialog
          open
          employee={record}
          role={session.role}
          onClose={() => {
            setDialog(null);
          }}
          onSaved={(updated) => {
            setDialog(null);
            setMessage(`${updated.givenName} ${updated.familyName} updated.`);
          }}
        />
      )}

      {dialog === 'invite' && (
        <InviteEmployeeDialog
          open
          employee={record}
          onClose={() => {
            setDialog(null);
          }}
          onInvited={(name) => {
            setDialog(null);
            setMessage(`Invitation sent to ${name}.`);
          }}
        />
      )}

      {dialog === 'role' && (
        <SetRoleDialog
          open
          employee={record}
          onClose={() => {
            setDialog(null);
          }}
          onChanged={() => {
            setDialog(null);
            setMessage('Role changed. It takes effect the next time they sign in.');
          }}
        />
      )}

      {dialog === 'login-email' && (
        <ChangeLoginEmailDialog
          open
          employee={record}
          onClose={() => {
            setDialog(null);
          }}
          onChanged={(loginEmail) => {
            setDialog(null);
            // The work email did not move and the detail carries no login email, so this snackbar is
            // the only confirmation there is -- without it the operator runs the change again
            // (section 8.7 rule F).
            setMessage(`${displayName} now signs in as ${loginEmail}. Their work email is unchanged.`);
          }}
        />
      )}

      {/*
        Rule G. Suspension's ConfirmDialog is inline rather than a component of its own: the copy is two
        sentences and there is no form, so a file would add an import without adding a rule. The wording
        is section 8.2's, and it is the sentence that distinguishes this from *Mark departed*.
      */}
      <ConfirmDialog
        open={dialog === 'suspend'}
        title={`Suspend ${displayName}'s access?`}
        confirmLabel="Suspend access"
        /* NOT red. Reversible with *Restore access*, and red is reserved for the departure. */
        confirmColor="primary"
        isPending={suspend.isPending}
        onConfirm={() => {
          suspend.mutate(record.id, {
            onSuccess: () => {
              setDialog(null);
              setMessage(`${displayName}'s access is suspended. They are still employed.`);
            },
          });
        }}
        onClose={() => {
          suspend.reset();
          setDialog(null);
        }}
      >
        <Stack spacing={2}>
          <DialogContentText>
            They stay employed — this only revokes their access. You can restore it at any time.
          </DialogContentText>
          {/* The invariant and the self-action guard are both 422s here, never 403s: the caller has the
              role, the data's state forbids the operation (section 8.4 rules A and D). */}
          <ErrorBanner error={suspend.error} />
          {suspend.error !== null && (
            <DialogContentText variant="body2">
              If this Customer would be left without an active Customer Admin, promote another Employee to
              Customer Admin first, then try again.
            </DialogContentText>
          )}
        </Stack>
      </ConfirmDialog>

      {dialog === 'depart' && (
        <DepartEmployeeDialog
          open
          employee={record}
          onClose={() => {
            setDialog(null);
          }}
          onDeparted={() => {
            setDialog(null);
            setMessage(`${displayName} is marked as departed and their access is suspended.`);
          }}
        />
      )}

      {dialog === 'reinstate' && (
        <ReinstateEmployeeDialog
          open
          employee={record}
          onClose={() => {
            setDialog(null);
          }}
          onReinstated={() => {
            setDialog(null);
            // Says nothing about signing in: the account may have returned as Invited (section 8.1).
            setMessage(`${displayName} is Active again. Check the Access chip for their account state.`);
          }}
        />
      )}

      {/* *Restore access* has no dialog, so its failure needs somewhere to land. */}
      {reactivate.error !== null && <ErrorBanner error={reactivate.error} />}

      <Snackbar
        open={message !== null}
        autoHideDuration={12000}
        onClose={() => {
          setMessage(null);
        }}
        message={message ?? ''}
      />
    </Box>
  );
}

/** One labelled read-only value. `null` is an em dash, never an empty line the reader has to interpret. */
function DetailField({ label, value }: { label: string; value: string | null }) {
  return (
    <Box>
      <Typography variant="caption" color="text.secondary" component="div">
        {label}
      </Typography>
      <Typography variant="body1" component="div">
        {value === null || value.length === 0 ? '—' : value}
      </Typography>
    </Box>
  );
}

/**
 * Rule E. Masked per mount, never persisted, and NOT a security control -- the value is already in the
 * response. There is nothing to fetch when it is revealed, which is exactly why this is ergonomics.
 */
function MaskedField({
  label,
  value,
  shown,
  onToggle,
}: {
  label: string;
  value: string | null;
  shown: boolean;
  onToggle: () => void;
}): ReactNode {
  const hasValue = value !== null && value.length > 0;

  return (
    <Box>
      <Typography variant="caption" color="text.secondary" component="div">
        {label}
      </Typography>
      <Stack direction="row" spacing={1} sx={{ alignItems: 'center' }}>
        <Typography variant="body1" component="div">
          {!hasValue ? '—' : shown ? value : '••••••••'}
        </Typography>
        {hasValue && (
          <IconButton
            size="small"
            aria-label={shown ? `Hide ${label}` : `Show ${label}`}
            onClick={onToggle}
          >
            {shown ? <VisibilityOffIcon fontSize="small" /> : <VisibilityIcon fontSize="small" />}
          </IconButton>
        )}
      </Stack>
    </Box>
  );
}
