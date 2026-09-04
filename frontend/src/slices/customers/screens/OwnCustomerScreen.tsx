import { useState, type ReactNode } from 'react';
import Button from '@mui/material/Button';
import Card from '@mui/material/Card';
import CardContent from '@mui/material/CardContent';
import Divider from '@mui/material/Divider';
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
import { can } from '../../../shared/permissions/can';
import { EditCustomerContactDialog } from '../components/EditCustomerContactDialog';
import { useOwnCustomer } from '../queries';

/**
 * Route /my-customer, RequireRole'd to CustomerAdmin and Employee -- the EXACT REVERSE of /customers
 * and /customers/:customerId, which are the two Accountant roles' (GeneralUIArchitecture.md
 * section 4.1). No role opens both.
 *
 * THE NARROWING IS THE SERVER'S, NOT THIS SCREEN'S. CustomerMapper.ToSelfDto omits five fields
 * ToDto includes -- taxNumber, taxOffice, onboardedOn, createdAt, updatedAt -- so they are ABSENT
 * FROM THE RESPONSE, which is what 02-AuthorizationMatrix.md:311 demands: "must be absent from the
 * API response, not merely unrendered". There is therefore nothing here for the UI to hide, and this
 * file contains no code that hides anything. If a taxNumber ever appears in this response, the fix is
 * in CustomerMapper and the finding is a report, not a delete in JSX (section 6.2 rule A).
 *
 * NO customerId ANYWHERE IN THIS FILE. /api/customers/own takes no parameter -- it reads
 * CurrentUser.CustomerId server-side (section 7 rule I). `own.id` is used only as the customerId the
 * contact write needs in its BODY.
 *
 * AN Employee GETS A READ-ONLY CARD WITH NO BUTTONS, AND THAT IS THE WHOLE SCREEN FOR THEM. No
 * filler, no "contact your administrator", no disabled Edit button (section 12 item 2, section 6.2
 * rule C). EditCustomerContact is granted to AA, AU and CustomerAdmin only
 * (CustomersActionCatalogue.cs:18-19), so for an Employee there is no button and the dialog is never
 * mounted.
 *
 * NO PATH TO LEGAL NAME, TRADING NAME, TAX NUMBER OR TAX OFFICE -- not a button, not a link, not a
 * URL. EditCustomerLegal excludes both Customer-side roles (CustomersActionCatalogue.cs:17) and three
 * of those four are not even in this DTO. Legal name and trading name are RENDERED, because they are
 * in the DTO and are what the company is called; they are simply not editable from here.
 *
 * NO SUSPEND, NO REACTIVATE, NO MENTION OF EITHER. Both are AccountantAdmin-only and no Customer-side
 * screen names them (section 6.3).
 */

/** A label/value row. Local, like the detail screen's: shared/ is Phase 0's. */
function DetailRow({ label, value }: { label: string; value: ReactNode }) {
  return (
    <Stack direction="row" spacing={2} sx={{ alignItems: 'baseline' }}>
      <Typography variant="body2" color="text.secondary" sx={{ minWidth: 120, flexShrink: 0 }}>
        {label}
      </Typography>
      <Typography variant="body2" component="div">
        {value}
      </Typography>
    </Stack>
  );
}

export function OwnCustomerScreen() {
  const { role } = useAuthenticatedSession();
  const query = useOwnCustomer();

  const [isEditOpen, setIsEditOpen] = useState(false);
  const [toast, setToast] = useState<string | null>(null);

  /** CustomerAdmin yes, Employee no (CustomersActionCatalogue.cs:18-19). */
  const canEditContact = can(role, 'EditCustomerContact');

  if (query.isLoading) {
    return <LoadingRegion label="Loading your Customer" />;
  }

  /**
   * NO 401 SPECIAL CASE, AND NO SPECIAL CASE FOR THE 403 EITHER (section 6.4 rule A).
   * GetOwnCustomerHandler.cs:24 runs RequireAsync("ViewOwnCustomer") BEFORE it reads
   * CurrentUser.CustomerId, so an Accountant reaching this endpoint gets 403 and the
   * AppException("Authentication required.", 401) on the next line is unreachable for them. Both are
   * unreachable through the router anyway, and section 2.3 rule H says a 403 here is handled exactly
   * as a 403 is handled everywhere else.
   *
   * The 404 is the defensive branch -- a Customer-side account whose customers row is gone -- and it
   * renders NotFoundPage, never "forbidden" (section 7 rule B).
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

  const own = query.data;
  if (own === undefined) {
    return <LoadingRegion label="Loading your Customer" />;
  }

  return (
    <Stack spacing={3}>
      {/*
        THE CHIP IS RENDERED EVEN THOUGH IT WILL NORMALLY READ Active (section 6.4 rule B). A
        suspended Customer's people cannot sign in at all, so the only way anyone sees "Suspended"
        here is inside the up-to-8-hour window of a session that predates the suspension -- and that
        window is exactly when a user needs the explanation. Typed CustomerStatus, so an 'Invited'
        chip -- a UserAccount status -- cannot be rendered for a Customer.
      */}
      <PageHeader title={own.legalName} subtitle={<StatusChip status={own.status} />} />

      <Card variant="outlined">
        <CardContent>
          <Stack spacing={1}>
            <DetailRow label="Trading name" value={own.tradingName ?? '—'} />
            <DetailRow
              label="Address"
              value={
                <>
                  <div>{own.addressLine1}</div>
                  {own.addressLine2 !== null && <div>{own.addressLine2}</div>}
                  <div>
                    {own.addressCity} {own.addressPostalCode}
                  </div>
                  <div>{own.addressCountry}</div>
                </>
              }
            />
            <DetailRow label="Email" value={own.contactEmail} />
            <DetailRow label="Phone" value={own.contactPhone} />
          </Stack>
        </CardContent>
        {canEditContact && (
          <>
            <Divider />
            <CardContent>
              <Button
                onClick={() => {
                  setIsEditOpen(true);
                }}
              >
                Edit contact
              </Button>
            </CardContent>
          </>
        )}
      </Card>

      {/*
        THE SAME COMPONENT AS THE DETAIL SCREEN'S, posting the same DTO with customerId taken from
        own.id (section 6.3). CustomerScope restricts the write to the caller's own row regardless
        (UpdateCustomerContactHandler.cs:39-43), so there is no second endpoint and no CA-specific
        form. Mounted only while open, so its defaultValues come from the freshest cached record on
        every open and a background refetch cannot wipe what has been typed.

        Its hook INVALIDATES ['customers','own'] and never seeds it: update-contact returns the wide
        CustomerDto and this key holds the narrow CustomerSelf, so seeding would put taxNumber and the
        other four omitted fields into a cache entry this screen reads (section 6.1).
      */}
      {canEditContact && isEditOpen && (
        <EditCustomerContactDialog
          open
          customerId={own.id}
          initialValues={{
            addressLine1: own.addressLine1,
            addressLine2: own.addressLine2,
            addressCity: own.addressCity,
            addressPostalCode: own.addressPostalCode,
            addressCountry: own.addressCountry,
            contactEmail: own.contactEmail,
            contactPhone: own.contactPhone,
          }}
          onClose={() => {
            setIsEditOpen(false);
          }}
          onSaved={() => {
            setIsEditOpen(false);
            setToast('Contact details saved');
          }}
        />
      )}

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
