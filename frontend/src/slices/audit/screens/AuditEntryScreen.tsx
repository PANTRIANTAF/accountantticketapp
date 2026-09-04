import type { ReactNode } from 'react';
import { useLocation, useNavigate, useParams, Link as RouterLink } from 'react-router-dom';
import Box from '@mui/material/Box';
import Button from '@mui/material/Button';
import Link from '@mui/material/Link';
import Paper from '@mui/material/Paper';
import Typography from '@mui/material/Typography';
import ArrowBackIcon from '@mui/icons-material/ArrowBack';
import { ApiError } from '../../../shared/api/http';
import { AccessDeniedPage } from '../../../shared/components/AccessDeniedPage';
import { ErrorBanner } from '../../../shared/components/ErrorBanner';
import { LoadingRegion } from '../../../shared/components/LoadingRegion';
import { NotFoundPage } from '../../../shared/components/NotFoundPage';
import { PageHeader } from '../../../shared/components/PageHeader';
import { StatusChip } from '../../../shared/components/StatusChip';
import { auditRoleLabel, dashIfEmpty, formatOccurredAt, targetRoute } from '../auditFormat';
import { AuditPayloadPanel } from '../components/AuditPayloadPanel';
import { isGuid } from '../guid';
import { useAuditEntry } from '../queries';

/**
 * One audit entry. Route /audit/:auditEntryId, AccountantAdmin only. AuditScreens.md section 4.
 *
 * READ-ONLY, AND NOT "READ-ONLY FOR NOW". There is no edit, no delete, no annotate, no acknowledge
 * and no export -- not even disabled. Matrix section 10: "Edit or delete an audit entry -- Nobody. No
 * API exists for this", and 20260901_002_ReshapeAuditEntries.sql ends "Append-only. No UPDATE or
 * DELETE path exists in the application." A greyed-out control here would misrepresent the guarantee
 * the table exists to provide.
 *
 * NO NEXT/PREVIOUS ENTRY (section 4 rule E). The endpoint takes one id and the API has no adjacency
 * concept, so "next" could only be synthesised from the last search page -- which is silently wrong
 * the moment the reader arrived by a shared link, because the neighbours would be the SHARER's
 * filtered neighbours, not the log's.
 */
