import { ApiError } from './ApiError';
import { parseProblemDetails } from './problemDetails';

/**
 * The ONLY module in the application permitted to call fetch. Everything else calls get/post.
 * A fetch anywhere under slices/ is a defect regardless of whether it works.
 * GeneralUIArchitecture.md section 2.1.
 */

/** The session bootstrap. SessionProvider is the only caller; the exemption note below explains why. */
export const SESSION_BOOTSTRAP_PATH = '/api/auth/me';

/**
 * THE THREE PATHS WHOSE 401 IS NOT AN EXPIRED SESSION. A 401 from anywhere else means the cookie is
 * gone and the user must be moved (GeneralUIArchitecture.md section 2.3 rule H, LoginArchitecture.md
 * section 7 rule A). From these three it is the endpoint's ordinary answer, and treating it as an
 * expiry is visibly wrong:
 *
 *   /api/auth/me              An anonymous visitor's NORMAL answer (LoginArchitecture.md 1.1). Acting
 *                             on it means /login mounts, bootstraps, 401s, redirects -- an infinite
 *                             loop that pins the CPU.
 *   /api/auth/login           A WRONG PASSWORD. There is no session to lose: the visitor never had
 *                             one. Acting on it tells a first-time user with a typo that "your
 *                             session has ended", which is both untrue and confusing.
 *   /api/auth/change-password A WRONG CURRENT PASSWORD -- 401, not 403, by design
 *                             (ChangeOwnPasswordHandler.cs:88). Acting on it logs the user out over a
 *                             typo, which is the plan's section 8.10 item 5 exactly.
 *
 * THIS IS A DOCUMENTED CONFLICT, RESOLVED IN FAVOUR OF THE MORE SPECIFIC RULE, and it is reported
 * rather than hidden. Rule H says a 401 from ANY call means the session is gone and must "never [be]
 * render[ed] ... inside a form". The SAME DOCUMENT's section 7.1 error table says of 401: "Redirect to
 * /login. ON THE LOGIN FORM ITSELF, A FORM BANNER" -- so a form does render a 401, and rule H cannot
 * be meant literally for the two endpoints that exist to check a credential. LoginArchitecture.md
 * section 7 is titled "Session expiry mid-session", which is the case rule H is about.
 *
 * The error is STILL THROWN for all three, so the screen renders it. The only thing suppressed is the
 * cache-clear-and-move-the-user side effect.
 */
const UNAUTHORIZED_HANDLER_EXEMPT_PATHS: readonly string[] = [
  SESSION_BOOTSTRAP_PATH,
  '/api/auth/login',
  '/api/auth/change-password',
];

/**
 * Verified against Shared/Auth/MustChangePasswordMiddleware.cs, which sets
 * Detail = "You must change your password before continuing." -- the ONE response in the entire
 * API that populates `detail`.
 *
 * It appears once, as this constant, and never as an inline literal. Matching on an English
 * sentence is fragile by construction; BACKEND_CHANGES_REQUIRED item 5 asks for a
 * machine-readable `code` extension, and one constant is what makes that a one-line change.
 */
export const MUST_CHANGE_PASSWORD_DETAIL = 'You must change your password before continuing.';

export function isMustChangePassword(error: unknown): boolean {
  return error instanceof ApiError
    && error.status === 403
    && (error.detail ?? '').includes(MUST_CHANGE_PASSWORD_DETAIL);
}

/**
 * http.ts cannot navigate. It is not a component, and it must not import the router or touch
 * window.location: a hard navigation discards the router state that LoginArchitecture.md
 * section 2.3 needs for return-to-intended-route, and it fires once per failed call, so a screen
 * with four queries would do it four times.
 *
 * So it exposes handler slots instead. SessionProvider registers both, once, in an effect.
 */
type Handler = () => void;

let onUnauthorized: Handler | null = null;
let onPasswordChangeRequired: Handler | null = null;

export function registerUnauthorizedHandler(handler: Handler): void {
  onUnauthorized = handler;
}

export function registerPasswordChangeRequiredHandler(handler: Handler): void {
  onPasswordChangeRequired = handler;
}

// No base URL, and no environment variable that could become one. Every path is relative to the
// SPA's own origin, in every environment. In development the Vite proxy forwards /api to the API;
// in production the SPA and the API are the same origin. See 04-Infrastructure.md section 2.
async function request<T>(method: 'GET' | 'POST', path: string, body?: unknown): Promise<T> {
  if (!path.startsWith('/api/')) {
    // A path that does not start with /api/ hits MapFallbackToFile and returns index.html with a
    // 200. Without this guard the caller gets HTML where it expected JSON and the parse failure
    // surfaces far away from the mistake.
    throw new Error(`API path must start with "/api/": ${path}`);
  }

  const response = await fetch(path, {
    method,
    // 'same-origin' is the default, but it is stated because 'omit' silently drops the session
    // cookie and 'include' implies a CORS request this app never makes.
    credentials: 'same-origin',
    headers: body === undefined ? undefined : { 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
  });

  if (!response.ok) {
    const error = await parseProblemDetails(response);

    // The exemption, by path, in one place. See UNAUTHORIZED_HANDLER_EXEMPT_PATHS above.
    if (error.status === 401 && !UNAUTHORIZED_HANDLER_EXEMPT_PATHS.includes(path)) {
      onUnauthorized?.();
    }

    // A state the account is in, not a failed action: SessionProvider invalidates the session
    // query and RequireSession renders the navigation declaratively. It never toasts.
    if (isMustChangePassword(error)) {
      onPasswordChangeRequired?.();
    }

    // ALWAYS thrown. The caller's error channel is unchanged: a non-2xx throws, callers never
    // read response.ok, and TanStack Query's isError is the single error channel (rule E).
    throw error;
  }

  // 204 never occurs in this API -- every mutation returns a body -- but a zero-length 200 would
  // make response.json() throw, so it is handled rather than assumed away.
  if (response.status === 204 || response.headers.get('content-length') === '0') {
    return undefined as T;
  }

  return (await response.json()) as T;
}

export const get = <T>(path: string): Promise<T> => request<T>('GET', path);
export const post = <T>(path: string, body?: unknown): Promise<T> => request<T>('POST', path, body);
export { ApiError };
