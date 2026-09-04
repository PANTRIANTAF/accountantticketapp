import { createBrowserRouter, Navigate } from 'react-router-dom';
import { AppShell } from './shared/components/AppShell';
import { NotFoundPage } from './shared/components/NotFoundPage';
import { LANDING_ROUTE_BY_ROLE, RequireSession, ROUTE_ROLES } from './shared/auth/RequireSession';
import { RequireRole } from './shared/auth/RequireRole';
import { useAuthenticatedSession } from './shared/auth/useSession';
import { AcceptInvitationScreen } from './slices/identity/screens/AcceptInvitationScreen';
import { ChangePasswordScreen } from './slices/identity/screens/ChangePasswordScreen';
import { ForgotPasswordScreen } from './slices/identity/screens/ForgotPasswordScreen';
import { LoginScreen } from './slices/identity/screens/LoginScreen';
import { ResetPasswordScreen } from './slices/identity/screens/ResetPasswordScreen';
import { AccountantListScreen } from './slices/identity/screens/AccountantListScreen';
import { ProfileScreen } from './slices/identity/screens/ProfileScreen';
import { useLogout } from './slices/identity/queries';
import { CustomerListScreen } from './slices/customers/screens/CustomerListScreen';
import { OnboardCustomerScreen } from './slices/customers/screens/OnboardCustomerScreen';
import { CustomerDetailScreen } from './slices/customers/screens/CustomerDetailScreen';
import { OwnCustomerScreen } from './slices/customers/screens/OwnCustomerScreen';
import { EmployeeListScreen } from './slices/employees/screens/EmployeeListScreen';
import { EmployeeDetailScreen } from './slices/employees/screens/EmployeeDetailScreen';
import { TicketTypeListScreen } from './slices/ticketTypes/screens/TicketTypeListScreen';
import { TicketTypeDetailScreen } from './slices/ticketTypes/screens/TicketTypeDetailScreen';
import { TicketTypeEditorScreen } from './slices/ticketTypes/screens/TicketTypeEditorScreen';
import { NotificationCentreScreen } from './slices/notifications/screens/NotificationCentreScreen';
import { UnreadBadge } from './slices/notifications/components/UnreadBadge';
import { AuditSearchScreen } from './slices/audit/screens/AuditSearchScreen';
import { AuditEntryScreen } from './slices/audit/screens/AuditEntryScreen';

/**
 * THE SINGLE ROUTE TABLE. All 21 rows of GeneralUIArchitecture.md section 4.1, in order.
 *
 * This is the one file that imports every slice's screens (section 1.4 rule E). That is its job.
 *
 * HOW A SLICE PLAN ADDS ITS SCREENS. Every row below already exists with its RequireRole guard in
 * place; the not-yet-built ones render NotFoundPage behind a TODO naming the plan that owns them. A
 * slice plan therefore:
 *
 *   1. imports its screen at the top of this file,
 *   2. swaps `<NotFoundPage />` for `<ItsScreen />` inside the existing RequireRole, and deletes the
 *      TODO comment,
 *   3. changes NOTHING about the guard, the path or the role set.
 *
 * A missing route is a 404 nobody can explain; a route pointing at a screen a later plan will write
 * is a TODO a reviewer can see. It is also why the rows exist NOW rather than being inserted later:
 * with six plans running in parallel, a one-line element swap is a change that cannot collide with
 * another plan's, and an insertion is.
 *
 * The role sets come from ROUTE_ROLES in shared/auth/RequireSession.tsx so there is exactly ONE copy
 * of section 4.1's Roles column -- the login screen needs the same column to validate a return-to
 * path (LoginArchitecture.md section 2.3 rule C) and cannot import this file without a cycle.
 *
 * FOUR THINGS NOT TO "FIX":
 *
 * C. PATHS ARE KEBAB-CASE, matching the API's convention: /ticket-types, never /tickettypes or
 *    /ticketTypes.
 * D. SPA ROUTES TAKE PATH PARAMETERS; API ROUTES NEVER DO. /employees/:employeeId in the browser
 *    becomes POST /api/employees/get with { employeeId } in the body (section 2.3 rule D). Not an
 *    inconsistency: a URL a user bookmarks needs the id in it, and an API that never puts ids in
 *    paths never has a route-vs-body ambiguity.
 * E. /accept-invitation AND /reset-password ARE CONTRACT, NOT UI CHOICES.
 *    Slices/Identity/Application/TokenLinks.cs builds {baseUrl}/accept-invitation?token=... and
 *    {baseUrl}/reset-password?token=... and mails them. Renaming either -- or the `token` parameter
 *    -- breaks every link already sitting in an inbox, and invitation links are live for SEVEN DAYS
 *    (Core/UserAccountToken.cs:35). Do not "align" /reset-password with its endpoint name
 *    complete-password-reset; LoginArchitecture.md section 4.3 explains why they differ on purpose.
 * F. THE `*` CATCH-ALL IS MANDATORY (section 4.4) and renders INSIDE the shell, so a user who
 *    mistypes a URL still has navigation to get back with. Once the three hosting lines land,
 *    MapFallbackToFile("index.html") returns the SPA with a 200 for every non-/api path, so the
 *    server cannot tell /customers from /custmoers -- without this row a typo renders a blank page
 *    with no error in the browser and none in the logs.
 */
