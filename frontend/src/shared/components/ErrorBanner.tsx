import { useEffect, useRef } from 'react';
import Alert from '@mui/material/Alert';
import AlertTitle from '@mui/material/AlertTitle';
import Button from '@mui/material/Button';
import Typography from '@mui/material/Typography';
import { ApiError } from '../api/ApiError';

/**
 * THE ONE IMPLEMENTATION of GeneralUIArchitecture.md section 7.1's ten-row error taxonomy. Screens
 * pass the error and nothing else; they do not branch on the status themselves. Ten rows in ten
 * screens is ten chances to render "forbidden" for a 404.
 *
 * The rows, and why each is what it is:
 *
 *   400  malformed body or an invalid/expired token -> `title`
 *   401  no session, expired session, bad credentials, non-Active account -> `title`
 *        Normally unreachable: http.ts moves the user to /login. It IS reachable on the login form
 *        itself, and on change-password, where a wrong CURRENT password is a 401 by design.
 *   403 with `detail`     the forced-password-change gate -> `title`
 *        Also normally unreachable: http.ts routes on it. It is a STATE, not a failed action.
 *   403 without `detail`  permission denied, already audited -> a fixed sentence
 *   404  not found OR out of scope -> "Not found." and NOTHING ELSE
 *   409  duplicate code or email, or a concurrent edit -> `title` + reload affordance
 *   422  a business rule refused the request -> `title`, verbatim
 *   429  Caddy's rate limiter on /api/auth/* -> a fixed sentence. Body is not JSON
 *   500  unexpected, already logged server-side -> generic sentence + the traceId
 *   502/503  the app container is down or restarting -> "The server is unavailable."
 *
 * TWO RULES THAT ARE EASY TO BREAK:
 *
 * A. NEVER render "forbidden", "denied" or "no permission" for a 404 (section 2.3 rule J). The
 *    backend returns 404 for out-of-scope rows deliberately, because a 403 confirms the row exists.
 *    "Not found" is the only honest wording and it is honest in both cases.
 * B. Show the traceId on 500 AND NOWHERE ELSE. It is support's only handle on a server-side log
 *    entry; printing it on a 422 -- where `title` already says exactly what is wrong -- teaches
 *    users to ignore it.
 *
 * An error NEVER replaces the form the user was filling in (section 7.2). Their input must survive
 * the failure: losing a half-built ticket type to a 422 about one field is the difference between a
 * correction and a re-entry. This is a banner ABOVE the submit button, inside the form.
 */
export function ErrorBanner({
  error,
  onReload,
  focusOnMount = true,
}: {
  /** Whatever TanStack Query put in `error`. `null`/`undefined` renders nothing. */
  error: unknown;
  /** Supplied for a 409, where the taxonomy asks for a reload affordance. */
  onReload?: () => void;
  /**
   * Focus moves to the banner on a failed submit (section 8.4 rule 3). A screen that renders this
   * somewhere focus must not move -- next to a field the user is still typing in -- passes false.
   */
  focusOnMount?: boolean;
}) {
  const ref = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (focusOnMount) ref.current?.focus();
  }, [focusOnMount]);

  if (error === null || error === undefined) return null;

  const presented = present(error);

  return (
    <Alert
      // role="alert" so a screen reader announces a failed submit. Silence after pressing Save is
      // indistinguishable from a hung request (section 8.4 rule 2).
      role="alert"
      ref={ref}
      tabIndex={-1}
      severity={presented.severity}
      action={
        presented.offerReload && onReload !== undefined ? (
          <Button color="inherit" size="small" onClick={onReload}>
            Reload
          </Button>
        ) : undefined
      }
      sx={{ my: 2 }}
    >
      {presented.heading !== undefined && <AlertTitle>{presented.heading}</AlertTitle>}
      {presented.message}
      {presented.traceId !== undefined && (
        <Typography variant="caption" component="p" sx={{ mt: 1 }}>
          Reference: {presented.traceId}
        </Typography>
      )}
    </Alert>
  );
}

interface Presented {
  severity: 'error' | 'warning' | 'info';
  heading?: string;
  message: string;
  /** Only ever set for a 500. */
  traceId?: string;
  offerReload: boolean;
}

const RELOAD_ADVICE = 'Reload and try again.';

/**
 * Section 7.1's 409 message: the server's title, plus the reload advice, and never that advice
 * twice. A title that already carries it is returned untouched; one missing terminal punctuation
 * gains a full stop so two sentences do not run together.
 */
function appendReloadAdvice(title: string): string {
  const trimmed = title.trim();
  if (trimmed.endsWith(RELOAD_ADVICE)) return trimmed;
  const separator = /[.!?]$/.test(trimmed) ? ' ' : '. ';
  return `${trimmed}${separator}${RELOAD_ADVICE}`;
}

function present(error: unknown): Presented {
  if (!(error instanceof ApiError)) {
    // fetch itself rejected: the network is down, or the dev proxy is not running. There is no
    // status and no `title`, and the browser's own message ("Failed to fetch") is not for a user.
    return {
      severity: 'error',
      message: 'Could not reach the server. Check your connection and try again.',
      offerReload: true,
    };
  }

  switch (error.status) {
    case 403:
      // With a `detail` this is the password gate, which http.ts already routed on; render its own
      // sentence rather than the permission one if it somehow arrives here.
      return {
        severity: 'error',
        message: error.isPasswordChangeRequired
          ? error.title
          : 'You do not have permission to do that.',
        offerReload: false,
      };

    case 404:
      // Rule A. Not "forbidden", not "denied", not "you do not have access".
      return { severity: 'info', message: 'Not found.', offerReload: false };

    case 409:
      // Section 7.1 (:973) specifies "the title, plus 'Reload and try again'" -- but it specifies
      // CONTENT, not concatenation, and two shipped 409s make the mechanical version misread.
      // EditTicketTypeHandler.cs:72's message ALREADY ends "Reload and try again.", so appending
      // gives "...edited by someone else. Reload and try again. Reload and try again."; and
      // CreateTicketTypeHandler.cs:17's "A Ticket Type with this code already exists" has no full
      // stop, so appending runs two sentences together. Append only what is missing: the result
      // satisfies :973 in every case and can never say it twice.
      //
      // The arguably better fix is server-side -- a handler should not embed client UX advice in a
      // message, and the duplicate-code 409 should not advise a reload at all, since reloading
      // changes nothing about a code that is already taken. That needs a .cs change; reported.
      return {
        severity: 'warning',
        message: appendReloadAdvice(error.title),
        offerReload: true,
      };

    case 429:
      // From Caddy, not the API. Its body is not ProblemDetails, so `title` here is the fallback
      // from problemDetails.ts. Safe to be specific: the proxy's answer carries no account
      // information at all.
      return {
        severity: 'warning',
        message: 'Too many attempts. Wait a moment and try again.',
        offerReload: false,
      };

    case 500:
      // Rule B: the ONE status that shows a traceId.
      return {
        severity: 'error',
        heading: 'Something went wrong',
        message: 'The server could not complete that request. It has been logged.',
        ...(error.traceId === undefined ? {} : { traceId: error.traceId }),
        offerReload: true,
      };

    case 502:
    case 503:
      return {
        severity: 'error',
        message: 'The server is unavailable. Try again in a moment.',
        offerReload: true,
      };

    default:
      // 400, 401 and 422 -- and anything new the API grows. `title` is the human-readable message
      // for every one of them, written for the user; do not paraphrase it (section 7.3 item 2).
      return { severity: 'error', message: error.title, offerReload: false };
  }
}
