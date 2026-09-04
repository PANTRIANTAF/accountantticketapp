import { useMemo, useState, type ReactNode } from 'react';
import { Link as RouterLink, useParams, useSearchParams } from 'react-router-dom';
import Alert from '@mui/material/Alert';
import Box from '@mui/material/Box';
import Button from '@mui/material/Button';
import Chip from '@mui/material/Chip';
import Paper from '@mui/material/Paper';
import Snackbar from '@mui/material/Snackbar';
import Stack from '@mui/material/Stack';
import Table from '@mui/material/Table';
import TableBody from '@mui/material/TableBody';
import TableCell from '@mui/material/TableCell';
import TableContainer from '@mui/material/TableContainer';
import TableHead from '@mui/material/TableHead';
import TableRow from '@mui/material/TableRow';
import Typography from '@mui/material/Typography';
import { ApiError } from '../../../shared/api/ApiError';
import { DynamicForm } from '../../../shared/dynamicForm/DynamicForm';
import { ErrorBanner } from '../../../shared/components/ErrorBanner';
import { LoadingRegion } from '../../../shared/components/LoadingRegion';
import { NotFoundPage } from '../../../shared/components/NotFoundPage';
import { PageHeader } from '../../../shared/components/PageHeader';
import { formatDate, formatDateTime } from '../../../shared/format/dates';
import { can } from '../../../shared/permissions/can';
import { useAuthenticatedSession } from '../../../shared/auth/useSession';
import { TicketTypeStatusChip } from '../components/TicketTypeStatusChip';
import { ToggleTicketTypeDialog } from '../components/ToggleTicketTypeDialog';
import { VersionBanner } from '../components/VersionBanner';
import { dataTypeLabel } from '../fieldDataTypes';
import { useTicketTypeDetail, useTicketTypeVersion, useToggleTicketType } from '../queries';
import type { FieldDescriptor, TicketTypeDetail } from '../types';

/**
 * Six regions in order. Screens/TicketTypesScreens.md section 4.1.
 *
 * ONE RENDER PATH SERVES BOTH READS. `?version=` absent -> useTicketTypeDetail; present ->
 * useTicketTypeVersion. Both return the same TicketTypeDetailDto.
 *
 * THE TWO READS ARE NEVER CHAINED. /version applies ApplyCustomerSideAudience -- audience only, no
 * IsActive -- while /detail applies ApplyCustomerSideVisibility, audience AND IsActive
 * (TicketTypeMapper.cs:33-43, correction note T-4). So /version can succeed where /detail returns
 * 404, for the same type and the same caller. Falling back from a 404 on /detail to a /version call
 * "to get something to show" is precisely the discovery the 404 refused, and /version is never a way
 * to test whether a type exists (screen spec section 7.3 rule A).
 *
 * A 404 RENDERS "NOT FOUND" AND NEVER "FORBIDDEN". For a Customer Admin or an Employee on a
 * deactivated type, 404 is the DESIGNED answer, not a fault -- it is the scoping mechanism
 * (GeneralUIArchitecture.md section 2.3 rule J). And it is never caught into an empty screen, which
 * would turn that mechanism into a blank page.
 */