export const router = createBrowserRouter([
  // ---------------------------------------------------------------------------------------------
  // Public. shell: NO -- standalone and centred. Someone at /login has no session and therefore no
  // role, so there is nothing to draw a nav bar from (section 4.2).
  // ---------------------------------------------------------------------------------------------
  { path: '/login', element: <LoginScreen /> },
  { path: '/forgot-password', element: <ForgotPasswordScreen /> },
  { path: '/reset-password', element: <ResetPasswordScreen /> },
  { path: '/accept-invitation', element: <AcceptInvitationScreen /> },

  // ---------------------------------------------------------------------------------------------
  // Authenticated, shell: NO. This session is rejected on every other route with a 403, so drawing
  // the navigation would offer ten links that all fail (section 4.2, LoginArchitecture.md 3.2 C).
  // ---------------------------------------------------------------------------------------------
  {
    path: '/change-password',
    element: (
      <RequireSession>
        <ChangePasswordScreen />
      </RequireSession>
    ),
  },

  // ---------------------------------------------------------------------------------------------
  // Everything below the shell. RequireSession wraps the shell ONCE (section 4.3 rule C) -- not once
  // per screen. A screen that checks for a session itself is a screen that can be mounted without
  // one, and the next screen added has no guard and nobody notices.
  // ---------------------------------------------------------------------------------------------
  {
    path: '/',
    element: (
      <RequireSession>
        <ShellRoute />
      </RequireSession>
    ),
    children: [
      // `/` renders no content of its own: it is a role-dependent redirect (section 4.2).
      { index: true, element: <RoleLandingRedirect /> },

      // ----- Customers -----
      {
        path: '/customers',
        element: (
          <RequireRole roles={ROUTE_ROLES['/customers']}>
            <CustomerListScreen />
          </RequireRole>
        ),
      },
      {
        path: '/customers/new',
        element: (
          <RequireRole roles={ROUTE_ROLES['/customers/new']}>
            <OnboardCustomerScreen />
          </RequireRole>
        ),
      },
      {
        path: '/customers/:customerId',
        element: (
          <RequireRole roles={ROUTE_ROLES['/customers/:customerId']}>
            <CustomerDetailScreen />
          </RequireRole>
        ),
      },
      {
        path: '/my-customer',
        element: (
          <RequireRole roles={ROUTE_ROLES['/my-customer']}>
            <OwnCustomerScreen />
          </RequireRole>
        ),
      },

      // ----- Employees -----
      {
        path: '/employees',
        element: (
          <RequireRole roles={ROUTE_ROLES['/employees']}>
            <EmployeeListScreen />
          </RequireRole>
        ),
      },
      {
        path: '/employees/:employeeId',
        element: (
          <RequireRole roles={ROUTE_ROLES['/employees/:employeeId']}>
            <EmployeeDetailScreen />
          </RequireRole>
        ),
      },

      // ----- Ticket types -----
      {
        path: '/ticket-types',
        element: (
          <RequireRole roles={ROUTE_ROLES['/ticket-types']}>
            <TicketTypeListScreen />
          </RequireRole>
        ),
      },
      {
        path: '/ticket-types/new',
        element: (
          <RequireRole roles={ROUTE_ROLES['/ticket-types/new']}>
            <TicketTypeEditorScreen />
          </RequireRole>
        ),
      },
      {
        path: '/ticket-types/:ticketTypeId',
        element: (
          <RequireRole roles={ROUTE_ROLES['/ticket-types/:ticketTypeId']}>
            <TicketTypeDetailScreen />
          </RequireRole>
        ),
      },
      {
        path: '/ticket-types/:ticketTypeId/edit',
        element: (
          <RequireRole roles={ROUTE_ROLES['/ticket-types/:ticketTypeId/edit']}>
            <TicketTypeEditorScreen />
          </RequireRole>
        ),
      },

      // ----- Identity -----
      {
        path: '/accountants',
        element: (
          <RequireRole roles={ROUTE_ROLES['/accountants']}>
            <AccountantListScreen />
          </RequireRole>
        ),
      },

      // ----- Notifications -----
      {
        path: '/notifications',
        element: (
          <RequireRole roles={ROUTE_ROLES['/notifications']}>
            <NotificationCentreScreen />
          </RequireRole>
        ),
      },

      // ----- Audit -----
      {
        path: '/audit',
        element: (
          <RequireRole roles={ROUTE_ROLES['/audit']}>
            <AuditSearchScreen />
          </RequireRole>
        ),
      },
      {
        path: '/audit/:auditEntryId',
        element: (
          <RequireRole roles={ROUTE_ROLES['/audit/:auditEntryId']}>
            <AuditEntryScreen />
          </RequireRole>
        ),
      },

      // ----- Profile -----
      {
        path: '/profile',
        element: (
          <RequireRole roles={ROUTE_ROLES['/profile']}>
            {/* TWO SCREEN DOCUMENTS CLAIM THIS ROUTE, at the same precedence level.
                IdentityScreens.md:372 puts the file in slices/identity/ and its checklist (:442,
                :509) requires /profile to make NO API request at all. EmployeesScreens.md:368 puts
                it in slices/employees/ with a contact-details region. Precedence cannot break the
                tie -- both are Screens/*.md -- so the code does: EmployeesScreens.md:643 records
                that a CustomerAdmin or Employee has no way to obtain their own employeeId
                (SessionDto carries none), so :633 and :687 require no submit button and no
                pre-filled form until that closes. The Employees version is therefore inert today,
                and it reaches getOwnCustomer -- a Customers endpoint -- on a route whose roles are
                ALL_ROLES, so it fires for Accountants who have no Customer.
                Identity's session-only screen satisfies every NEGATIVE criterion in BOTH
                checklists: no contact-details region for an AccountantAdmin (:632), no submit
                button in any role (:633, :687), no name or email edit (:442).
                slices/employees/screens/ProfileScreen.tsx is left on disk, unrouted, pending a
                decision. Do not merge the two -- delete one. */}
            <ProfileScreen />
          </RequireRole>
        ),
      },

      // Rule F. Mandatory, and inside the shell.
      { path: '*', element: <NotFoundPage /> },
    ],
  },
]);