export function AuditEntryScreen() {
  const { auditEntryId } = useParams();
  const navigate = useNavigate();
  const location = useLocation();

  /**
   * THE HOOK IS CALLED UNCONDITIONALLY AND GATED BY `enabled` (section 4 rule C). AuditEndpoints.cs:28
   * binds `Guid auditEntryId`, so a malformed value is a 400 from parameter binding whose body says
   * nothing a reader can act on -- and returning early before the hook would change the hook count
   * when the reader edits the URL from a bad id to a good one.
   *
   * `enabled` here is the ID, NEVER PERMISSION: gating on a role would render an empty screen where a
   * denial belongs, and permission is the route guard's job and the server's.
   */
  const entry = useAuditEntry(auditEntryId ?? '');

  /**
   * BACK USES HISTORY, NOT A HARDCODED /audit (section 4 rule D), so the filters, the page and the
   * scroll position of a search over hundreds of thousands of rows survive.
   *
   * location.key === 'default' means this is the first entry in this history stack -- a shared link,
   * a bookmark, a new tab -- and navigate(-1) would leave the application entirely. That case falls
   * back to the bare /audit URL, which starts an unfiltered search rather than nothing at all.
   */
  const backToAuditLog = (
    <Button
      startIcon={<ArrowBackIcon />}
      onClick={() => {
        if (location.key === 'default') {
          void navigate('/audit');
        } else {
          void navigate(-1);
        }
      }}
      sx={{ mb: 1 }}
    >
      Back to audit log
    </Button>
  );

  // NOT A GUID: NotFoundPage, and no request was issued (section 4 rule A). "Not found" is the only
  // honest wording -- never "forbidden" and never "deleted", because entries are never deleted.
  if (!isGuid(auditEntryId)) {
    return (
      <>
        {backToAuditLog}
        <NotFoundPage />
      </>
    );
  }

  if (entry.error instanceof ApiError) {
    // A 404 means the id is wrong, or the row is out of scope for the caller -- the API returns 404
    // rather than 403 on purpose, because a 403 would confirm the row exists (section 1 rule E). The
    // server's own sentence is "Audit entry not found." (GetAuditEntryHandler.cs:36).
    if (entry.error.status === 404) {
      return (
        <>
          {backToAuditLog}
          <NotFoundPage />
        </>
      );
    }

    // A bare 403 is reachable only if RequireRole and the server's ReadAuditLog grant disagree --
    // a client bug, not a server one (section 6.2 rule B). A 403 carrying `detail` is the
    // forced-password-change gate and http.ts intercepts it before this line.
    if (entry.error.status === 403) {
      return <AccessDeniedPage />;
    }
  }

  if (entry.isLoading) {
    return (
      <>
        {backToAuditLog}
        <LoadingRegion label="Loading the audit entry…" />
      </>
    );
  }

  if (entry.data === undefined) {
    return (
      <>
        {backToAuditLog}
        <ErrorBanner error={entry.error} />
      </>
    );
  }

  const record = entry.data;
  const route = targetRoute(record.targetKind, record.targetId);

  return (
    <>
      {backToAuditLog}

      <PageHeader
        title="Audit entry"
        subtitle={formatOccurredAt(record.occurredAt)}
        // The outcome is the first thing a reader needs and the header is where the eye lands.
        // Through the shared chip, with the word showing, never a bare colour.
        action={<StatusChip status={record.outcome} size="medium" />}
      />

      <Paper variant="outlined" sx={{ p: 2, mb: 3 }}>
        <Box
          sx={{
            display: 'grid',
            gridTemplateColumns: { xs: '1fr', sm: 'repeat(2, 1fr)' },
            gap: 2,
          }}
        >
          <Field label="Occurred">{formatOccurredAt(record.occurredAt)}</Field>

          {/* THE FULL ID HERE, shortened only in the table. There is no name to resolve it to: no
              endpoint maps a UserAccount id to a person (punch-list item 23), and a best-effort
              join would put a name on some rows and not others, which reads as a data-quality
              problem in the log rather than a gap in this client. */}
          <Field label="Actor" mono>
            {dashIfEmpty(record.actorUserId)}
          </Field>

          {/* The role AT THE TIME of the action, not the actor's role now, and a string rather than
              the integer `role` is everywhere else in the API. "Unknown" and any unrecognised value
              are rendered verbatim: a role this UI does not know is itself information. */}
          <Field label="Role at the time">{auditRoleLabel(record.actorRole)}</Field>

          {/* The catalogue code, monospace and verbatim (section 4 rule E). "Permission denied" is
              friendlier, but PermissionDenied is what the reader pastes into the Action filter and
              greps the source for -- and the filter 422s the humanised form. */}
          <Field label="Action" mono>
            {dashIfEmpty(record.action)}
          </Field>

          {/* "None" is a real target kind, not a gap. The id is LINKED ONLY WHERE AN SPA ROUTE
              EXISTS (section 4 rule D): five of the eight kinds have no screen in this application,
              so a link would render NotFoundPage and read as a broken audit log. */}
          <Field label="Target" mono>
            {dashIfEmpty(record.targetKind)}
            {record.targetId.trim() !== '' && (
              <>
                {' · '}
                {route === null ? (
                  record.targetId
                ) : (
                  <Link component={RouterLink} to={route}>
                    {record.targetId}
                  </Link>
                )}
              </>
            )}
          </Field>

          {/* null means NO CUSTOMER WAS INVOLVED -- an Accountant invitation, a failed login, a
              ticket-type edit. Never "All Customers", which would invert the meaning of the most
              sensitive field on the screen. */}
          <Field label="Customer" mono>
            {dashIfEmpty(record.customerId)}
          </Field>

          {/* "Source IP", never "the user's IP address": behind Caddy this may uniformly be the
              proxy's address, because forwarded headers are not configured (punch-list items 2 and
              25). Truncated to 45 characters at write time and rendered verbatim, with no ellipsis
              and no reverse lookup. */}
          <Field label="Source IP" mono>
            {dashIfEmpty(record.sourceIp)}
          </Field>

          {/* THE RAW HEADER, NEVER PARSED into "Chrome on Windows". It is clipped at 512 characters
              at write time (AuditApi.cs:56) and real user agents do exceed that, so the parse would
              run on a mutilated string and replace evidence with a guess -- on the one screen whose
              entire value is holding what actually arrived. Wrapped, not truncated. */}
          <Box sx={{ gridColumn: { sm: '1 / -1' } }}>
            <Field label="User agent" mono>
              {dashIfEmpty(record.userAgent)}
            </Field>
          </Box>
        </Box>
      </Paper>

      {/* Both sides of the change, side by side on a wide screen. Both are JSON TEXT from a jsonb
          column, already redacted at write time, and neither is ever truncated by this client. */}
      <Box
        sx={{
          display: 'grid',
          gridTemplateColumns: { xs: '1fr', md: 'repeat(2, 1fr)' },
          gap: 2,
        }}
      >
        <AuditPayloadPanel label="Before" value={record.beforeValue} />
        <AuditPayloadPanel label="After" value={record.afterValue} />
      </Box>
    </>
  );
}

/** One label-and-value pair from the metadata block. */
function Field({
  label,
  mono = false,
  children,
}: {
  label: string;
  /** Identifiers are monospace: they are compared character by character, not read as prose. */
  mono?: boolean;
  children: ReactNode;
}) {
  return (
    <Box>
      <Typography variant="caption" color="text.secondary" component="div">
        {label}
      </Typography>
      <Typography
        variant="body2"
        component="div"
        sx={mono ? { fontFamily: 'monospace', overflowWrap: 'anywhere' } : undefined}
      >
        {children}
      </Typography>
    </Box>
  );
}
