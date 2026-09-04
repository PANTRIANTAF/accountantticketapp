import type { ReactNode } from 'react';
import { Navigate, useLocation } from 'react-router-dom';
import { LoadingRegion } from '../components/LoadingRegion';
import { UserRole } from '../format/enums';
import { useSession } from './useSession';

/**
 * The authenticated gate. GeneralUIArchitecture.md section 4.3, plan section 4.3.
 *
 * FOUR BRANCHES, IN THIS ORDER. The order is the specification, not a preference:
 *
 *   1. loading            -> LoadingRegion. No route decision is taken.
 *   2. anonymous          -> <Navigate to="/login" state={{ from: location }} replace />
 *   3. mustChangePassword -> <Navigate to="/change-password" replace />
 *   4. otherwise          -> children
 *
 * Putting mustChangePassword first reads `session` in the loading branch, where there is none.
 * Putting anonymous before loading is the login-form flash on every hard refresh.
 *
 * IT WRAPS THE SHELL ONCE, in routes.tsx -- not once per screen (section 4.3 rule C). A screen
 * that checks for a session itself is a screen that can be mounted without one.
 */
/** The forced-password-change route, in one place: RequireSession both redirects to it and exempts it. */
const CHANGE_PASSWORD_PATH = '/change-password';

export function RequireSession({ children }: { children: ReactNode }) {
  const session = useSession();
  const location = useLocation();

  if (session.status === 'loading') {
    // Not the shell with empty navigation, and not the login form. Both are visibly wrong for the
    // half-second they are on screen (plan section 4.1 rule C).
    return <LoadingRegion label="Loading…" minHeight="60vh" />;
  }

  if (session.status === 'anonymous') {
    // `replace` so the back button does not return to a route that immediately redirects again.
    // The path travels in ROUTER STATE, never in a query parameter: a `?returnTo=` is an open
    // redirect the moment it is allowed to hold an absolute URL (LoginArchitecture.md
    // section 2.3 rule A).
    return <Navigate to="/login" state={{ from: location }} replace />;
  }

  if (session.session.mustChangePassword && location.pathname !== CHANGE_PASSWORD_PATH) {
    // Belt and braces with the http.ts 403 interceptor: this catches the flag the bootstrap
    // already returned, before any request that would 403; the interceptor catches the flag being
    // set by another session mid-flight. Both paths must exist (LoginArchitecture.md
    // section 3.2 rule B).
    //
    // THE PATHNAME CHECK IS LOAD-BEARING, NOT DEFENSIVE. /change-password is itself wrapped in
    // RequireSession (plan section 7.1), and that is correct -- the screen calls an authenticated
    // endpoint. Without this check the branch fires ON THE VERY ROUTE IT REDIRECTS TO: <Navigate>
    // renders null and its effect has stable dependencies, so it navigates to /change-password once
    // and then renders nothing forever. The user sees a BLANK PAGE where the form should be, with no
    // error and no redirect loop to notice it by, and the gate looks broken in exactly the way
    // LoginArchitecture.md section 3.2 rule D warns about.
    return <Navigate to={CHANGE_PASSWORD_PATH} replace />;
  }

  return <>{children}</>;
}

/**
 * Post-login landing route by role. LoginArchitecture.md section 2.2 and
 * GeneralUIArchitecture.md section 4.2, which agree.
 *
 * Also the body of the `/` route, which is a role-dependent redirect rather than a page.
 * routes.tsx imports this so there is exactly one copy.
 *
 * `/profile` for an Employee is an ACKNOWLEDGED PLACEHOLDER, not a home screen: an Employee's
 * real home is "my tickets" and the Tickets UI does not exist. Three documents agree on
 * `/profile` (LoginArchitecture.md sections 2.2 and 2.6, GeneralUIArchitecture.md section 4.2,
 * Architect Files/README.md). Do not "improve" it to `/ticket-types` -- that is a catalogue of
 * forms they cannot submit yet, which reads as a broken home.
 */
export const LANDING_ROUTE_BY_ROLE: Record<UserRole, string> = {
  [UserRole.AccountantAdmin]: '/customers',
  [UserRole.AccountantUser]: '/customers',
  [UserRole.CustomerAdmin]: '/employees',
  [UserRole.Employee]: '/profile',
};

const ALL_ROLES: readonly UserRole[] = [
  UserRole.AccountantAdmin,
  UserRole.AccountantUser,
  UserRole.CustomerAdmin,
  UserRole.Employee,
];

/**
 * The Roles column of GeneralUIArchitecture.md section 4.1, in the same order, as data.
 *
 * WHY IT IS HERE AND NOT IN routes.tsx. LoginArchitecture.md section 2.3 rule C requires the
 * login screen to check the stored return-to path against the role before navigating to it, so
 * something outside routes.tsx needs the Roles column. A slice screen importing routes.tsx would
 * be a cycle (routes.tsx imports every screen -- section 1.4 rule E), and a second hand-written
 * copy of the column would drift. So the column lives here, in shared/, and routes.tsx imports it
 * for its RequireRole rows. One copy, both readers.
 *
 * A SLICE PLAN ADDS ROWS HERE, NEVER REPLACES THE TABLE -- one row here and one row in
 * routes.tsx, with identical keys.
 *
 * The four public routes map to an EMPTY role set on purpose: they are marked `anonymous` in
 * section 4.1, so no authenticated role may see them, so rule C sends a user who was bounced off
 * `/login` to their landing route rather than back to the form they just submitted.
 *
 * `:param` segments match any single segment. Literal keys are matched before parameterised ones,
 * so `/customers/new` wins over `/customers/:customerId`.
 */
