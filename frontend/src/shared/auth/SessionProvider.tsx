import { createContext, useCallback, useEffect, useState, type ReactNode } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import {
  get,
  registerPasswordChangeRequiredHandler,
  registerUnauthorizedHandler,
  SESSION_BOOTSTRAP_PATH,
} from '../api/http';
import type { UserRole } from '../format/enums';

/**
 * Mirrors Slices/Identity/Application/Dtos/AuthDtos.cs --
 * SessionDto(string UserId, string DisplayName, UserRole Role, Guid? CustomerId, bool MustChangePassword).
 *
 * There is deliberately NO loginEmail, which is why the change-password form cannot check the
 * not-equal-login-email rule client-side. BACKEND_CHANGES_REQUIRED item 11.
 *
 * WHY THIS TYPE LIVES HERE AND NOT IN slices/identity/types.ts.
 * GeneralUIArchitecture.md section 1.4 rule A says shared/ may NEVER import from slices/, while
 * LoginArchitecture.md section 1.1 puts getSession in slices/identity/api.ts and section 1.2 puts
 * SessionProvider in shared/auth/. Those cannot both hold. Precedence resolves it: section 1.4
 * rule A wins. So this file declares SessionDto and calls get<SessionDto>('/api/auth/me')
 * directly -- which is exactly what section 1.2's own comment on the file, "bootstraps
 * GET /api/auth/me", describes -- and slices/identity/types.ts RE-EXPORTS the type rather than
 * redeclaring it. There is no second getSession: two declarations of the session shape is the
 * drift rule A exists to prevent.
 */
export interface SessionDto {
  /** A Guid rendered as a string, not a Guid on the wire. */
  userId: string;
  /** Never blank; AcceptInvitationHandler refuses to clear it. */
  displayName: string;
  /** A NUMBER. 0 is AccountantAdmin, and 0 is falsy -- never test it for truthiness. */
  role: UserRole;
  /** null for both Accountant roles; non-null for CustomerAdmin and Employee. */
  customerId: string | null;
  /** Check BEFORE routing anywhere. */
  mustChangePassword: boolean;
}

/** The query key for the session. Session mutations write into it; they do not invalidate it. */
export const SESSION_QUERY_KEY = ['identity', 'session'] as const;

/**
 * Three states, not two. Collapsing `loading` into `anonymous` means every guard sees "no session"
 * during the first round trip and redirects an authenticated user to /login, who bounces back --
 * a flash of the login form on every hard refresh, invisible on a fast local machine.
 * LoginArchitecture.md section 1.2.
 */
export type Session =
  | { status: 'loading' }
  | { status: 'anonymous' }
  | { status: 'authenticated'; session: SessionDto };

export interface SessionContextValue {
  session: Session;
  /**
   * Set once when the global 401 handler fires, so /login shows ONE "your session ended" message.
   * Provider state, not storage: read once by the login screen and cleared
   * (LoginArchitecture.md section 7 rule B). Not a toast on a page that is about to unmount.
   */
  expired: boolean;
  clearExpired: () => void;
}

// eslint-disable-next-line -- no ESLint in this project; the comment is a marker for reviewers.
export const SessionContext = createContext<SessionContextValue | null>(null);

export function SessionProvider({ children }: { children: ReactNode }) {
  const queryClient = useQueryClient();
  const [expired, setExpired] = useState(false);

  const { data, isPending, error } = useQuery({
    queryKey: SESSION_QUERY_KEY,
    queryFn: () => get<SessionDto>(SESSION_BOOTSTRAP_PATH),
    // A 401 is an answer, not a transient failure. The global policy already refuses 4xx; it is
    // stated here because retrying means three round trips before the login form appears.
    retry: false,
    // The session does not change without a mutation that seeds it: login, logout,
    // change-password, and the 401 handler. Refetching on focus would call /api/auth/me every
    // time the user alt-tabs.
    staleTime: Infinity,
  });

  useEffect(() => {
    registerUnauthorizedHandler(() => {
      // The WHOLE cache, not just the session key. LoginArchitecture.md section 6 rule A: leaving
      // customer lists and audit entries in memory means the next user at the same browser sees
      // the previous user's data flash on screen. On a shared office machine that is a real
      // disclosure.
      queryClient.clear();
      setExpired(true);
    });

    registerPasswordChangeRequiredHandler(() => {
      // The refreshed session carries mustChangePassword: true and RequireSession does the rest.
      // Never a toast: it is a state the account is in, not a failed action.
      void queryClient.invalidateQueries({ queryKey: SESSION_QUERY_KEY });
    });
  }, [queryClient]);

  const clearExpired = useCallback(() => setExpired(false), []);

  let session: Session;
  if (isPending) {
    session = { status: 'loading' };
  } else if (data !== undefined) {
    session = { status: 'authenticated', session: data };
  } else {
    // Settled with an error. A 401 here is the normal answer for an anonymous visitor
    // (LoginArchitecture.md section 1.1); any other failure -- the API being down, for instance --
    // also leaves the app with no session, and the honest rendering is the same public routes.
    // ErrorBanner surfaces the reason once the user tries something.
    void error;
    session = { status: 'anonymous' };
  }

  return (
    <SessionContext.Provider value={{ session, expired, clearExpired }}>
      {children}
    </SessionContext.Provider>
  );
}
