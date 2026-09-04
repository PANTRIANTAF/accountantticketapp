import { useEffect, useState } from 'react';
import { Controller, useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import Box from '@mui/material/Box';
import Button from '@mui/material/Button';
import Chip from '@mui/material/Chip';
import Collapse from '@mui/material/Collapse';
import MenuItem from '@mui/material/MenuItem';
import Paper from '@mui/material/Paper';
import Stack from '@mui/material/Stack';
import TextField from '@mui/material/TextField';
import Typography from '@mui/material/Typography';
import ExpandLessIcon from '@mui/icons-material/ExpandLess';
import ExpandMoreIcon from '@mui/icons-material/ExpandMore';
import { AdapterDateFns } from '@mui/x-date-pickers/AdapterDateFns';
import { DateTimePicker } from '@mui/x-date-pickers/DateTimePicker';
import { LocalizationProvider } from '@mui/x-date-pickers/LocalizationProvider';
import { useAuditActionCodes } from '../queries';
import type { AuditSearchRequest } from '../types';
import {
  activeFilters,
  auditFilterSchema,
  emptyAuditSearchRequest,
  toAuditSearchParams,
  toFilterValues,
  type AuditFilterField,
  type AuditFilterValues,
} from '../screens/auditFilterSchema';

/**
 * The eight filters, plus *Clear all* and *Search*. AuditScreens.md section 3.2.
 *
 * DRAFT FILTERS ARE REACT STATE; APPLIED FILTERS ARE THE QUERY KEY (section 3.2 rule D). Nothing
 * fetches until *Search*: keying off the draft fires a POST against the largest table in the
 * database on every keystroke. The draft lives in this form; the applied set lives in the URL and is
 * handed back down as `applied`, which is also what re-seeds the form when the reader presses Back.
 *
 * PAGING IS NOT HERE. It belongs to the pager. pageNumber and pageSize travel through the form
 * because they are part of the validated filter set, but no control renders them, and applying or
 * clearing any filter resets pageNumber to 1 (section 3.2 rule F) -- done by the screen.
 *
 * THE LocalizationProvider IS LOCAL TO THIS PANEL, and it is the only place in this slice that needs
 * one. Phase 0's main.tsx provider stack has none, and this plan creates and edits nothing outside
 * slices/audit/, so the two pickers are wrapped here rather than app-wide. Reported: an app-level
 * LocalizationProvider in main.tsx is the right home once a second slice needs a date control.
 */
export function AuditFilterPanel({
  applied,
  onApply,
  onClearAll,
  onRemoveFilter,
}: {
  /** The APPLIED set, from the URL. Seeds the draft and drives the chips. */
  applied: AuditSearchRequest;
  onApply: (values: AuditFilterValues) => void;
  onClearAll: () => void;
  onRemoveFilter: (field: AuditFilterField) => void;
}) {
  const [expanded, setExpanded] = useState(true);
  const codes = useAuditActionCodes();

  const {
    control,
    register,
    handleSubmit,
    reset,
    setValue,
    watch,
    formState: { errors },
  } = useForm<AuditFilterValues>({
    resolver: zodResolver(auditFilterSchema),
    // section 9.3 rule A. Not onChange: "not a date" after the first keystroke of a GUID.
    mode: 'onBlur',
    defaultValues: toFilterValues(applied),
  });

  // Re-seed the draft when the APPLIED set changes -- a Back navigation, a shared link, a removed
  // chip. Keyed on the serialised params rather than on `applied`, which is a fresh object on every
  // render and would reset the form (and the reader's half-typed value) on each one.
  const appliedKey = toAuditSearchParams(applied).toString();
  useEffect(() => {
    reset(toFilterValues(applied));
    // eslint-disable-next-line react-hooks/exhaustive-deps -- see above: `applied` is unstable.
  }, [appliedKey, reset]);

  const chips = activeFilters(applied);
  const targetKind = watch('targetKind');

  // Three states, and NEVER AN EMPTY SELECT (section 5 rule C): an empty dropdown makes the whole
  // search unusable and reads as "there are no actions".
  const catalogue: CatalogueState = codes.data !== undefined
    ? 'ready'
    : codes.isLoading
      ? 'loading'
      : 'failed';

  return (
    <Paper variant="outlined" sx={{ p: 2, mb: 3 }}>
      <Stack
        direction={{ xs: 'column', sm: 'row' }}
        spacing={1}
        sx={{ alignItems: { sm: 'center' }, flexWrap: 'wrap' }}
      >
        <Button
          onClick={() => {
            setExpanded((open) => !open);
          }}
          startIcon={expanded ? <ExpandLessIcon /> : <ExpandMoreIcon />}
          aria-expanded={expanded}
          sx={{ flexShrink: 0 }}
        >
          Filters
        </Button>

        {/* COLLAPSED, THE PANEL STILL NAMES EVERY ACTIVE FILTER (section 3.2 rule C): a count plus
            one removable Chip each. A panel reading only "Filters" lets a reader take a filtered
            table for the whole log and conclude "this never happened" from rows that were merely
            excluded. The chips stay visible when expanded too -- they are the applied set, which is
            not the same thing as the values in the controls below. */}
        <Typography variant="body2" color="text.secondary" sx={{ flexShrink: 0 }}>
          {chips.length === 0 ? 'None active — the whole log' : `${String(chips.length)} active:`}
        </Typography>
        <Stack direction="row" spacing={1} sx={{ flexWrap: 'wrap', rowGap: 1 }}>
          {chips.map((chip) => (
            <Chip
              key={chip.field}
              size="small"
              label={`${chip.label}=${chip.value}`}
              onDelete={() => {
                onRemoveFilter(chip.field);
              }}
            />
          ))}
        </Stack>
      </Stack>

      <Collapse in={expanded} unmountOnExit>
        <LocalizationProvider dateAdapter={AdapterDateFns}>
          <Box
            component="form"
            noValidate
            onSubmit={handleSubmit(onApply)}
            sx={{ mt: 2 }}
          >
            <Box
              sx={{
                display: 'grid',
                gridTemplateColumns: { xs: '1fr', sm: 'repeat(2, 1fr)', lg: 'repeat(3, 1fr)' },
                gap: 2,
              }}
            >
              <TextField
                {...register('actorUserId')}
                label="Actor user id"
                fullWidth
                error={errors.actorUserId !== undefined}
                helperText={errors.actorUserId?.message ?? 'Exact match. A partial id matches nothing.'}
              />

              <Controller
                name="action"
                control={control}
                render={({ field }) => (
                  <CatalogueField
                    label="Action"
                    state={catalogue}
                    options={codes.data?.actions ?? []}
                    value={field.value}
                    onChange={field.onChange}
                    onBlur={field.onBlur}
                  />
                )}
              />

              <Controller
                name="outcome"
                control={control}
                render={({ field }) => (
                  <CatalogueField
                    label="Outcome"
                    state={catalogue}
                    options={codes.data?.outcomes ?? []}
                    value={field.value}
                    onChange={field.onChange}
                    onBlur={field.onBlur}
                  />
                )}
              />

              <Controller
                name="targetKind"
                control={control}
                render={({ field }) => (
                  <CatalogueField
                    label="Target kind"
                    state={catalogue}
                    options={codes.data?.targetKinds ?? []}
                    value={field.value}
                    onChange={(value) => {
                      field.onChange(value);
                      // RULE B, second half: clearing the kind clears the id, so the pair can never
                      // reach the server as the 422 at SearchAuditLogHandler.cs:92.
                      if (value.trim() === '') setValue('targetId', '');
                    }}
                    onBlur={field.onBlur}
                  />
                )}
              />

              {/* RULE B, first half: a target id is only meaningful alongside its kind, so the
                  control is DISABLED until one is chosen. The server's 422 for the pair is then
                  structurally unreachable rather than merely validated against. */}
              <TextField
                {...register('targetId')}
                label="Target id"
                fullWidth
                disabled={targetKind.trim() === ''}
                error={errors.targetId !== undefined}
                helperText={errors.targetId?.message ?? 'Needs a target kind.'}
              />

              <TextField
                {...register('customerId')}
                label="Customer id"
                fullWidth
                error={errors.customerId !== undefined}
                helperText={errors.customerId?.message ?? 'A full customer id.'}
              />

              <Controller
                name="from"
                control={control}
                render={({ field, fieldState }) => (
                  <DateTimePicker
                    label="From"
                    value={field.value}
                    onChange={(value) => {
                      field.onChange(value);
                    }}
                    slotProps={{
                      textField: {
                        fullWidth: true,
                        onBlur: field.onBlur,
                        error: fieldState.error !== undefined,
                        helperText: fieldState.error?.message ?? 'Inclusive.',
                      },
                    }}
                  />
                )}
              />

              <Controller
                name="to"
                control={control}
                render={({ field, fieldState }) => (
                  <DateTimePicker
                    label="To"
                    value={field.value}
                    onChange={(value) => {
                      field.onChange(value);
                    }}
                    slotProps={{
                      textField: {
                        fullWidth: true,
                        onBlur: field.onBlur,
                        error: fieldState.error !== undefined,
                        helperText: fieldState.error?.message ?? 'Inclusive.',
                      },
                    }}
                  />
                )}
              />
            </Box>

            <Stack direction="row" spacing={2} sx={{ mt: 2, justifyContent: 'flex-end' }}>
              <Button
                type="button"
                onClick={() => {
                  // The reader's page size survives a clear: it is not a filter.
                  reset(toFilterValues({ ...emptyAuditSearchRequest(), pageSize: applied.pageSize }));
                  onClearAll();
                }}
              >
                Clear all
              </Button>
              {/* Never disabled because the form is invalid (section 9.3 rule B): let submit run and
                  let the resolver put the message next to the picker. There is no mutation here to
                  be pending on -- a search is a query. */}
              <Button type="submit" variant="contained">
                Search
              </Button>
            </Stack>
          </Box>
        </LocalizationProvider>
      </Collapse>
    </Paper>
  );
}

type CatalogueState = 'ready' | 'loading' | 'failed';

/**
 * One of the three server-populated filters. Never hardcode the options (section 5 rule A): the
 * server adds an action code in the same commit as the feature that emits it, so a copy silently
 * lacks the newest codes -- exactly the ones being investigated, because the newest feature is the
 * one that just misbehaved. The omission has no symptom: the dropdown simply lacks the value.
 *
 * IF /api/audit/action-codes FAILED, THIS DEGRADES TO A FREE-TEXT FIELD (section 5 rule C) with
 * helper text saying an unrecognised value is rejected. An Admin who knows the code can still
 * search; an empty Select would leave no way to filter at all and read as "there are no actions".
 */
function CatalogueField({
  label,
  state,
  options,
  value,
  onChange,
  onBlur,
}: {
  label: string;
  state: CatalogueState;
  options: string[];
  value: string;
  onChange: (value: string) => void;
  onBlur: () => void;
}) {
  if (state !== 'ready') {
    return (
      <TextField
        label={label}
        fullWidth
        value={value}
        disabled={state === 'loading'}
        onChange={(event) => {
          onChange(event.target.value);
        }}
        onBlur={onBlur}
        helperText={
          state === 'loading'
            ? 'Loading the catalogue…'
            : 'The catalogue could not be loaded. Type the exact code; an unrecognised value is rejected.'
        }
      />
    );
  }

  return (
    <TextField
      select
      label={label}
      fullWidth
      value={value}
      onChange={(event) => {
        onChange(event.target.value);
      }}
      onBlur={onBlur}
      helperText="Any, unless set."
    >
      <MenuItem value="">
        <em>Any</em>
      </MenuItem>
      {options.map((option) => (
        <MenuItem key={option} value={option}>
          {option}
        </MenuItem>
      ))}
    </TextField>
  );
}
