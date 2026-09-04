/**
 * THE DYNAMIC FORM RENDERER'S CONTRACT. Screens/TicketTypesScreens.md section 6.2, transcribed
 * rather than paraphrased -- three of the comments below are the only record of a trap.
 *
 * WHY THIS LIVES IN shared/ AND NOT IN slices/ticketTypes/. slices/tickets/ is the renderer's real
 * consumer and does not exist yet (GeneralUIArchitecture.md section 0.1). Putting the renderer in
 * slices/ticketTypes/ would force slices/tickets/ to import a component from another slice, which
 * section 1.4 rule C forbids -- a slice may import only types.ts and api.ts from another slice. And
 * shared/ may never import from slices/ (rule A), so the dependency cannot be made legal in the
 * other direction either. slices/ticketTypes/types.ts RE-EXPORTS these three interfaces so the rest
 * of that slice has one import site.
 */

/** Mirrors Slices/TicketTypes/ExternalInterfaces/TicketTypeDetailDto.cs -> FieldDescriptorDetailDto. */
export interface FieldDescriptor {
  key: string;
  label: string;
  /** '' when absent. Never null: the C# property is a non-nullable string. */
  helpText: string;
  /** One of the eleven strings in ExternalInterfaces/FieldDataTypes.cs:28-38. Treated as unknown otherwise (6.3). */
  dataType: string;
  displayOrder: number;
  /** '' means "no group" -- the leading unnamed group (6.6). */
  groupName: string;
  isRequired: boolean;
  /**
   * The AUTHOR'S setting, echoed back. It is NOT a render instruction: the server has already
   * removed the fields a Customer-side caller may not see. See section 6.8 -- filtering on this
   * in the client is a defect.
   */
  isVisibleToCustomer: boolean;
  /** [] for every non-choice type. >= 2 entries for SingleChoice / MultipleChoice. */
  choiceOptions: ChoiceOption[];
  /** ALWAYS present -- the C# property is `= new()`, never null. Members are individually absent. */
  validation: FieldValidation;
  /** null when the author set no rule. Never a blank-fieldKey object. */
  conditionalVisibility: ConditionalVisibility | null;
}

export interface ChoiceOption { label: string; value: string; }

/** Mirrors FieldValidationDto. Every member is optional; '' and [] mean "no rule". */
export interface FieldValidation {
  minLength?: number | null;
  maxLength?: number | null;
  /** C# decimal -> JSON number. See GeneralUIArchitecture section 10.2. */
  minValue?: number | null;
  maxValue?: number | null;
  /** C# DateOnly -> "2026-09-02". No timezone. Never build a Date and format it locally. */
  earliestDate?: string | null;
  latestDate?: string | null;
  /** '' means no rule. A .NET-authored pattern that must be compiled in JS (6.4 rule 4). */
  regexPattern: string;
  /** [] means no rule. Split from a comma-separated column, already trimmed server-side. */
  allowedFileTypes: string[];
  maxFileSizeBytes?: number | null;
}

export interface ConditionalVisibility { fieldKey: string; value: string; }

export type DynamicFormMode = 'input' | 'preview' | 'read';

export interface DynamicFormProps {
  fields: FieldDescriptor[];
  mode: DynamicFormMode;
  /** Keyed by FieldDescriptor.key. Absent key = no value. */
  values?: Record<string, unknown>;
  /** Omitted in 'preview' and 'read'. Receives ONLY visible fields' values (6.5 trap 1). */
  onSubmit?: (values: Record<string, unknown>) => void;
  // Deliberately absent: role, session, ticketId, ticketTypeId, isAccountant. See 6.8.
}
