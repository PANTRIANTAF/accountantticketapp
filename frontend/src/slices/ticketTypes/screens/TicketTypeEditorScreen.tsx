import { zodResolver } from '@hookform/resolvers/zod';
import AddIcon from '@mui/icons-material/Add';
import LockOutlinedIcon from '@mui/icons-material/LockOutlined';
import Alert from '@mui/material/Alert';
import AlertTitle from '@mui/material/AlertTitle';
import Box from '@mui/material/Box';
import Button from '@mui/material/Button';
import Divider from '@mui/material/Divider';
import FormControlLabel from '@mui/material/FormControlLabel';
import Paper from '@mui/material/Paper';
import Snackbar from '@mui/material/Snackbar';
import Stack from '@mui/material/Stack';
import Switch from '@mui/material/Switch';
import TextField from '@mui/material/TextField';
import Typography from '@mui/material/Typography';
import { useQueryClient } from '@tanstack/react-query';
import { useEffect, useRef, useState } from 'react';
import { Controller, FormProvider, useFieldArray, useForm } from 'react-hook-form';
import { useNavigate, useParams } from 'react-router-dom';
import { ApiError } from '../../../shared/api/ApiError';
import { ErrorBanner } from '../../../shared/components/ErrorBanner';
import { LoadingRegion } from '../../../shared/components/LoadingRegion';
import { NotFoundPage } from '../../../shared/components/NotFoundPage';
import { PageHeader } from '../../../shared/components/PageHeader';
import { FieldDescriptorEditor } from '../components/FieldDescriptorEditor';
import { StaleVersionBanner } from '../components/StaleVersionBanner';
import {
  fetchCurrentTicketTypeDetail,
  useCreateTicketType,
  useEditTicketType,
  useTicketTypeDetail,
} from '../queries';
import {
  blankField,
  blankTicketType,
  ticketTypeFormSchema,
  toCreateRequest,
  toEditRequest,
  toFormValues,
  type TicketTypeFormValues,
} from '../schemas';
import type { TicketTypeDetail } from '../types';

/**
 * ONE COMPONENT, TWO MODES, chosen from `useParams().ticketTypeId`. Mounted on BOTH
 * /ticket-types/new and /ticket-types/:ticketTypeId/edit. Screens/TicketTypesScreens.md section 5.
 *
 * THE THREE THINGS THAT MAKE THIS FORM DANGEROUS (section 5.1), and where each is handled:
 *
 *  1. `fields` IS A FULL REPLACEMENT THAT MINTS A NEW VERSION. EditTicketTypeHandler.cs:51-56 builds
 *     the new version's descriptors from `req.Fields` and nothing else, so the editor loads the
 *     COMPLETE field array and submits ALL of it every time -- `toFormValues` then `toEditRequest`,
 *     never a payload assembled from RHF's `dirtyFields`, never a row omitted because nobody touched
 *     it, and no field row behind an accordion that has to be opened before its values exist.
 *  2. EVERY SAVE IS A NEW VERSION, INCLUDING A SAVE THAT CHANGED NOTHING. There is no no-op path on
 *     /edit, unlike toggle. Hence the snackbar names the version -- "Saved as version 4." -- and Save
 *     is disabled ONLY while the mutation is pending.
 *  3. THERE IS NO CONCURRENCY CONTROL ANYWHERE IN THE BUILT BACKEND. Hence the mandatory pre-submit
 *     stale check below, which is a mitigation and not a fix.
 *
 * NO can() GATE HERE. Both routes are already restricted to AccountantAdmin and AccountantUser by
 * ROUTE_ROLES (RequireSession.tsx:129,131), which is exactly the role set of CreateTicketType and
 * EditTicketType in the action catalogue. A second copy of that decision on this screen is a second
 * place for it to drift.
 */
