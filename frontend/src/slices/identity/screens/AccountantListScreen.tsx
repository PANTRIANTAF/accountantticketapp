import { useRef, useState } from 'react';
import type { UseMutateFunction } from '@tanstack/react-query';
import Alert from '@mui/material/Alert';
import Button from '@mui/material/Button';
import Snackbar from '@mui/material/Snackbar';
import Typography from '@mui/material/Typography';
import { ApiError } from '../../../shared/api/ApiError';
import { DEFAULT_PAGE_SIZE, type PaginatedResponse } from '../../../shared/api/paginated';
import { useAuthenticatedSession } from '../../../shared/auth/useSession';
import { AccessDeniedPage } from '../../../shared/components/AccessDeniedPage';
import { ConfirmDialog } from '../../../shared/components/ConfirmDialog';
import { ErrorBanner } from '../../../shared/components/ErrorBanner';
import { PageHeader } from '../../../shared/components/PageHeader';
import { UserRole } from '../../../shared/format/enums';
import { can } from '../../../shared/permissions/can';
import { AccountantAdminTable } from '../components/AccountantAdminTable';
import { AccountantNameTable } from '../components/AccountantNameTable';
import { InviteAccountantDialog } from '../components/InviteAccountantDialog';
import {
  useAccountantList,
  useDemoteAccountant,
  usePromoteAccountant,
  useReactivateAccountant,
  useSuspendAccountant,
} from '../queries';
import type { AccountantDetail } from '../types';

/**
 * /accountants -- one route, TWO RESPONSE SHAPES, and this screen owns the branch.
 *
 * It owns exactly three things and delegates everything else: the role branch, the page state, and the
 * row-action error banner. The tables render, the row menu decides which affordances exist, the dialog
 * invites, and queries.ts owns every caching decision.
 *
 * A. THE BRANCH IS ON `session.role`, NEVER ON A FIELD'S PRESENCE (IdentityScreens.md section 2 rules
 *    A and B). The server's own branch is `user.Role == UserRole.AccountantAdmin`
 *    (ListAccountantsHandler.cs:77), so mirroring that exact condition is the only discrimination that
 *    cannot drift. `if ('loginEmail' in row)` is wrong in a way that passes review: `lastLoginAt` is
 *    legitimately null for anybody who has never signed in, so an optional field that happens to be
 *    null looks exactly like the narrow shape, and the Admin would get the name-only rendering for a
 *    never-signed-in colleague on some rows and not others.
 * B. `session.role === UserRole.AccountantAdmin`, NEVER `if (session.role)`. AccountantAdmin is 0 and 0
 *    is falsy, so a truthiness test takes the AccountantUser path for the most privileged role in the
 *    system and the Accountant Admin sees a one-column table (section 10.1).
 * C. THE UI IS NOT HIDING ANYTHING FOR SECURITY. The narrow shape comes from the server, which omits the
 *    five keys entirely. If a withheld field were ever to arrive here, the fix is a server change and a
 *    punch-list entry, not a filter in this file (02-AuthorizationMatrix.md section 12 rule 2).
 * D. NOTHING IS COUNTED CLIENT-SIDE TO PRE-EMPT A 422. Above all, the number of Accountant Admins: this
 *    screen holds one page of a paginated list, so the count is wrong as soon as there are more
 *    Accountants than fit on a page, and a wrong count would disable a legal action with nobody more
 *    powerful able to re-enable it -- Accountant Admin is the ceiling (matrix section 12 rule 6).
 * E. A 404 FROM A ROW ACTION MEANS "not found OR not visible to you" (AccountInvariants.cs:58-71 filters
 *    by role inside the lookup), so nothing here renders "forbidden", "denied" or "no permission" for
 *    one. ErrorBanner's taxonomy already says "Not found.".
 */
