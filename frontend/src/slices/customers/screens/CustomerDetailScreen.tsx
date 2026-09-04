import { useState, type ReactNode } from 'react';
import { Link as RouterLink, useParams } from 'react-router-dom';
import ArrowForwardIcon from '@mui/icons-material/ArrowForward';
import MoreVertIcon from '@mui/icons-material/MoreVert';
import Button from '@mui/material/Button';
import Card from '@mui/material/Card';
import CardContent from '@mui/material/CardContent';
import CardHeader from '@mui/material/CardHeader';
import Divider from '@mui/material/Divider';
import IconButton from '@mui/material/IconButton';
import Menu from '@mui/material/Menu';
import MenuItem from '@mui/material/MenuItem';
import Snackbar from '@mui/material/Snackbar';
import Stack from '@mui/material/Stack';
import Typography from '@mui/material/Typography';
import { ApiError } from '../../../shared/api/ApiError';
import { AccessDeniedPage } from '../../../shared/components/AccessDeniedPage';
import { ErrorBanner } from '../../../shared/components/ErrorBanner';
import { LoadingRegion } from '../../../shared/components/LoadingRegion';
import { NotFoundPage } from '../../../shared/components/NotFoundPage';
import { PageHeader } from '../../../shared/components/PageHeader';
import { StatusChip } from '../../../shared/components/StatusChip';
import { useAuthenticatedSession } from '../../../shared/auth/useSession';
import { formatDate, formatDateTime } from '../../../shared/format/dates';
import { can } from '../../../shared/permissions/can';
import { EditCustomerContactDialog } from '../components/EditCustomerContactDialog';
import { EditCustomerLegalDialog } from '../components/EditCustomerLegalDialog';
import { ReactivateCustomerDialog } from '../components/ReactivateCustomerDialog';
import { SuspendCustomerDialog } from '../components/SuspendCustomerDialog';
import { useCustomer } from '../queries';

/**
 * Route /customers/:customerId, RequireRole'd to AccountantAdmin and AccountantUser
 * (GeneralUIArchitecture.md section 4.1). A CustomerAdmin reads its own Customer at /my-customer
 * instead; nothing Customer-side links here.
 *
 * THERE IS NO /customers/:customerId/edit ROUTE. Both edit forms are dialogs on this screen
 * (CustomersScreens.md section 2), because each touches four to seven fields of a record already on
 * screen.
 *
 * NO DELETE, ARCHIVE, REMOVE OR MERGE. 02-AuthorizationMatrix.md section 3: "Delete a Customer --
 * Nobody. Customers are never deleted." Suspension is the only removal and it is reversible.
 *
 * NO EMPLOYEE OR CUSTOMER ADMIN COUNT, and no suspension reason. CustomerDto carries neither count,
 * and the reason is written into the audit log only (SuspendCustomerHandler.cs:56-67), which only an
 * AccountantAdmin may read. Both are in the plan's section 15 as backend questions.
 *
 * IF customerId EVER ARRIVES AS 'new', THE ROUTE TABLE IS WRONG. react-router ranks the static
 * /customers/new above this dynamic segment regardless of declaration order, so no guard is added
 * here: a guard would hide the fault behind a ?customerId=new 400 whose title names a C# parameter.
 */

/** A label/value row. Local on purpose: shared/ is Phase 0's and this slice creates nothing in it. */
function DetailRow({ label, value }: { label: string; value: ReactNode }) {
  return (
    <Stack direction="row" spacing={2} sx={{ alignItems: 'baseline' }}>
      <Typography variant="body2" color="text.secondary" sx={{ minWidth: 140, flexShrink: 0 }}>
        {label}
      </Typography>
      <Typography variant="body2" component="div">
        {value}
      </Typography>
    </Stack>
  );
}

/** An em dash for an absent optional; a blank value reads as data that failed to load. */
const orDash = (value: string | null): string => (value === null || value === '' ? '—' : value);

type OpenDialog = 'legal' | 'contact' | 'suspend' | 'reactivate' | null;