export function TicketTypeEditorScreen() {
  const { ticketTypeId } = useParams<{ ticketTypeId: string }>();
  const navigate = useNavigate();
  const queryClient = useQueryClient();

  /**
   * The route parameter is the mode. `/ticket-types/new` is declared BEFORE
   * `/ticket-types/:ticketTypeId` in routes.tsx, so "new" is never captured as an id.
   */
  const isEdit = ticketTypeId !== undefined;
  const id = ticketTypeId ?? '';

  const detailQuery = useTicketTypeDetail(id, { enabled: isEdit });
  const create = useCreateTicketType();
  const edit = useEditTicketType();

  const [snackbar, setSnackbar] = useState<string | null>(null);
  const [staleConflict, setStaleConflict] = useState<TicketTypeDetail | null>(null);
  /** A failure of the pre-submit read itself -- the POST was never sent. */
  const [preflightError, setPreflightError] = useState<unknown>(null);
  const [isPreflighting, setIsPreflighting] = useState(false);

  const form = useForm<TicketTypeFormValues>({
    // Section 9.3 rule A. Validating on change turns "a code is required" into a message that appears
    // after the first keystroke of a code the user is still typing.
    mode: 'onBlur',
    resolver: zodResolver(ticketTypeFormSchema),
    defaultValues: blankTicketType(),
  });

  const fieldArray = useFieldArray({ control: form.control, name: 'fields' });

  /**
   * RECORDED WHEN THE FORM IS FIRST POPULATED, AND NOT UPDATED ON RE-RENDER (section 5.6). A ref and
   * not state: reading it must never be a render-ordering question, and changing it must never be a
   * reason to re-render. It is advanced exactly once more, after a successful save, to the version
   * that save minted -- otherwise the next save on the same screen would compare against the version
   * before last and refuse itself.
   */
  const loadedVersionRef = useRef<number | null>(null);
  /**
   * What was loaded, kept for the stale banner's diff. The FORM's current values cannot serve: they
   * are what the author changed, and the banner has to say what SOMEBODY ELSE changed.
   */
  const loadedDetailRef = useRef<TicketTypeDetail | null>(null);
  const isPopulatedRef = useRef(false);

  const detail = detailQuery.data;
  /**
   * SECTION 5.7 / PLAN SECTION 9.3: the detail on hand must be the CURRENT version. /detail always
   * returns the current one (GetTicketTypeHandler passes CurrentVersionOf), so this can only be true
   * for a cache entry seeded from elsewhere -- but the consequence of being wrong is a mass revert on
   * a 200 OK, so it is checked rather than assumed.
   */
  const isHistorical = detail !== undefined && detail.versionNumber !== detail.currentVersionNumber;

  useEffect(() => {
    if (!isEdit || detail === undefined || isPopulatedRef.current) return;
    // A historical detail is refused, not loaded. Populating and then hiding Save would leave the
    // author's v1 field list one browser Back away from being submitted.
    if (detail.versionNumber !== detail.currentVersionNumber) return;

    isPopulatedRef.current = true;
    loadedVersionRef.current = detail.currentVersionNumber;
    loadedDetailRef.current = detail;
    // reset, not eleven setValue calls: this is the form's new baseline, so isDirty must restart from
    // it. It runs ONCE -- a background refetch that re-ran it would discard whatever the author had
    // typed since, silently.
    form.reset(toFormValues(detail));
  }, [isEdit, detail, form]);

  /**
   * SECTION 5.5 RULE E: displayOrder := array index, densely from 0, on every change.
   *
   * The displayOrder LEAVES are rewritten rather than the whole `fields` array: a setValue on the
   * array's own path emits on RHF's array subject, which makes useFieldArray regenerate every row's
   * key and remount all of them -- losing focus and every open control on every move. `toFieldRequest`
   * recomputes the numbers from the index again on submit, so the wire value cannot disagree with the
   * order on screen even if this ever missed a row.
   */
  function renumberDisplayOrder() {
    form.getValues('fields').forEach((_row, index) => {
      form.setValue(`fields.${index}.displayOrder`, index, { shouldDirty: true });
    });
  }

  async function submit(values: TicketTypeFormValues) {
    setPreflightError(null);

    if (!isEdit) {
      try {
        const created = await create.mutateAsync(toCreateRequest(values));
        /**
         * TO THE DETAIL SCREEN, replacing this history entry. Staying here would leave a form in
         * CREATE mode holding a code that now exists, so a second Save is a guaranteed 409; and the
         * destination is the confirmation -- it shows the code, the status and v1. There is no
         * snackbar because a snackbar on a screen being unmounted is a message nobody reads.
         */
        navigate(`/ticket-types/${created.id}`, { replace: true });
      } catch {
        // The message is in create.error, rendered by the banner above Save. Swallowed here so a
        // failed save is not also an unhandled rejection in the console.
      }
      return;
    }

    /**
     * STEP 2 OF GeneralUIArchitecture.md SECTION 9.4: RE-READ IMMEDIATELY BEFORE WRITING.
     * fetchQuery and not invalidateQueries -- the value is needed here and now, not as a background
     * refresh that resolves after the POST.
     */
    let latest: TicketTypeDetail;
    setIsPreflighting(true);
    try {
      latest = await fetchCurrentTicketTypeDetail(queryClient, id);
    } catch (error) {
      // The read failed, so nothing is known about the server's state and NOTHING IS SENT. Saving
      // anyway would be submitting precisely because the check could not be made.
      setPreflightError(error);
      return;
    } finally {
      setIsPreflighting(false);
    }

    if (latest.currentVersionNumber !== loadedVersionRef.current) {
      /**
       * STEP 3: DO NOT SUBMIT. `fields` is a full replacement, so submitting would replace their
       * version's field list with ours.
       *
       * THIS NARROWS THE RACE; IT DOES NOT CLOSE IT. Between this read and the POST below another
       * Accountant can still save, and both callers still receive 200 OK -- there is no row-version
       * column and no lost-update check anywhere in the built backend, and the audit log records two
       * TicketTypeVersionCreated entries, which is what two legitimate edits look like. The fix is a
       * version column and a 409 on mismatch: item 7 in UI/BACKEND_CHANGES_REQUIRED.md. The banner
       * says as much to the user, in its own copy.
       */
      setStaleConflict(latest);
      return;
    }

    try {
      const updated = await edit.mutateAsync(toEditRequest(id, values));
      // The baseline moves to the version that was just minted. Without this the next Save compares
      // against the version before it and refuses itself with a conflict that does not exist.
      loadedVersionRef.current = updated.currentVersionNumber;
      loadedDetailRef.current = updated;
      form.reset(toFormValues(updated));
      // NAMES THE VERSION (section 5.1 item 2): silent success on an operation that increments a
      // counter is how a catalogue reaches v30 by accident.
      setSnackbar(`Saved as version ${String(updated.currentVersionNumber)}.`);
    } catch {
      // NEVER RESET THE FORM ON ERROR (section 9.3 rule D). Twelve field rows must survive a 422 and
      // a 409; the message is rendered by the banner above Save.
    }
  }

  // ----- Edit-mode load states. Create mode has nothing to load. -----

  if (isEdit && detailQuery.error instanceof ApiError && detailQuery.error.status === 404) {
    // 404 is the designed answer for an out-of-scope row and renders as "not found", never as
    // "forbidden" (section 2.3 rule J).
    return <NotFoundPage />;
  }

  if (isEdit && detailQuery.isLoading) {
    return <LoadingRegion label="Loading ticket type" />;
  }

  if (isEdit && detail === undefined) {
    return (
      <ErrorBanner error={detailQuery.error} onReload={() => void detailQuery.refetch()} />
    );
  }

  const isPending = create.isPending || edit.isPending || isPreflighting;
  /** Submit is blocked while a stale conflict stands, and there is no *Save anyway*. */
  const isBlocked = isHistorical || staleConflict !== null;

  return (
    <>
      <PageHeader
        title={isEdit ? `Edit ${detail?.displayName ?? ''}` : 'New ticket type'}
        subtitle={
          isEdit
            ? 'Saving creates a new version. The field list is replaced whole, not merged.'
            : 'The template a ticket of this kind is generated from.'
        }
      />

      {isHistorical && detail !== undefined && (
        /**
         * A BLOCKING BANNER AND NO SUBMIT BUTTON (section 5.7). /edit replaces the field set with
         * whatever the form holds, so saving a form populated from v1 while v5 exists mints v6
         * containing v1's fields: four versions of work reverted, a 200 OK, and the only trace is a
         * version number that went up.
         */
        <Alert severity="warning" sx={{ mb: 3 }}>
          <AlertTitle>
            This is version {detail.versionNumber}, and version {detail.currentVersionNumber} is the
            current one.
          </AlertTitle>
          <Typography variant="body2" sx={{ mb: 2 }}>
            An old version cannot be edited. Saving from here would replace the current field list
            with this one and revert everything changed since.
          </Typography>
          <Button
            variant="contained"
            onClick={() => {
              isPopulatedRef.current = false;
              void detailQuery.refetch();
            }}
          >
            Edit the current version
          </Button>
        </Alert>
      )}

      {staleConflict !== null && loadedVersionRef.current !== null && (
        <StaleVersionBanner
          loadedVersion={loadedVersionRef.current}
          latest={staleConflict}
          loadedFieldKeys={(loadedDetailRef.current?.fields ?? []).map((field) => field.key)}
          loadedDisplayName={loadedDetailRef.current?.displayName ?? ''}
          loadedCategory={loadedDetailRef.current?.category ?? ''}
          loadedAllowEmployeeToOpen={loadedDetailRef.current?.allowEmployeeToOpen ?? false}
          loadedAllowSubjectOtherThanCreator={
            loadedDetailRef.current?.allowSubjectOtherThanCreator ?? false
          }
          onReload={() => {
            /**
             * DISCARD AND RELOAD FROM THE VERSION THAT NOW EXISTS. The fresh detail is already in the
             * cache -- fetchCurrentTicketTypeDetail wrote it there -- so this re-populates from it
             * rather than reading again.
             */
            loadedVersionRef.current = staleConflict.currentVersionNumber;
            loadedDetailRef.current = staleConflict;
            form.reset(toFormValues(staleConflict));
            setStaleConflict(null);
          }}
          // Leaves submit blocked. It is a way to copy work out of the form, not a way past the check.
          onKeepEditing={() => {
            setSnackbar('Saving is blocked while an older version is open. Reload to continue.');
          }}
        />
      )}

      {/* FormProvider, because the four editor components are nested three deep and each needs the
          same control. Threading `control` through FieldDescriptorEditor as a prop makes every one of
          them generic over the form type for no benefit. */}
      <FormProvider {...form}>
        <Box
          component="form"
          noValidate
          onSubmit={(event) => {
            event.preventDefault();
            if (isBlocked) return;
            void form.handleSubmit(submit)(event);
          }}
        >
          {/* ----- The type-level form (section 5.3). ----- */}
          <Paper variant="outlined" sx={{ p: 2, mb: 3 }}>
            <Stack spacing={2}>
              {isEdit ? (
                /**
                 * READ-ONLY, AND ABSENT FROM THE PAYLOAD (section 5.2). EditTicketTypeRequestDto has
                 * no Code property and System.Text.Json silently ignores an unknown one, so a
                 * TextField here would accept an edit, report success, and show the old value again
                 * once the cache is seeded from the response -- which reads as a save that failed
                 * silently. Rendered as a labelled value rather than a disabled input, because a
                 * disabled input is a control that might be enabled by something.
                 */
                <Box>
                  <Typography variant="caption" color="text.secondary" component="p">
                    Code
                  </Typography>
                  <Stack direction="row" spacing={0.5} sx={{ alignItems: 'center' }}>
                    <LockOutlinedIcon fontSize="small" color="disabled" />
                    <Typography variant="body1" sx={{ fontFamily: 'monospace' }}>
                      {detail?.code ?? ''}
                    </Typography>
                  </Stack>
                  <Typography variant="caption" color="text.secondary" component="p">
                    A ticket type&apos;s code never changes.
                  </Typography>
                </Box>
              ) : (
                <TextField
                  label="Code"
                  required
                  {...form.register('code')}
                  error={Boolean(form.formState.errors.code)}
                  helperText={
                    form.formState.errors.code?.message ??
                    // Case-insensitive: CreateTicketTypeHandler.cs:45 compares lower-cased codes and
                    // idx_ticket_types_code_lower is unique on LOWER(code).
                    'Permanent, and unique whatever the capitalisation. This is the only chance to set it.'
                  }
                  fullWidth
                />
              )}

              <TextField
                label="Display name"
                required
                {...form.register('displayName')}
                error={Boolean(form.formState.errors.displayName)}
                helperText={form.formState.errors.displayName?.message}
                fullWidth
              />

              <TextField
                label="Category"
                required
                {...form.register('category')}
                error={Boolean(form.formState.errors.category)}
                helperText={
                  form.formState.errors.category?.message ??
                  // A free-text column on the list screen, not a grouping and not a lookup: there is
                  // no categories endpoint.
                  'Free text. Shown as a column on the ticket types list.'
                }
                fullWidth
              />

              <TextField
                label="Description"
                multiline
                minRows={3}
                {...form.register('description')}
                error={Boolean(form.formState.errors.description)}
                helperText={form.formState.errors.description?.message}
                fullWidth
              />

              <Controller
                control={form.control}
                name="allowEmployeeToOpen"
                render={({ field }) => (
                  <Box>
                    <FormControlLabel
                      control={
                        <Switch
                          checked={field.value}
                          onChange={(event) => {
                            field.onChange(event.target.checked);
                          }}
                        />
                      }
                      label="Allow Employee to open"
                    />
                    {/* The consequence is stated, because it is severe and invisible from this screen:
                        the type disappears from every Employee's list AND their reads become 404
                        (ListTicketTypesHandler.cs:32-33; TicketTypeMapper.cs:30-31). */}
                    <Typography variant="caption" color="text.secondary" component="p">
                      Turning this off hides the type from every Employee&apos;s list and makes their
                      reads return &ldquo;not found&rdquo;.
                    </Typography>
                  </Box>
                )}
              />

              <Controller
                control={form.control}
                name="allowSubjectOtherThanCreator"
                render={({ field }) => (
                  <Box>
                    <FormControlLabel
                      control={
                        <Switch
                          checked={field.value}
                          onChange={(event) => {
                            field.onChange(event.target.checked);
                          }}
                        />
                      }
                      label="Allow subject other than creator"
                    />
                    {/* Authored, stored, displayed -- and read by no handler anywhere.
                        CreateTicketHandler.cs:93-95 restricts an Employee to a ticket about themselves
                        unconditionally and :107 reads AllowEmployeeToOpen but never this flag. Shown
                        with the note rather than hidden, because an Accountant setting it needs to see
                        that it was stored. */}
                    <Typography variant="caption" color="text.secondary" component="p">
                      Stored, but nothing reads it yet, so it has no effect on who a ticket can be
                      about.
                    </Typography>
                  </Box>
                )}
              />
            </Stack>
          </Paper>

          {/* ----- The field rows (section 5.4). ----- */}
          <Stack direction="row" sx={{ alignItems: 'baseline', justifyContent: 'space-between', mb: 1 }}>
            <Typography variant="h6" component="h2">
              Fields
            </Typography>
            <Typography variant="body2" color="text.secondary">
              {fieldArray.fields.length === 1 ? '1 field' : `${String(fieldArray.fields.length)} fields`}
            </Typography>
          </Stack>

          {/* The array-level message -- "A ticket type needs at least one field." -- has no row to
              land on, so it is rendered here. Blocking a zero-field save in Zod is section 5.5 rule A:
              a user who composes nine fields, deletes them all by mistake and learns from a server
              banner has lost the work. */}
          {form.formState.errors.fields?.message !== undefined && (
            <Alert severity="error" sx={{ mb: 2 }}>
              {form.formState.errors.fields.message}
            </Alert>
          )}

          <Stack spacing={2}>
            {fieldArray.fields.map((row, index) => (
              <FieldDescriptorEditor
                key={row.id}
                fieldIndex={index}
                fieldCount={fieldArray.fields.length}
                onMoveUp={() => {
                  fieldArray.move(index, index - 1);
                  renumberDisplayOrder();
                }}
                onMoveDown={() => {
                  fieldArray.move(index, index + 1);
                  renumberDisplayOrder();
                }}
                onRemove={() => {
                  fieldArray.remove(index);
                  renumberDisplayOrder();
                }}
              />
            ))}
          </Stack>

          <Button
            startIcon={<AddIcon />}
            onClick={() => {
              fieldArray.append(blankField(fieldArray.fields.length));
            }}
            sx={{ mt: 2 }}
          >
            Add field
          </Button>

          <Divider sx={{ my: 3 }} />

          {/* A 422 IS A FORM BANNER, VERBATIM, ABOVE SAVE, AND IS NEVER MAPPED ONTO A FIELD
              (section 7.3): the messages do name the field key -- "Duplicate field key 'x'." -- and
              matching that string to highlight a row is exactly the heuristic that forbids. Reaching
              one of these at all means a Zod rule is missing; the banner is not the fix. The 409s land
              here too, verbatim, with the reload affordance ErrorBanner adds. */}
          <ErrorBanner error={create.error ?? edit.error ?? preflightError} />

          <Stack direction="row" spacing={1} sx={{ alignItems: 'center' }}>
            {/* NO SUBMIT BUTTON AT ALL while a historical version or a stale conflict stands -- not a
                disabled one, because a disabled Save invites a hunt for the state that enables it. */}
            {!isBlocked && (
              <Button type="submit" variant="contained" disabled={isPending}>
                {isEdit ? 'Save new version' : 'Create ticket type'}
              </Button>
            )}
            <Button
              onClick={() => {
                navigate(isEdit ? `/ticket-types/${id}` : '/ticket-types');
              }}
              disabled={isPending}
            >
              Cancel
            </Button>
            {isEdit && !isBlocked && (
              <Typography variant="body2" color="text.secondary">
                {/* Said next to the button, because it is not what "Save" usually means. */}
                Saving replaces the whole field list and creates version{' '}
                {String((loadedVersionRef.current ?? 0) + 1)}.
              </Typography>
            )}
          </Stack>
        </Box>
      </FormProvider>

      <Snackbar
        open={snackbar !== null}
        message={snackbar ?? ''}
        autoHideDuration={6000}
        onClose={() => setSnackbar(null)}
      />
    </>
  );
}
