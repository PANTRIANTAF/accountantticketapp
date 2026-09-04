import * as z from 'zod';

/**
 * The four Zod schemas of this slice, mirrored field by field from
 * Slices/Customers/Application/CustomerValidation.cs and
 * Slices/Employees/Application/EmployeeValidation.cs.
 *
 *   customerBlockSchema    the Company block of /customers/new  -- CustomerValidation.cs:8-19, 34-43
 *   firstAdminBlockSchema  the First Customer Admin block       -- EmployeeValidation.cs:86-104, 138-148
 *   legalSchema            EditCustomerLegalDialog              -- CustomerValidation.cs:24-30
 *   contactSchema          EditCustomerContactDialog            -- CustomerValidation.cs:45-54
 *
 * Four exports and no fifth. The onboarding screen composes the first two into
 * `z.object({ customer, firstAdmin })` itself, because the nesting is a property of that one request
 * body (EmployeeWriteDtos.cs:139-143) and not of either block.
 *
 * A. MIRROR THE SERVER EXACTLY -- NEITHER STRICTER NOR LOOSER. Stricter blocks input the API accepts;
 *    looser produces a 422 banner with nothing tying it to an input, because ProblemDetails here
 *    carries no errors{} dictionary (AppExceptionMiddleware.cs:53-58). Every message below is the
 *    server's own message, character for character, so a rule that slips through the client and comes
 *    back as a banner reads identically to the one that did not.
 * B. THE EMAIL RULE IS DELIBERATELY WEAK. CustomerValidation.cs:56-62 and EmployeeValidation.cs:94-96
 *    check for the presence of '@' and nothing else. z.string().email() -- or z.email() in Zod 4 --
 *    rejects addresses the API accepts, and the user has no way to discover which rule was ours.
 * C. TRIMMED, AND `null` FOR AN UNTOUCHED OPTIONAL (section 9.3 rules E and F). Both happen inside
 *    the schema: .trim() runs during parsing, and React Hook Form hands the submit handler the
 *    resolver's PARSED output, so a screen cannot forget to trim. Optional fields transform '' to
 *    null, because CustomerValidation.cs:74-82 maps an empty optional to null and '' is a value that
 *    can pass a nullability check and fail a length one.
 * D. TWO DATE FIELDS, TWO DIFFERENT CEILINGS. onboardedOn is +1 day
 *    (CustomerValidation.cs:17); employmentStartDate is +1 YEAR (EmployeeValidation.cs:26, 143).
 *    Both are DateOnly strings compared as strings -- never through a parsed Date, which shifts the
 *    boundary a day west of UTC (GeneralUIArchitecture.md section 10.2).
 * E. legalSchema AND contactSchema ARE SEPARATE SCHEMAS OVER DISJOINT FIELD SETS, and that is a
 *    permission boundary, not a convenience: CustomersActionCatalogue.cs:17 grants EditCustomerLegal
 *    to AA and AU only, while :18-19 grants EditCustomerContact to AA, AU and CA. There is no
 *    combined "edit customer" schema, because there is no combined endpoint and no role that would
 *    be allowed to post one.
 *
 * NOT MIRRORED, DELIBERATELY: the uniqueness of taxNumber. It is a 409 from the database
 * (UpdateCustomerLegalHandler.cs:48-50, 62-65), not a shape the client can know, and it renders as a
 * form banner.
 */

/**
 * A required string, at the server's own two messages. The server trims BEFORE measuring
 * (CustomerValidation.cs:66-71), so trimming here cannot turn an accepted value into a rejected one.
 */
const requiredText = (maximumLength: number, name: string) =>
  z
    .string()
    .trim()
    .min(1, `${name} is required.`)
    .max(maximumLength, `${name} must be at most ${String(maximumLength)} characters.`);

/**
 * An optional string. NOT `.optional()`: every field is registered on a React Hook Form input that
 * always supplies a string, so the INPUT type stays uniformly `string` (which is what defaultValues
 * needs) while the OUTPUT type becomes `string | null` (which is what the DTO needs). The transform
 * is the whole reason a screen never writes `|| null` at a call site and never sends ''.
 */