export function CustomerDetailScreen() {
  const { customerId } = useParams<{ customerId: string }>();
  const { role } = useAuthenticatedSession();
  const query = useCustomer(customerId);

  const [openDialog, setOpenDialog] = useState<OpenDialog>(null);
  const [menuAnchor, setMenuAnchor] = useState<HTMLElement | null>(null);
  const [toast, setToast] = useState<string | null>(null);

  /**
   * AFFORDANCE GATES, EXACTLY AS CustomersScreens.md SECTION 4.4 TABLES THEM, each verified against
   * CustomersActionCatalogue.cs:13-22.
   *
   * A MISMATCH HERE IS NOT COSMETIC. PermissionChecker.cs:41 is fail-closed and audits every denial
   * before throwing (:49-63), so a wrong gate is a 403 the user cannot act on PLUS a PermissionDenied
   * row against their name in the one log an investigator is supposed to trust. If can() says true and
   * the server says 403, the fix is Phase 0's table (section 6.2 rule B) -- never a catch here.
   *
   * can() ANSWERS "WHO MAY CALL", NEVER "WHICH ROWS" (section 6.2 rule A). Row-level scoping is
   * CustomerScope.cs:37-41's and surfaces as a 404.
   */
  const canEditContact = can(role, 'EditCustomerContact'); // AA, AU, CA -- :18-19
  const canEditLegal = can(role, 'EditCustomerLegal'); // AA, AU only -- :17
  const canSuspend = can(role, 'SuspendCustomer'); // AA only -- :14
  const canReactivate = can(role, 'ReactivateCustomer'); // AA only -- :15

  /**
   * THE WHOLE Actions MENU IS ABSENT FOR AN AccountantUser, not rendered disabled: a menu whose only
   * two items can never be enabled is noise (section 6.2 rule C). Both items are AccountantAdmin-only,
   * so this is false for AU and the button is not rendered at all -- in any state, in any menu.
   */
  const hasActionsMenu = canSuspend || canReactivate;

  if (query.isLoading) {
    return <LoadingRegion label="Loading Customer" />;
  }

  /**
   * 404 RENDERS NotFoundPage, AND NEVER "forbidden", "denied" OR "no permission" (section 2.3 rule J).
   * Every route naming a customerId answers 404 "Customer not found." for a row the caller may not
   * see, because a 403 would confirm the row exists. "Not found." is the only honest wording and it is
   * honest in both cases.
   */
  if (query.error !== null) {
    if (query.error instanceof ApiError && query.error.status === 404) {
      return <NotFoundPage />;
    }
    if (query.error instanceof ApiError && query.error.status === 403) {
      return <AccessDeniedPage />;
    }
    return (
      <ErrorBanner
        error={query.error}
        onReload={() => {
          void query.refetch();
        }}
      />
    );
  }

  const customer = query.data;
  if (customer === undefined) {
    // enabled: false while the id is still undefined mid-navigation. Not an error state.
    return <LoadingRegion label="Loading Customer" />;
  }

  return (
    <Stack spacing={3}>
      <PageHeader
        title={customer.legalName}
        subtitle={<StatusChip status={customer.status} />}
        action={
          hasActionsMenu ? (
            <>
              <IconButton
                aria-label="Actions"
                onClick={(event) => {
                  setMenuAnchor(event.currentTarget);
                }}
              >
                <MoreVertIcon />
              </IconButton>
              <Menu
                anchorEl={menuAnchor}
                open={menuAnchor !== null}
                onClose={() => {
                  setMenuAnchor(null);
                }}
              >
                {/*
                  Each item is gated on the permission AND on the current status. Suspending an
                  already-suspended Customer is 422 "This customer is already suspended."
                  (SuspendCustomerHandler.cs:49-50), so offering it would be an error the operator can
                  only discover by clicking. The 422 is still handled -- a stale tab reaches it -- but
                  it is not invited.
                */}
                {canSuspend && customer.status === 'Active' && (
                  <MenuItem
                    onClick={() => {
                      setMenuAnchor(null);
                      setOpenDialog('suspend');
                    }}
                  >
                    Suspend Customer
                  </MenuItem>
                )}
                {canReactivate && customer.status === 'Suspended' && (
                  <MenuItem
                    onClick={() => {
                      setMenuAnchor(null);
                      setOpenDialog('reactivate');
                    }}
                  >
                    Reactivate Customer
                  </MenuItem>
                )}
              </Menu>
            </>
          ) : undefined
        }
      />

      <Stack direction={{ xs: 'column', md: 'row' }} spacing={3} sx={{ alignItems: 'flex-start' }}>
        {/*
          THE LEGAL CARD -- exactly UpdateCustomerLegalRequestDto's four fields, which is why its
          button is gated on EditCustomerLegal and a CustomerAdmin never sees this screen at all.
        */}
        <Card variant="outlined" sx={{ flex: 1, width: '100%' }}>
          <CardHeader title="Legal" />
          <Divider />
          <CardContent>
            <Stack spacing={1}>
              <DetailRow label="Legal name" value={customer.legalName} />
              <DetailRow label="Trading name" value={orDash(customer.tradingName)} />
              <DetailRow label="Tax number" value={customer.taxNumber} />
              <DetailRow label="Tax office" value={orDash(customer.taxOffice)} />
            </Stack>
          </CardContent>
          {canEditLegal && (
            <CardContent sx={{ pt: 0 }}>
              <Button
                onClick={() => {
                  setOpenDialog('legal');
                }}
              >
                Edit legal
              </Button>
            </CardContent>
          )}
        </Card>

        {/* THE CONTACT CARD -- exactly UpdateCustomerContactRequestDto's seven fields. */}
        <Card variant="outlined" sx={{ flex: 1, width: '100%' }}>
          <CardHeader title="Contact" />
          <Divider />
          <CardContent>
            <Stack spacing={1}>
              <DetailRow
                label="Address"
                value={
                  <>
                    <div>{customer.addressLine1}</div>
                    {customer.addressLine2 !== null && <div>{customer.addressLine2}</div>}
                    <div>
                      {customer.addressCity} {customer.addressPostalCode}
                    </div>
                    <div>{customer.addressCountry}</div>
                  </>
                }
              />
              <DetailRow label="Email" value={customer.contactEmail} />
              <DetailRow label="Phone" value={customer.contactPhone} />
            </Stack>
          </CardContent>
          {canEditContact && (
            <CardContent sx={{ pt: 0 }}>
              <Button
                onClick={() => {
                  setOpenDialog('contact');
                }}
              >
                Edit contact
              </Button>
            </CardContent>
          )}
        </Card>
      </Stack>

      <Stack direction={{ xs: 'column', md: 'row' }} spacing={3} sx={{ alignItems: 'flex-start' }}>
        {/*
          THE RECORD CARD IS READ-ONLY AND HAS NO BUTTON. No endpoint changes any of these three
          fields: UpdateCustomerLegalRequestDto.cs:5-9 and UpdateCustomerContactRequestDto.cs:5-12
          carry none of them. Rendering them is right; putting one in an input would post a value
          nothing reads.

          THREE DATES, TWO WIRE FORMATS (GeneralUIArchitecture.md section 10.2). onboardedOn is a
          DateOnly -- "2026-03-14", no timezone -- and formatDate treats it as one, so it never shifts
          a day west of UTC. createdAt and updatedAt are DateTimeOffsets and carry their offset. All
          three go through shared/format/dates.ts, because that is where a timezone bug gets fixed
          once instead of in six screens.
        */}
        <Card variant="outlined" sx={{ flex: 1, width: '100%' }}>
          <CardHeader title="Record" />
          <Divider />
          <CardContent>
            <Stack spacing={1}>
              <DetailRow label="Onboarded on" value={formatDate(customer.onboardedOn)} />
              <DetailRow label="Created" value={formatDateTime(customer.createdAt)} />
              <DetailRow label="Updated" value={formatDateTime(customer.updatedAt)} />
            </Stack>
          </CardContent>
        </Card>

        {/*
          A ROUTER LINK, NOT AN IMPORT. Navigating to another slice's screen is routing; importing its
          screen or its hooks would be a dependency (section 1.4 rule C). Available to both Accountant
          roles, ungated: /employees carries its own RequireRole.
        */}
        <Stack sx={{ flex: 1, width: '100%' }}>
          <Button
            component={RouterLink}
            to={`/employees?customerId=${encodeURIComponent(customer.id)}`}
            endIcon={<ArrowForwardIcon />}
            sx={{ alignSelf: 'flex-start' }}
          >
            View employees
          </Button>
        </Stack>
      </Stack>

      {/*
        THE FOUR DIALOGS. Each is opened from THE FRESHEST DETAIL THE CACHE HOLDS -- `customer` is
        this render's query data -- and is unmounted when closed, so no dialog keeps initial values
        across a route change. Both edit endpoints are full replacements with no concurrency token
        anywhere in the backend (section 8 rule A), so a form pre-filled from a stale read would
        revert whatever changed in between with nothing to detect it.
      */}
      {canEditLegal && openDialog === 'legal' && (
        <EditCustomerLegalDialog
          open
          customer={customer}
          onClose={() => {
            setOpenDialog(null);
          }}
          onSaved={() => {
            setOpenDialog(null);
            setToast('Legal details saved');
          }}
        />
      )}

      {canEditContact && openDialog === 'contact' && (
        <EditCustomerContactDialog
          open
          customerId={customer.id}
          initialValues={{
            addressLine1: customer.addressLine1,
            addressLine2: customer.addressLine2,
            addressCity: customer.addressCity,
            addressPostalCode: customer.addressPostalCode,
            addressCountry: customer.addressCountry,
            contactEmail: customer.contactEmail,
            contactPhone: customer.contactPhone,
          }}
          onClose={() => {
            setOpenDialog(null);
          }}
          onSaved={() => {
            setOpenDialog(null);
            setToast('Contact details saved');
          }}
        />
      )}

      {canSuspend && openDialog === 'suspend' && (
        <SuspendCustomerDialog
          open
          customerId={customer.id}
          legalName={customer.legalName}
          onClose={() => {
            setOpenDialog(null);
          }}
          onSuspended={() => {
            setOpenDialog(null);
            setToast('Customer suspended');
          }}
        />
      )}

      {canReactivate && openDialog === 'reactivate' && (
        <ReactivateCustomerDialog
          open
          customerId={customer.id}
          legalName={customer.legalName}
          onClose={() => {
            setOpenDialog(null);
          }}
          onReactivated={() => {
            setOpenDialog(null);
            // "Customer reactivated" AND NOTHING MORE. It must not promise anybody can now sign in:
            // reactivating the Customer does not restore individually suspended UserAccounts, which
            // 02-AuthorizationMatrix.md section 11 calls "correct and will look like a bug".
            setToast('Customer reactivated');
          }}
        />
      )}

      {/* A Snackbar on success. Successes are the only toasts in the app (section 5.3). */}
      <Snackbar
        open={toast !== null}
        autoHideDuration={4000}
        message={toast ?? ''}
        onClose={() => {
          setToast(null);
        }}
      />
    </Stack>
  );
}
