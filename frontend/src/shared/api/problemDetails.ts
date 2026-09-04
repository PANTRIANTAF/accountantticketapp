import { ApiError } from './ApiError';

const fallbackTitle: Record<number, string> = {
  401: 'Your session has ended. Sign in again.',
  403: 'You do not have permission to do that.',
  404: 'Not found.',
  429: 'Too many attempts. Wait a moment and try again.',
  500: 'Something went wrong. Try again.',
  502: 'The server is unavailable. Try again shortly.',
  503: 'The server is unavailable. Try again shortly.',
};

export async function parseProblemDetails(response: Response): Promise<ApiError> {
  // The body is NOT always JSON. Caddy answers a rate-limited request itself, and a proxy error
  // is HTML. Calling response.json() unguarded turns "429 Too Many Requests" into an unhandled
  // SyntaxError, which reaches the user as a blank screen instead of "slow down".
  //
  // The 401 and 403 written by IdentityRegistration.cs's OnRedirectToLogin /
  // OnRedirectToAccessDenied overrides have an EMPTY body, so this path is taken on every
  // unauthenticated request too, not only on a rate limit. LoginArchitecture.md section 0.3.
  //
  // Never branch on Content-Type to decide whether a body is a problem document: the API
  // serialises with WriteAsJsonAsync (Shared/Errors/AppExceptionMiddleware.cs), which sets
  // application/json and NOT application/problem+json. Branch on response.ok.
  let title: string | undefined;
  let detail: string | undefined;
  let traceId: string | undefined;

  try {
    const body = (await response.json()) as {
      title?: string; detail?: string; traceId?: string;
    };
    title = body.title;
    detail = body.detail;
    traceId = body.traceId;
  } catch {
    // Left undefined on purpose; the fallback below covers it.
  }

  return new ApiError(
    response.status,
    title ?? fallbackTitle[response.status] ?? 'Something went wrong. Try again.',
    detail,
    traceId,
  );
}