const optionalText = (maximumLength: number, name: string) =>
  z
    .string()
    .trim()
    .max(maximumLength, `${name} must be at most ${String(maximumLength)} characters.`)
    .transform((value): string | null => (value === '' ? null : value));

/**
 * A required email, at the server's THREE messages in the server's order: required, then length,
 * then the '@' check (CustomerValidation.cs:56-62). `name` differs between the two blocks --
 * "Contact email" on the Company block, "Work email" on the person -- and so do the server's
 * messages, so it is a parameter rather than a hard-coded string.
 */
const requiredEmail = (name: string) =>
  requiredText(320, name).includes('@', `${name} must contain '@'.`);

/**
 * Today in UTC, plus a number of days, as a DateOnly string.
 *
 * UTC, because the server's ceiling is DateOnly.FromDateTime(DateTime.UtcNow)
 * (CustomerValidation.cs:17). Built through Date.UTC arithmetic and sliced out of the ISO string, so
 * no local-timezone offset ever enters the comparison; the VALUE being validated is never parsed at
 * all, it is compared as text.
 */
const utcDatePlusDays = (days: number): string => {
  const now = new Date();
  const shifted = new Date(
    Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), now.getUTCDate() + days),
  );
  return shifted.toISOString().slice(0, 10);
};

/**
 * Today in UTC, plus whole years, as a DateOnly string -- the EmployeeValidation.cs:143 ceiling.
 *
 * One known divergence, and it is one day wide once every four years: C#'s DateOnly.AddYears clamps
 * 29 February to 28 February, while Date.UTC rolls it forward to 1 March. On that one input the
 * client is a day LOOSER than the server, so the value is refused by a 422 banner carrying the
 * server's own sentence rather than blocked client-side. Being looser is the correct side to err on
 * (rule A): a stricter client would reject a date the API accepts, with no way for the user to
 * discover whose rule refused it.
 */
const utcDatePlusYears = (years: number): string => {
  const now = new Date();
  const shifted = new Date(
    Date.UTC(now.getUTCFullYear() + years, now.getUTCMonth(), now.getUTCDate()),
  );
  return shifted.toISOString().slice(0, 10);
};

/**
 * The Company block of /customers/new -- the twelve fields of `CreateCustomer`
 * (ICustomerApi.cs:18-32), validated by CustomerValidation.cs:8-19 and :34-43.
 *
 * EVERY LABEL THIS SCHEMA'S MESSAGES NAME IS A COMPANY'S, because a Customer is a company and never
 * a natural person (00-Glossary.md; section 12.1 rule B). "Legal name", not "First name". The one
 * natural person in this slice is in firstAdminBlockSchema below.
 */
export const customerBlockSchema = z.object({
  legalName: requiredText(300, 'Legal name'),
  tradingName: optionalText(300, 'Trading name'),
  taxNumber: requiredText(50, 'Tax number'),
  taxOffice: optionalText(200, 'Tax office'),
  addressLine1: requiredText(200, 'Address line 1'),
  addressLine2: optionalText(200, 'Address line 2'),
  addressCity: requiredText(100, 'Address city'),
  addressPostalCode: requiredText(20, 'Address postal code'),
  addressCountry: requiredText(100, 'Address country'),
  contactEmail: requiredEmail('Contact email'),
  contactPhone: requiredText(40, 'Contact phone'),

  // DateOnly on the wire: "2026-09-02", no time part and no timezone. z.iso.date() checks that
  // shape; z.string().date() is the Zod 3 spelling and is deprecated in the installed Zod 4.
  //
  // The empty string is the only failure a native date input can produce, so the format message is
  // the server's required message (CustomerValidation.cs:16).
  onboardedOn: z.iso
    .date('Onboarded date is required.')
    .refine(
      (value) => value <= utcDatePlusDays(1),
      'Onboarded date cannot be more than one day in the future.',
    ),
});