export function AccountantListScreen() {
  const session = useAuthenticatedSession();

  // Rule B. A named constant, compared with ===, once.
  const isAccountantAdmin = session.role === UserRole.AccountantAdmin;

  // 1-BASED, the server's own convention. PaginatedTable owns the only 0-based conversion in the app,
  // so nothing here ever adds or subtracts 1.
  const [pageNumber, setPageNumber] = useState(1);
  const [pageSize, setPageSize] = useState(DEFAULT_PAGE_SIZE);

  const [isInviteOpen, setIsInviteOpen] = useState(false);
  const [toast, setToast] = useState<string | null>(null);

  /**
   * The row action awaiting confirmation. ONE ConfirmDialog for both destructive actions rather than
   * two nearly identical ones, and it exists for `suspend` and `demote` only (section 6 rule D):
   * reactivate and promote GRANT capability and are reversible, so they submit directly.
   */
  const [pending, setPending] = useState<{
    action: 'suspend' | 'demote';
    row: AccountantDetail;
  } | null>(null);

  /**
   * The row-action error, held here rather than read off whichever mutation failed last, so that a
   * banner from an earlier action cannot outlive it. `seq` keys the banner, which is what makes focus
   * move again on a second, identical failure (section 8.4 rule 3) -- a remount is the only way
   * ErrorBanner's focus-on-mount fires twice.
   */
  const [rowError, setRowError] = useState<{ seq: number; error: unknown } | null>(null);
  const errorSeq = useRef(0);

  const list = useAccountantList({ pageNumber, pageSize });

  const suspend = useSuspendAccountant();
  const reactivate = useReactivateAccountant();
  const promote = usePromoteAccountant();
  const demote = useDemoteAccountant();

  const changePage = (next: number) => {
    setPageNumber(next);
  };

  const changePageSize = (next: number) => {
    setPageSize(next);
    // Page 4 of a 15-row list is not page 4 of a 50-row one; staying put would ask for a page past the
    // end and land on the over-run EmptyState immediately after changing the size.
    setPageNumber(1);
  };

  /**
   * The one path all four row actions take. It clears the previous banner, sends the id, and turns a
   * success into a Snackbar and a failure into the banner above the table.
   *
   * NO `catch` THAT SWALLOWS ANYTHING (section 6 rule B). The row menu hides an action the server would
   * refuse, but hiding is an affordance and not a guarantee: a stale list, a second tab, or a can.ts
   * edited without AccountantRowMenu.tsx all put the request on the wire.
   */
  const runRowAction = (
    mutate: UseMutateFunction<AccountantDetail, Error, string>,
    row: AccountantDetail,
    successMessage: string,
  ) => {
    setRowError(null);

    mutate(row.id, {
      onSuccess: () => {
        setToast(successMessage);
      },
      onError: (error) => {
        errorSeq.current += 1;
        setRowError({ seq: errorSeq.current, error });
      },
    });
  };

  const onSuspend = (row: AccountantDetail) => {
    setPending({ action: 'suspend', row });
  };

  const onDemote = (row: AccountantDetail) => {
    setPending({ action: 'demote', row });
  };

  const onReactivate = (row: AccountantDetail) => {
    runRowAction(reactivate.mutate, row, `${row.displayName} can sign in again.`);
  };

  const onPromote = (row: AccountantDetail) => {
    // Rule E of section 6: the promoted user's own cookie still says AccountantUser and the new
    // permission arrives when they next sign in -- up to eight hours later, because there is no
    // server-side session store. Without this sentence the operator watches nothing happen and does it
    // again. (The exact wording is unspecified; flagged.)
    runRowAction(
      promote.mutate,
      row,
      `${row.displayName} is now an Accountant Admin. The new permissions take effect the next time they sign in.`,
    );
  };

  const confirmPending = () => {
    if (pending === null) return;

    const { action, row } = pending;
    setPending(null);

    if (action === 'suspend') {
      runRowAction(
        suspend.mutate,
        row,
        `${row.displayName} has been suspended and cannot sign in until reactivated.`,
      );
      return;
    }

    runRowAction(
      demote.mutate,
      row,
      `${row.displayName} is now an Accountant User. The change takes effect the next time they sign in.`,
    );
  };

  /**
   * A query 403 is reachable only if RequireRole and can.ts disagree -- a client bug, not a state to
   * design around (section 6.2 rule B). Every other status the query can produce belongs to
   * ErrorBanner, which PaginatedTable renders in place of the rows.
   */
  if (list.error instanceof ApiError && list.error.status === 403) {
    return <AccessDeniedPage />;
  }

  const canInvite = can(session.role, 'InviteAccountant');

  return (
    <>
      <PageHeader
        title="Accountants"
        // MANDATORY for an AccountantUser (section 4.1): without it the one-column screen reads as
        // broken rather than scoped. 02-AuthorizationMatrix.md section 2 is why this role sees the list
        // at all -- assigning a ticket requires knowing who exists.
        {...(isAccountantAdmin
          ? {}
          : { subtitle: 'Names only. Account details are managed by an Accountant Admin.' })}
        {...(canInvite
          ? {
              action: (
                <Button
                  variant="contained"
                  onClick={() => {
                    setIsInviteOpen(true);
                  }}
                >
                  Invite Accountant
                </Button>
              ),
            }
          : {})}
      />

      {/* ABOVE THE TABLE, verbatim from `title`, never attached to a control: ProblemDetails carries no
          field map, so there is nothing to highlight even for a 422 (section 7.3). */}
      {rowError !== null && (
        <ErrorBanner key={rowError.seq} error={rowError.error} />
      )}
      {isLastActiveAdminError(rowError?.error) && (
        // Section 6 rule C: this 422 is NOT the operator's mistake -- the server counts Active
        // Accountant Admins after the write, inside the transaction, and rolls back. The verbatim title
        // says what happened; this says what to do about it. It is a separate element because
        // ErrorBanner renders `title` and nothing else, and the title must stay verbatim.
        <Typography variant="body2" color="text.secondary" sx={{ mt: 1, mb: 2 }}>
          Promote another Accountant to Accountant Admin first.
        </Typography>
      )}

      {isAccountantAdmin ? (
        <AccountantAdminTable
          // THE ONE SANCTIONED CAST, and only in the branch the server's own condition put us in
          // (IdentityScreens.md section 2's own sample does the same with `rows as AccountantDetail[]`).
          // A `satisfies` or a type predicate cannot help here: the evidence for the wide shape is the
          // caller's role, which is not a property of the row.
          data={list.data as PaginatedResponse<AccountantDetail> | undefined}
          isLoading={list.isLoading}
          isFetching={list.isFetching}
          error={list.error}
          isOverrunPage={list.isOverrunPage}
          onPageChange={changePage}
          onPageSizeChange={changePageSize}
          role={session.role}
          currentUserId={session.userId}
          onSuspend={onSuspend}
          onReactivate={onReactivate}
          onPromote={onPromote}
          onDemote={onDemote}
        />
      ) : (
        <AccountantNameTable
          // No cast: AccountantDetail extends AccountantSummary, so the union is already assignable to
          // the narrow shape. This table cannot read a withheld field even if one arrived.
          data={list.data}
          isLoading={list.isLoading}
          isFetching={list.isFetching}
          error={list.error}
          isOverrunPage={list.isOverrunPage}
          onPageChange={changePage}
          onPageSizeChange={changePageSize}
        />
      )}

      {/* Mounted for an Accountant Admin only. `can()` gates the affordance; the server enforces it
          (IdentityActionCatalogue.cs:25). */}
      {canInvite && (
        <InviteAccountantDialog
          open={isInviteOpen}
          onClose={() => {
            setIsInviteOpen(false);
          }}
          onInvited={(email) => {
            setIsInviteOpen(false);
            // The address the operator TYPED, not the normalised one from the 201 body: they should
            // recognise what they just entered. The list invalidation is the mutation hook's.
            setToast(`Invitation sent to ${email}.`);
          }}
        />
      )}

      {/*
        Section 6 rule D: suspend and demote only, and the body NAMES THE CONSEQUENCE rather than asking
        "are you sure?" -- a question nobody reads twice. `error` colouring for both, because both remove
        capability.
      */}
      <ConfirmDialog
        open={pending !== null}
        title={
          pending === null
            ? ''
            : pending.action === 'suspend'
              ? `Suspend ${pending.row.displayName}?`
              : `Demote ${pending.row.displayName} to Accountant User?`
        }
        confirmLabel={pending?.action === 'suspend' ? 'Suspend' : 'Demote'}
        confirmColor="error"
        isPending={suspend.isPending || demote.isPending}
        onConfirm={confirmPending}
        onClose={() => {
          setPending(null);
        }}
      >
        <Typography variant="body2">
          {pending === null
            ? ''
            : pending.action === 'suspend'
              ? `${pending.row.displayName} will be unable to sign in until reactivated.`
              : `${pending.row.displayName} will lose the ability to invite, suspend, promote and demote Accountants, to create Customers, and to read the audit log. The change takes effect the next time they sign in.`}
        </Typography>
      </ConfirmDialog>

      {/*
        SUCCESSES ARE THE ONLY TOASTS IN THIS APPLICATION (section 5.3). A failure is never a toast: it
        disappears, and an error the user may need to read twice or quote in a support call must not.
      */}
      <Snackbar
        open={toast !== null}
        autoHideDuration={6000}
        onClose={() => {
          setToast(null);
        }}
        anchorOrigin={{ vertical: 'bottom', horizontal: 'center' }}
      >
        <Alert
          severity="success"
          variant="filled"
          onClose={() => {
            setToast(null);
          }}
        >
          {toast}
        </Alert>
      </Snackbar>
    </>
  );
}

/**
 * The one 4xx on this screen that needs a second sentence, matched on the EXACT title
 * (AccountInvariants.cs:46). Matching a substring or a status code alone would attach "Promote another
 * Accountant first" to "That account is already suspended.", which would be nonsense.
 */
const LAST_ACTIVE_ADMIN_TITLE = 'At least one active Accountant Admin must remain.';

function isLastActiveAdminError(error: unknown): boolean {
  return error instanceof ApiError && error.title === LAST_ACTIVE_ADMIN_TITLE;
}
