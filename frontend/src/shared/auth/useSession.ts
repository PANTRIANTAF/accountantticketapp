import { useContext } from 'react';
import { SessionContext, type Session, type SessionDto } from './SessionProvider';

/**
 * The ONE way to read the session. GeneralUIArchitecture.md section 4.3, plan section 4.2.
 *
 * Returns a union discriminated on `status`, so TypeScript refuses to let a caller read
 * `session.role` in the `loading` branch -- which is the whole point of three states.
 *
 * NO COMPONENT CALLS useQuery(['identity','session']) ITSELF. A second observer shares the same
 * cache entry, so it appears to work, until it is given a different staleTime and the two
 * disagree about whether the user is logged in.
 */
export function useSession(): Session {
  return useSessionContext().session;
}

/**
 * The session when the caller is already inside RequireSession and cannot be anything else.
 *
 * Exists so a screen does not write `if (session.status !== 'authenticated') return null` --
 * which silently renders nothing if the assumption is ever wrong, instead of saying so. Throwing
 * makes a routing mistake visible in development on the first render.
 *
 * A screen reachable while anonymous must use useSession() and handle all three states.
 */
export function useAuthenticatedSession(): SessionDto {
  const session = useSessionContext().session;
  if (session.status !== 'authenticated') {
    throw new Error(
      'useAuthenticatedSession() was called outside RequireSession: the session is ' +
        `'${session.status}'. Wrap the route in <RequireSession> or use useSession() instead.`,
    );
  }
  return session.session;
}

/**
 * The one-time "your session ended" flag (LoginArchitecture.md section 7 rule B).
 *
 * Only the login screen reads it, and it must clear it once rendered -- a flag left set redirects
 * the message onto a later, deliberate login.
 */
export function useSessionExpiry(): { expired: boolean; clearExpired: () => void } {
  const { expired, clearExpired } = useSessionContext();
  return { expired, clearExpired };
}

function useSessionContext() {
  const value = useContext(SessionContext);
  if (value === null) {
    // Loudly, not a fallback to `anonymous`. A missing provider is a routing mistake, and a
    // silent 'anonymous' turns it into a redirect loop that looks like a backend problem.
    throw new Error('useSession() must be used inside <SessionProvider>.');
  }
  return value;
}