export function TicketTypeDetailScreen() {
  const { ticketTypeId } = useParams<{ ticketTypeId: string }>();
  const [searchParams] = useSearchParams();
  const { role } = useAuthenticatedSession();

  const id = ticketTypeId ?? '';
  const versionParam = searchParams.get('version');
  const requestedVersion = parseVersionParam(versionParam);
  const isHistoricalRequest = requestedVersion !== undefined;

  // Both hooks are always called -- hooks cannot be conditional -- and exactly one is enabled.
  const detailQuery = useTicketTypeDetail(id, { enabled: !isHistoricalRequest });
  const versionQuery = useTicketTypeVersion(id, requestedVersion);
  const query = isHistoricalRequest ? versionQuery : detailQuery;

  const toggle = useToggleTicketType();
  const [pendingDeactivation, setPendingDeactivation] = useState(false);
  const [snackbar, setSnackbar] = useState<string | null>(null);

  const detail = query.data;

  /**
   * displayOrder THEN key, and NEVER `fields.sort(...)` in place: the array handed here is the one
   * inside the TanStack Query cache entry, so an in-place sort reorders what every other reader of
   * that key sees with no state change to explain it (section 6.6 rule F). displayOrder alone is not
   * total -- the column has no uniqueness constraint and an older type can have five fields at 0 --
   * so a screenshot would not be reproducible between a fresh fetch and a cache read (rule C).
   */
  const orderedFields = useMemo<readonly FieldDescriptor[]>(
    () => (detail === undefined ? [] : sortFields(detail.fields)),
    [detail],
  );

  if (query.error instanceof ApiError && query.error.status === 404) {
    return <NotFoundPage />;
  }

  if (query.isLoading) {
    return <LoadingRegion label="Loading ticket type" />;
  }

  if (detail === undefined) {
    // Any non-404 failure. ErrorBanner already renders the section 7.1 taxonomy, including the
    // traceId on a 500 and the fixed sentence on a 403 without detail.
    return <ErrorBanner error={query.error} onReload={() => void query.refetch()} />;
  }

  const isHistorical = detail.versionNumber !== detail.currentVersionNumber;

  /**
   * NO EDIT BUTTON AT ALL ON A HISTORICAL VIEW (screen spec section 7.2), not a disabled one and not
   * a confirmation. /edit replaces the field set wholesale from whatever the form holds, so an Edit
   * that opened with v1's fields loaded would mint a new version reverting every edit since -- with
   * a 200 and a version counter that went up by one, which is what success looks like. The banner
   * carries the only route forward, to the current version.
   */
  const showEdit = !isHistorical && can(role, 'EditTicketType');
  /**
   * Toggle acts on the TYPE, not on the version being viewed, and it mints no version
   * (ToggleTicketTypeHandler.cs), so it is offered from a historical view too. Nothing in section 7.2
   * removes it, and removing it would leave an Accountant who stepped back a version unable to
   * retire the type without navigating away.
   */
  const showToggle = can(role, 'ToggleTicketType');

  return (
    <>
      {/* REGION 1 -- header. */}
      <PageHeader
        title={detail.displayName}
        subtitle={
          <Stack direction="row" spacing={1} component="span" sx={{ alignItems: 'center' }}>
            <Box component="span" sx={{ fontFamily: 'monospace' }}>
              {detail.code}
            </Box>
            <TicketTypeStatusChip isActive={detail.isActive} />
            {/* Always the LATEST version that exists, whichever read produced this response. The
                version actually on screen is named by the banner when the two differ. */}
            <Box component="span">v{detail.currentVersionNumber}</Box>
          </Stack>
        }
        {...(showEdit || showToggle
          ? {
              action: (
                <Stack direction="row" spacing={1}>
                  {showEdit && (
                    <Button
                      component={RouterLink}
                      to={`/ticket-types/${detail.id}/edit`}
                      variant="contained"
                    >
                      Edit
                    </Button>
                  )}
                  {showToggle && (
                    <Button
                      variant="outlined"
                      onClick={() => {
                        if (detail.isActive) {
                          setPendingDeactivation(true);
                          return;
                        }
                        toggle.mutate(
                          { ticketTypeId: detail.id, newIsActive: true },
                          {
                            // From the RETURNED isActive, never from what was sent: toggle is
                            // idempotent and silent about a no-op, so a 200 is not evidence that
                            // anything moved.
                            onSuccess: (updated) => {
                              setSnackbar(
                                updated.isActive
                                  ? `${updated.displayName} is active.`
                                  : `${updated.displayName} is inactive.`,
                              );
                            },
                          },
                        );
                      }}
                    >
                      {detail.isActive ? 'Deactivate' : 'Reactivate'}
                    </Button>
                  )}
                </Stack>
              ),
            }
          : {})}
      />

      {/* An unparseable ?version= is reported rather than silently ignored: the reader asked for a
          specific version and is being shown a different one. */}
      {versionParam !== null && requestedVersion === undefined && (
        <Alert severity="warning" sx={{ mb: 2 }}>
          The version in the address was not a version number, so the current version is shown.
        </Alert>
      )}

      {/* REGION 2 -- version banner. Above everything else, and only when the two numbers differ. */}
      {isHistorical && (
        <VersionBanner
          ticketTypeId={detail.id}
          versionNumber={detail.versionNumber}
          currentVersionNumber={detail.currentVersionNumber}
        />
      )}

      <ErrorBanner error={pendingDeactivation ? null : toggle.error} />

      {/* REGION 3 -- summary. */}
      <Paper variant="outlined" sx={{ p: 2, mb: 3 }}>
        <Typography variant="h6" component="h2" gutterBottom>
          Summary
        </Typography>
        <SummaryRow label="Description">
          {detail.description === '' ? <Absent /> : detail.description}
        </SummaryRow>
        <SummaryRow label="Category">{detail.category}</SummaryRow>
        {/* Through format/dates.ts and nowhere else: these are C# DateTime values that may arrive
            with NO offset suffix and are UTC regardless (section 10.2). A bare value handed to
            `new Date` is read as LOCAL time and shifts silently. */}
        <SummaryRow label="Created">{formatDateTime(detail.createdAt)}</SummaryRow>
        <SummaryRow label="Last updated">{formatDateTime(detail.updatedAt)}</SummaryRow>
        {/* Deliberately NOT a created date for the version on screen. `createdAt` here is
            type.CreatedAt -- the TYPE's creation. TicketTypeVersion.CreatedAt exists in the database
            and is projected into no DTO, so a "version created" line would be a number the API does
            not send (plan section 5.2 rule B). */}
      </Paper>

      {/* REGION 4 -- behaviour flags. */}
      <Paper variant="outlined" sx={{ p: 2, mb: 3 }}>
        <Typography variant="h6" component="h2" gutterBottom>
          Behaviour
        </Typography>
        <SummaryRow label="Employees may open tickets of this type">
          {detail.allowEmployeeToOpen ? 'Yes' : 'No'}
          <Typography variant="caption" color="text.secondary" sx={{ display: 'block' }}>
            {detail.allowEmployeeToOpen
              ? 'An Employee of a Customer can raise a ticket of this type themselves.'
              : 'Only a Customer Admin or an Accountant can raise a ticket of this type.'}
          </Typography>
        </SummaryRow>
        <SummaryRow label="A ticket may name someone other than its creator as subject">
          {detail.allowSubjectOtherThanCreator ? 'Yes' : 'No'}
          {/* Shown, and shown WITH THIS SENTENCE, because no handler anywhere reads this flag. An
              Accountant who sets it needs to see that it was stored and that nothing acts on it yet;
              hiding it would make the setting look lost, and showing it silently would imply an
              enforcement that does not exist. */}
          <Typography variant="caption" color="text.secondary" sx={{ display: 'block' }}>
            Stored, but nothing reads it yet — no ticket screens exist in this release, so this
            setting has no effect until they do.
          </Typography>
        </SummaryRow>
      </Paper>

      {/* REGION 5 -- fields table. A plain Table: this is one version's field set, already whole in
          the response, so there is nothing to page and no PaginatedTable to use. */}
      <Paper variant="outlined" sx={{ mb: 3 }}>
        <Box sx={{ p: 2, pb: 1 }}>
          <Typography variant="h6" component="h2">
            {/* The count is detail.fields.length and is derived from nowhere else. For a
                Customer-side caller the server has already removed the fields they may not see, and
                a "3 fields not shown" line would require a number the API does not send AND would
                leak the existence of what was stripped (section 4.2 rule C). */}
            Fields ({orderedFields.length})
          </Typography>
        </Box>

        {orderedFields.length === 0 ? (
          <Box sx={{ px: 2, pb: 2 }}>
            <Typography variant="body2" color="text.secondary">
              This version has no fields.
            </Typography>
          </Box>
        ) : (
          <TableContainer>
            <Table size="small" aria-label="Fields">
              <TableHead>
                <TableRow>
                  <TableCell>Key</TableCell>
                  <TableCell>Label</TableCell>
                  <TableCell>Data type</TableCell>
                  <TableCell>Group</TableCell>
                  <TableCell align="right">Order</TableCell>
                  <TableCell>Required</TableCell>
                  <TableCell>Audience</TableCell>
                  <TableCell>Rules</TableCell>
                </TableRow>
              </TableHead>
              <TableBody>
                {orderedFields.map((field) => (
                  // `key` is UNIQUE(ticket_type_version_id, key), so it is a safe React key.
                  <TableRow key={field.key}>
                    <TableCell>
                      <Box component="span" sx={{ fontFamily: 'monospace' }}>
                        {field.key}
                      </Box>
                    </TableCell>
                    <TableCell>
                      {field.label}
                      {field.helpText !== '' && (
                        <Typography variant="caption" color="text.secondary" sx={{ display: 'block' }}>
                          {field.helpText}
                        </Typography>
                      )}
                    </TableCell>
                    {/* An unrecognised dataType is shown verbatim rather than blanked, so an author
                        can see what the row actually holds. */}
                    <TableCell>{dataTypeLabel(field.dataType)}</TableCell>
                    <TableCell>{field.groupName === '' ? <Absent /> : field.groupName}</TableCell>
                    <TableCell align="right">{field.displayOrder}</TableCell>
                    <TableCell>{field.isRequired ? 'Required' : 'Optional'}</TableCell>
                    <TableCell>
                      {/* "Accountant only", NEVER "hidden" (section 4.2 rule B). "Hidden" invites
                          the reading that the value is concealed from Customers but still collected
                          from them, which is the opposite of what happens: the field is not sent to
                          them and they are never asked for it. This is a BADGE, never a filter --
                          see the count note above. */}
                      {field.isVisibleToCustomer ? (
                        <Typography variant="body2" color="text.secondary">
                          Everyone
                        </Typography>
                      ) : (
                        <Chip label="Accountant only" size="small" variant="outlined" />
                      )}
                    </TableCell>
                    <TableCell>
                      <RulesSummary field={field} />
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </TableContainer>
        )}
      </Paper>

      {/* REGION 6 -- form preview. The only place the section 6 renderer is exercised before a
          Tickets UI exists, and therefore the only way it gets tested at all in this pass.

          READ-ONLY-*ISH*, NOT DISABLED (section 4.2 rule D): mode="preview" renders live, focusable
          controls with no submit button, because a disabled form cannot demonstrate that a
          conditionalVisibility rule works -- the single thing an author most needs to check before
          saving. Nothing typed here is persisted or read back, and NO onSubmit is passed: there is no
          ticket endpoint in this release, and section 10 item 9 forbids growing a Submit button.

          detail.fields is passed WHOLE and unsorted -- DynamicForm groups and orders it itself
          (section 6.6), and orderedFields is this screen's table ordering, which has no groups.

          KEYED ON THE VERSION, so stepping versions REMOUNTS the form. RHF seeds defaultValues once
          and a later change to the prop is deliberately not applied, and the RHF names are positional
          aliases (f0, f1, ...) -- so without this key, v1's third answer would survive into v3 as the
          value of whatever field happens to sit third there. */}
      <Paper variant="outlined" sx={{ p: 2, mb: 3 }}>
        <Typography variant="h6" component="h2" gutterBottom>
          Form preview
        </Typography>
        <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>
          This is how the form looks to whoever fills it in. Answering a field here shows and hides
          any field that depends on it. Nothing typed into this preview is saved.
        </Typography>
        {orderedFields.length === 0 ? (
          <Typography variant="body2" color="text.secondary">
            This version has no fields to preview.
          </Typography>
        ) : (
          <DynamicForm
            key={`${detail.id}-v${String(detail.versionNumber)}`}
            mode="preview"
            fields={detail.fields}
          />
        )}
      </Paper>

      {/* Version stepping is by NUMBER, and that is the whole of it: there is no version-history
          endpoint, and looping /version from 1 to currentVersionNumber to fake one is fifty requests
          on page load for a type edited fifty times (section 4.2 rule E). Both bounds come from the
          response already in hand. */}
      <VersionStepper detail={detail} />

      <ToggleTicketTypeDialog
        open={pendingDeactivation}
        displayName={detail.displayName}
        code={detail.code}
        isPending={toggle.isPending}
        error={toggle.error}
        onClose={() => {
          setPendingDeactivation(false);
          toggle.reset();
        }}
        onConfirm={() => {
          toggle.mutate(
            { ticketTypeId: detail.id, newIsActive: false },
            {
              onSuccess: (updated) => {
                setPendingDeactivation(false);
                setSnackbar(
                  updated.isActive
                    ? `${updated.displayName} is active.`
                    : `${updated.displayName} is inactive.`,
                );
              },
            },
          );
        }}
      />

      <Snackbar
        open={snackbar !== null}
        message={snackbar ?? ''}
        autoHideDuration={4000}
        onClose={() => setSnackbar(null)}
      />
    </>
  );
}

/** Previous / Next bounded by 1 and currentVersionNumber. */
function VersionStepper({ detail }: { detail: TicketTypeDetail }) {
  const previous = detail.versionNumber - 1;
  const next = detail.versionNumber + 1;
  const hasPrevious = previous >= 1;
  const hasNext = next <= detail.currentVersionNumber;

  if (detail.currentVersionNumber <= 1) return null;

  return (
    <Stack direction="row" spacing={1} sx={{ alignItems: 'center', mb: 3 }}>
      <Button
        component={RouterLink}
        to={`/ticket-types/${detail.id}?version=${previous}`}
        disabled={!hasPrevious}
        size="small"
      >
        Previous version
      </Button>
      <Typography variant="body2" color="text.secondary">
        Version {detail.versionNumber} of {detail.currentVersionNumber}
      </Typography>
      <Button
        component={RouterLink}
        // Stepping to the LATEST version drops the parameter, so the current view is reached through
        // /detail rather than through /version -- /version does not apply the IsActive check.
        to={
          next === detail.currentVersionNumber
            ? `/ticket-types/${detail.id}`
            : `/ticket-types/${detail.id}?version=${next}`
        }
        disabled={!hasNext}
        size="small"
      >
        Next version
      </Button>
    </Stack>
  );
}

/** A one-line summary of `validation` and `conditionalVisibility`, or a dash when there is neither. */
function RulesSummary({ field }: { field: FieldDescriptor }) {
  const rules = describeValidation(field);
  const visibility = field.conditionalVisibility;

  if (rules.length === 0 && visibility === null) return <Absent />;

  return (
    <>
      {rules.length > 0 && (
        <Typography variant="body2" component="span">
          {rules.join(', ')}
        </Typography>
      )}
      {visibility !== null && (
        <Typography variant="caption" color="text.secondary" sx={{ display: 'block' }}>
          {/* The rule as the author wrote it, quoted, with no attempt to render the target field's
              label: the target may not be in this array at all, and a missing label would read as an
              empty comparison. */}
          Shown only when “{visibility.fieldKey}” is “{visibility.value}”
        </Typography>
      )}
    </>
  );
}

/**
 * '' and [] mean "no rule" -- see FieldValidation. A `0` does NOT: minValue 0 and maxFileSizeBytes 0
 * are real bounds, so every numeric member is tested against null and undefined rather than for
 * truthiness.
 */
function describeValidation(field: FieldDescriptor): string[] {
  const { validation } = field;
  const parts: string[] = [];

  const has = (value: number | null | undefined): value is number =>
    value !== null && value !== undefined;

  if (has(validation.minLength)) parts.push(`min length ${validation.minLength}`);
  if (has(validation.maxLength)) parts.push(`max length ${validation.maxLength}`);
  if (has(validation.minValue)) parts.push(`min ${validation.minValue}`);
  if (has(validation.maxValue)) parts.push(`max ${validation.maxValue}`);
  // DateOnly strings, formatted through format/dates.ts, which formats the parts directly rather
  // than building a Date -- `new Date("2026-09-02")` is midnight UTC and renders as the previous day
  // west of UTC (section 10.2).
  if (validation.earliestDate) parts.push(`from ${formatDate(validation.earliestDate)}`);
  if (validation.latestDate) parts.push(`until ${formatDate(validation.latestDate)}`);
  // The pattern is shown, never compiled here: this screen does not evaluate user-supplied regex.
  if (validation.regexPattern !== '') parts.push(`pattern ${validation.regexPattern}`);
  if (validation.allowedFileTypes.length > 0) {
    parts.push(`file types ${validation.allowedFileTypes.join(' ')}`);
  }
  if (has(validation.maxFileSizeBytes)) {
    parts.push(`max size ${formatBytes(validation.maxFileSizeBytes)}`);
  }
  if (field.choiceOptions.length > 0) {
    parts.push(`${field.choiceOptions.length} options`);
  }

  return parts;
}

/** Whole units only, and no locale-specific separators to get wrong. */
function formatBytes(bytes: number): string {
  if (bytes >= 1024 * 1024) return `${Math.round(bytes / (1024 * 1024))} MB`;
  if (bytes >= 1024) return `${Math.round(bytes / 1024)} KB`;
  return `${bytes} bytes`;
}

function SummaryRow({ label, children }: { label: string; children: ReactNode }) {
  return (
    <Box sx={{ display: 'flex', flexDirection: { xs: 'column', sm: 'row' }, py: 0.75, gap: 1 }}>
      <Typography variant="body2" color="text.secondary" sx={{ minWidth: 280 }}>
        {label}
      </Typography>
      <Box>
        <Typography variant="body2" component="span">
          {children}
        </Typography>
      </Box>
    </Box>
  );
}

/** An em dash for an empty optional, so a blank cell is never read as a failed load. */
function Absent() {
  return (
    <Typography variant="body2" color="text.secondary" component="span" aria-label="none">
      —
    </Typography>
  );
}

/**
 * displayOrder, then `key`, on a COPY. localeCompare with sensitivity 'base' first so accents and
 * case do not dominate, then an ordinal tie-break so two keys differing only in case do not tie
 * (section 6.6 rule C).
 */
function sortFields(fields: readonly FieldDescriptor[]): FieldDescriptor[] {
  return [...fields].sort((a, b) => {
    if (a.displayOrder !== b.displayOrder) return a.displayOrder - b.displayOrder;
    const byBase = a.key.localeCompare(b.key, undefined, { sensitivity: 'base' });
    if (byBase !== 0) return byBase;
    return a.key < b.key ? -1 : a.key > b.key ? 1 : 0;
  });
}

/**
 * A positive integer, or undefined. Number('') is 0 and Number('3abc') is NaN, so the string is
 * tested before it is converted; `1..currentVersionNumber` is the server's own gapless range but the
 * upper bound is not known until the response arrives, so it is not checked here -- an out-of-range
 * number is a 404 from /version, rendered as "Not found" rather than crashing the stepper.
 */
function parseVersionParam(value: string | null): number | undefined {
  if (value === null || !/^\d+$/.test(value)) return undefined;
  const parsed = Number(value);
  return parsed >= 1 ? parsed : undefined;
}