export const ROUTE_ROLES = {
  // Public -- shell: no, anonymous.
  '/login': [],
  '/forgot-password': [],
  '/reset-password': [],
  '/accept-invitation': [],

  // Authenticated, shell: no.
  '/change-password': ALL_ROLES,

  // Shell routes.
  '/': ALL_ROLES,
  '/customers': [UserRole.AccountantAdmin, UserRole.AccountantUser],
  '/customers/new': [UserRole.AccountantAdmin],
  '/customers/:customerId': [UserRole.AccountantAdmin, UserRole.AccountantUser],
  '/my-customer': [UserRole.CustomerAdmin, UserRole.Employee],
  '/employees': [UserRole.AccountantAdmin, UserRole.AccountantUser, UserRole.CustomerAdmin],
  '/employees/:employeeId': ALL_ROLES,
  '/ticket-types': ALL_ROLES,
  '/ticket-types/new': [UserRole.AccountantAdmin, UserRole.AccountantUser],
  '/ticket-types/:ticketTypeId': ALL_ROLES,
  '/ticket-types/:ticketTypeId/edit': [UserRole.AccountantAdmin, UserRole.AccountantUser],
  '/accountants': [UserRole.AccountantAdmin, UserRole.AccountantUser],
  '/notifications': ALL_ROLES,
  '/audit': [UserRole.AccountantAdmin],
  '/audit/:auditEntryId': [UserRole.AccountantAdmin],
  '/profile': ALL_ROLES,
} as const satisfies Record<string, readonly UserRole[]>;

/** Whether a concrete path resolves to a route the role may see. */
export function isPathVisibleToRole(path: string, role: UserRole): boolean {
  const roles = matchRouteRoles(path);
  // An unmatched path is the `*` route. It is not a route the role "may see" -- it is no route at
  // all -- so the caller falls back to the landing route rather than opening a session on a
  // not-found page.
  return roles === null ? false : roles.includes(role);
}

/**
 * The return-to decision, applying all four of LoginArchitecture.md section 2.3's rules.
 *
 * `from` is whatever RequireSession put in `location.state`, which is to say untrusted data typed
 * `unknown` -- router state survives a reload and is editable in devtools.
 *
 * The CALLER must clear the state once this has been used (rule D), or the path redirects the next
 * login too and surfaces weeks later as "logging in sends me to a random page".
 */
export function resolvePostLoginPath(from: unknown, role: UserRole): string {
  const landing = LANDING_ROUTE_BY_ROLE[role];
  const path = readSafePath(from);
  if (path === null) return landing;
  if (!isPathVisibleToRole(splitQuery(path), role)) return landing;
  return path;
}

/**
 * Rule B: only ever a path starting with a SINGLE `/`. `//evil.example.com` is protocol-relative
 * and a browser treats it as a different origin, so it is an open redirect even though it looks
 * like a path. A backslash is rejected for the same reason -- browsers normalise `/\` to `//`.
 * Validated even though the value came from our own router, because it costs one line.
 */
function readSafePath(from: unknown): string | null {
  let candidate: string | null = null;

  if (typeof from === 'string') {
    candidate = from;
  } else if (from !== null && typeof from === 'object') {
    // A react-router Location. Keep the query string: /audit?outcome=Denied is the page the user
    // asked for, not /audit. The hash is dropped -- it is never meaningful in this app.
    const location = from as { pathname?: unknown; search?: unknown };
    if (typeof location.pathname === 'string') {
      candidate = location.pathname + (typeof location.search === 'string' ? location.search : '');
    }
  }

  if (candidate === null) return null;
  if (!candidate.startsWith('/')) return null;
  if (candidate.startsWith('//') || candidate.startsWith('/\\')) return null;

  // A control character in a redirect target is header-splitting muscle memory; no legitimate path
  // in this app contains one. Checked by code point rather than a regex range so the source file
  // itself never has to hold a control character.
  for (const character of candidate) {
    const code = character.codePointAt(0) ?? 0;
    if (code < 0x20 || code === 0x7f) return null;
  }

  return candidate;
}

function splitQuery(path: string): string {
  const cut = path.search(/[?#]/);
  return cut === -1 ? path : path.slice(0, cut);
}

function matchRouteRoles(path: string): readonly UserRole[] | null {
  const normalized = path.length > 1 && path.endsWith('/') ? path.slice(0, -1) : path;
  const entries = Object.entries(ROUTE_ROLES) as [string, readonly UserRole[]][];

  // Literal patterns first, so /customers/new is not swallowed by /customers/:customerId.
  const literal = entries.find(([pattern]) => !pattern.includes(':') && pattern === normalized);
  if (literal !== undefined) return literal[1];

  const segments = normalized.split('/');
  const parameterised = entries.find(([pattern]) => {
    if (!pattern.includes(':')) return false;
    const patternSegments = pattern.split('/');
    if (patternSegments.length !== segments.length) return false;
    return patternSegments.every((patternSegment, index) => {
      if (patternSegment.startsWith(':')) {
        const value = segments[index];
        return value !== undefined && value.length > 0;
      }
      return patternSegment === segments[index];
    });
  });

  return parameterised === undefined ? null : parameterised[1];
}
