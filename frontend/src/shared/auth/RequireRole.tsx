import type { ReactNode } from 'react';
import { AccessDeniedPage } from '../components/AccessDeniedPage';
import type { UserRole } from '../format/enums';
import { useSession } from './useSession';

/**
 * The per-page role gate. GeneralUIArchitecture.md section 4.3, plan section 4.4.
 *
 * A. IT RENDERS A DENIAL PAGE; IT DOES NOT REDIRECT. A user who typed /audit deserves to be told
 *    the page is not for them. A silent bounce to /customers reads as a broken link and they try
 *    again.
 *
 * B. IT IS NOT A SECURITY BOUNDARY. It is the same affordance logic as can(), applied to a whole
 *    page. The server denies the underlying calls with 403 and audits every denial regardless of
 *    what the router did (section 6.2 rule B).
 *
 * C. COMPARE WITH roles.includes(session.role). Never indexOf(...) > 0 and never a truthiness
 *    test: AccountantAdmin is 0, which is index 0 in most of these arrays AND falsy as a value.
 *
 * It is always INSIDE RequireSession, so `loading` and `anonymous` have already been handled. It
 * still handles them rather than asserting, because a route table that nests them the other way
 * round should degrade to "not yet" rather than crash.
 */
export function RequireRole({
  roles,
  children,
}: {
  roles: readonly UserRole[];
  children: ReactNode;
}) {
  const session = useSession();

  if (session.status !== 'authenticated') {
    // RequireSession owns both of these branches and has already rendered a loader or a redirect.
    return null;
  }

  if (!roles.includes(session.session.role)) {
    return <AccessDeniedPage />;
  }

  return <>{children}</>;
}