/**
 * The shell, plus the two couplings that cannot live inside shared/components/AppShell.tsx because
 * shared/ may never import from slices/ (section 1.4 rule A). Both land here, in the file that
 * already imports every slice (Plans/README.md, "the two seams").
 */
function ShellRoute() {
  const logout = useLogout();

  return (
    <AppShell
      onSignOut={() => logout.mutate()}
      isSigningOut={logout.isPending}
      // SEAM B: shared/ may never import from slices/ (section 1.4 rule A), so the bell cannot be
      // imported into AppShell.tsx. It arrives here instead, in the file that already imports every
      // slice's screens.
      notificationSlot={<UnreadBadge />}
    />
  );
}

/**
 * The body of the `/` route: a role-dependent redirect, not a page (section 4.2).
 *
 * A RECORD LOOKUP, NOT A CONDITIONAL. `if (session.role)` is false for AccountantAdmin, which is 0,
 * and a `switch` with a `default` catches 0 the same way -- either one lands the most privileged role
 * in the system wherever the fallback points. LANDING_ROUTE_BY_ROLE is total over UserRole, so
 * TypeScript refuses a missing case and there is no fallback to get wrong.
 */
function RoleLandingRedirect() {
  const { role } = useAuthenticatedSession();
  return <Navigate to={LANDING_ROUTE_BY_ROLE[role]} replace />;
}
