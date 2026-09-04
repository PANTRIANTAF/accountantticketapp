/**
 * The ONLY module converting between wire timestamps and displayed text.
 * GeneralUIArchitecture.md section 10.2. Three wire shapes:
 *
 *   C# DateOnly       -> "2026-09-02"                    plain date, no timezone
 *   C# DateTime       -> "2026-09-02T14:33:12.4Z" OR with NO suffix -- UTC either way
 *   C# DateTimeOffset -> "2026-09-02T14:33:12.4+00:00"   has an offset; parse directly
 *
 * One deployment serves one office, so there is no timezone label -- but a timestamp silently
 * eight hours out is a real support call, so the conversion happens here and nowhere else.
 */

const DATE_ONLY = /^\d{4}-\d{2}-\d{2}$/;

// Matches a trailing Z or a +HH:MM / -HH:MM offset.
const HAS_OFFSET = /(?:Z|[+-]\d{2}:?\d{2})$/i;

/**
 * Parses a C# DateTime or DateTimeOffset. A bare value -- no `Z`, no offset -- is UTC and gets a
 * `Z` appended, because JavaScript would otherwise parse it as LOCAL time and shift it silently.
 * Returns null for an empty or unparseable value rather than an Invalid Date.
 */
export function parseUtc(value: string | null | undefined): Date | null {
  if (!value) return null;
  const normalized = HAS_OFFSET.test(value) ? value : `${value}Z`;
  const parsed = new Date(normalized);
  return Number.isNaN(parsed.getTime()) ? null : parsed;
}

/**
 * Formats a C# DateOnly ("2026-09-02") as a plain date.
 *
 * It does NOT build a Date and format in local time. `new Date("2026-09-02")` is parsed as
 * midnight UTC, so west of UTC toLocaleDateString renders 1 September -- and it is correct on an
 * authoring machine east of UTC, which is how the bug ships. The parts are formatted directly.
 *
 * Also tolerates a full timestamp, because two C# properties spelled like dates are DateTime.
 */
export function formatDate(value: string | null | undefined): string {
  if (!value) return '';

  const datePart = DATE_ONLY.test(value) ? value : value.slice(0, 10);
  if (!DATE_ONLY.test(datePart)) return value;

  const [year, month, day] = datePart.split('-');
  if (year === undefined || month === undefined || day === undefined) return value;

  // Constructed with explicit local Y/M/D so no timezone shift is possible, then formatted with
  // Intl so the order matches the reader's locale.
  const local = new Date(Number(year), Number(month) - 1, Number(day));
  return dateFormatter.format(local);
}

/** Formats a C# DateTime or DateTimeOffset in the browser's local timezone. */
export function formatDateTime(value: string | null | undefined): string {
  const parsed = parseUtc(value);
  return parsed === null ? '' : dateTimeFormatter.format(parsed);
}

const dateFormatter = new Intl.DateTimeFormat(undefined, {
  year: 'numeric',
  month: 'short',
  day: '2-digit',
});

const dateTimeFormatter = new Intl.DateTimeFormat(undefined, {
  year: 'numeric',
  month: 'short',
  day: '2-digit',
  hour: '2-digit',
  minute: '2-digit',
});
