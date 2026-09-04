/**
 * The eleven field data types, in the order they are declared in
 * AccountantApp.Api/Slices/TicketTypes/ExternalInterfaces/FieldDataTypes.cs:28-38.
 *
 * ONE LIST, imported by both the editor's data-type Select and the registry's completeness check.
 * Two copies drift, and the drift shows up as a data type an author can pick and the renderer
 * cannot draw.
 *
 * THESE ARE STORED STRING VALUES, not a display vocabulary. They are persisted in
 * field_descriptors.data_type and named in that table's ck_* CHECK constraint, so the comparison is
 * ORDINAL and case-sensitive server-side (FieldDataTypes.All is built with StringComparer.Ordinal,
 * :49) -- "yesno" would write a row the constraint rejects. Never lower-case one, and never render
 * one raw outside the editor's Select: DATA_TYPE_LABELS is the display form.
 */

export const FIELD_DATA_TYPES = [
  'SingleLineText',
  'MultiLineText',
  'WholeNumber',
  'DecimalNumber',
  'MoneyAmount',
  'Date',
  'DateRange',
  'YesNo',
  'SingleChoice',
  'MultipleChoice',
  'FileUpload',
] as const;

export type FieldDataType = (typeof FIELD_DATA_TYPES)[number];

/** The two types that carry choiceOptions. TicketTypeMapper.cs:180 uses exactly this pair. */
export const CHOICE_DATA_TYPES: readonly FieldDataType[] = ['SingleChoice', 'MultipleChoice'];

export function isChoiceDataType(dataType: string): boolean {
  return CHOICE_DATA_TYPES.includes(dataType as FieldDataType);
}

export function isKnownDataType(dataType: string): dataType is FieldDataType {
  return (FIELD_DATA_TYPES as readonly string[]).includes(dataType);
}

/**
 * Display labels. Success criterion 22 forbids rendering a raw dataType string anywhere except the
 * editor's Select, and "SingleLineText" is a wire value rather than English.
 */
export const DATA_TYPE_LABELS: Record<FieldDataType, string> = {
  SingleLineText: 'Single-line text',
  MultiLineText: 'Multi-line text',
  WholeNumber: 'Whole number',
  DecimalNumber: 'Decimal number',
  MoneyAmount: 'Money amount',
  Date: 'Date',
  DateRange: 'Date range',
  YesNo: 'Yes / No',
  SingleChoice: 'Single choice',
  MultipleChoice: 'Multiple choice',
  FileUpload: 'File upload',
};

/** The label for a dataType that may not be one of the eleven -- an older or newer server's value. */
export function dataTypeLabel(dataType: string): string {
  return isKnownDataType(dataType) ? DATA_TYPE_LABELS[dataType] : dataType;
}