/**
 * The First Customer Admin block of /customers/new -- OnboardFirstAdminDto
 * (EmployeeWriteDtos.cs:156-166), validated by EmployeeValidation.cs:86-104.
 *
 * THIS IS THE ONE PLACE IN THE SLICE WHERE PERSON-SHAPED LABELS ARE CORRECT (section 12.1 rule C),
 * because these fields belong to an EMPLOYEE -- a different entity, in a different slice, reached
 * through /api/customers/onboard only.
 *
 * TWO DIFFERENCES FROM EMPLOYEE REGISTRATION, both from EmployeeValidation.cs:92-96:
 *
 *   workEmail is REQUIRED here (it is optional on RegisterEmployeeRequestDto), because this
 *   operation always invites and an invitation needs somewhere to go; and
 *   its '@' message says "Work email", not "Contact email".
 *
 * AND NO `role` FIELD. The handler chooses CustomerAdmin (OnboardCustomerHandler.cs:104-114); it is
 * not a request field and there is no selector for it. Creating the first person as a plain Employee
 * would put the Customer in violation of its own at-least-one-active-Customer-Admin invariant from
 * the moment it exists.
 */
export const firstAdminBlockSchema = z.object({
  givenName: requiredText(100, 'Given name'),
  familyName: requiredText(100, 'Family name'),
  jobTitle: optionalText(200, 'Job title'),
  workEmail: requiredEmail('Work email'),
  contactPhone: optionalText(50, 'Contact phone'),
  taxIdentificationNumber: optionalText(50, 'Tax identification number'),
  socialSecurityNumber: optionalText(50, 'Social security number'),

  // +1 YEAR, not +1 day. EmployeeValidation.cs:26 calls the threshold a typo guard against a
  // mistyped year, and :145-146 formats the message with the constant, so the "1 year(s)" wording --
  // parenthesised plural and all -- is the server's, reproduced exactly.
  employmentStartDate: z.iso
    .date('Employment start date is required.')
    .refine(
      (value) => value <= utcDatePlusYears(1),
      'Employment start date cannot be more than 1 year(s) in the future.',
    ),
});

/**
 * EditCustomerLegalDialog -- UpdateCustomerLegalRequestDto.cs:5-9 minus customerId, validated by
 * CustomerValidation.cs:24-30. AA and AU only.
 *
 * `onboardedOn` IS NOT HERE AND MUST NOT BE ADDED. No endpoint changes it
 * (UpdateCustomerLegalRequestDto.cs:5-9), so an input for it would post a value nothing reads. The
 * plan's section 15 asks whether that immutability is intended.
 */
export const legalSchema = z.object({
  legalName: requiredText(300, 'Legal name'),
  tradingName: optionalText(300, 'Trading name'),
  taxNumber: requiredText(50, 'Tax number'),
  taxOffice: optionalText(200, 'Tax office'),
});

/**
 * EditCustomerContactDialog -- UpdateCustomerContactRequestDto.cs:5-12 minus customerId, validated
 * by CustomerValidation.cs:45-54. AA, AU and CA, which is why the same dialog serves
 * /customers/:customerId and /my-customer.
 *
 * DISJOINT FROM legalSchema, EXACTLY. Both endpoints are full replacements
 * (UpdateCustomerContactHandler.cs:47-53, UpdateCustomerLegalHandler.cs:53-56), so a field in the
 * wrong schema is either silently reverted -- it is absent from the DTO that dialog posts -- or a
 * 403.
 */
export const contactSchema = z.object({
  addressLine1: requiredText(200, 'Address line 1'),
  addressLine2: optionalText(200, 'Address line 2'),
  addressCity: requiredText(100, 'Address city'),
  addressPostalCode: requiredText(20, 'Address postal code'),
  addressCountry: requiredText(100, 'Address country'),
  contactEmail: requiredEmail('Contact email'),
  contactPhone: requiredText(40, 'Contact phone'),
});
