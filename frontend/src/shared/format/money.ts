/**
 * A C# MoneyAmount arrives as a JSON number from a `decimal`.
 *
 * There is NO currency field in the schema, so formatting is locale-decimal and not
 * currency-symbol until one exists (GeneralUIArchitecture.md section 10.2). Keep the raw number
 * for arithmetic, and never store a formatted string back into form state.
 *
 * Nothing in Phase 0 renders money. This file exists so the first slice that needs it does not
 * invent a second one.
 */

const moneyFormatter = new Intl.NumberFormat(undefined, {
  minimumFractionDigits: 2,
  maximumFractionDigits: 2,
});

export function formatMoney(value: number | null | undefined): string {
  if (value === null || value === undefined || Number.isNaN(value)) return '';
  return moneyFormatter.format(value);
}
