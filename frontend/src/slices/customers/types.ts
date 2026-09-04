/**
 * Hand-written interfaces mirroring the C# DTOs of AccountantApp.Api/Slices/Customers, camelCase,
 * Guid -> string, each commented with the file it mirrors so the next reader can diff them
 * (GeneralUIArchitecture.md section 2.5).
 *
 * Eight exports: CustomerStatus, CustomerSummary, Customer, CustomerSelf, ListCustomersRequest,
 * UpdateCustomerContactRequest, UpdateCustomerLegalRequest, SetCustomerStatusRequest.
 *
 * NO `role` ANYWHERE IN THIS FILE. Nothing in this slice sends or renders a role: the CustomerAdmin
 * role the onboarding handler assigns is chosen server-side and never crosses the wire
 * (OnboardCustomerHandler.cs:110-114). The integer-vs-string trap of GeneralUIArchitecture.md
 * section 10.1 still binds every `status` field here, but there is no integer in this slice to get
 * wrong.
 *
 * NO CreateCustomerRequest. POST /api/customers/create makes a Customer with no Employee and no
 * account, which 02-AuthorizationMatrix.md section 3 calls useless; no screen calls it and api.ts
 * does not wrap it. See api.ts rule A.
 *
 * NO union with the Employees slice's onboarding types. OnboardCustomerRequest and
 * OnboardCustomerResponse live in slices/employees/, because /api/customers/onboard is registered
 * from EmployeesEndpoints.cs:227 (03-SliceInventory.md section 1).
 */

/**
 * Mirrors Slices/Customers/Core/Customer.cs:27-31 -- exactly two values. NEVER add 'Invited'; that
 * is a UserAccount status (01-DomainModel.md section 2) and the database rejects it
 * (Infrastructure/Migrations/20260901_002_AddCustomerStatusCheck.sql:10-11 adds
 * CHECK (status IN ('Active','Suspended'))).
 *
 * RE-EXPORTED, NOT REDECLARED. Phase 0 already declares this union in shared/format/enums.ts, where
 * it sits beside the three other status vocabularies and is what StatusChip's StatusWord is built
 * from. The plan asks types.ts for eight exports including this one; a second declaration of the
 * same union is exactly the drift that section 1.4 rule A's SessionDto note exists to prevent, so
 * this file re-exports the one declaration -- the same resolution slices/identity/types.ts uses for
 * SessionDto.
 */
export type { CustomerStatus } from '../../shared/format/enums';

import type { CustomerStatus } from '../../shared/format/enums';

/**
 * Mirrors Application/Dtos/CustomerSummaryDto.cs:5-8 -- FOUR keys, and there is no fifth to add.
 * CustomerMapper.ToSummaryExpression (CustomerMapper.cs:9-16) projects exactly these.
 *
 * Contact email, city, employee count, ticket count and onboarded date are NOT in it. Resolving one
 * per row is fifteen extra requests per page.
 */
export interface CustomerSummary {
  id: string;
  legalName: string;
  tradingName: string | null;
  status: CustomerStatus;
}

/**
 * Mirrors Application/Dtos/CustomerDto.cs:5-20 -- all SIXTEEN keys, in declaration order.
 * Returned by /detail, /update-contact, /update-legal, /suspend and /reactivate.
 *
 * `onboardedOn` is a C# DateOnly: "2026-09-02", no timezone, no time part. `createdAt` and
 * `updatedAt` are DateTimeOffsets: the offset is present and they parse directly. Three date fields,
 * two wire formats -- GeneralUIArchitecture.md section 10.2, and all three go through
 * shared/format/dates.ts.
 */
export interface Customer {
  id: string;
  legalName: string;
  tradingName: string | null;
  taxNumber: string;
  taxOffice: string | null;
  addressLine1: string;
  addressLine2: string | null;
  addressCity: string;
  addressPostalCode: string;
  addressCountry: string;
  contactEmail: string;
  contactPhone: string;
  status: CustomerStatus;
  /** DateOnly. Render with formatDate; never through new Date(). */
  onboardedOn: string;
  /** DateTimeOffset. */
  createdAt: string;
  /** DateTimeOffset. */
  updatedAt: string;
}

/**
 * Mirrors Application/Dtos/CustomerSelfDto.cs:5-16 -- ELEVEN keys. Returned by /api/customers/own.
 *
 * NARROWER THAN `Customer` ON THE SERVER, not merely unrendered. CustomerMapper.ToSelfDto
 * (CustomerMapper.cs:38-51) omits five fields ToDto includes -- taxNumber, taxOffice, onboardedOn,
 * createdAt, updatedAt -- which is what 02-AuthorizationMatrix.md:311 demands: "absent from the API
 * response, not merely unrendered." There is nothing here for the UI to hide.
 *
 * A SEPARATE INTERFACE, NOT Partial<Customer> OR Omit<Customer, ...>. A derived type invites a
 * component that accepts either and reads `taxNumber` off whichever it got. Two names for two shapes
 * is the whole point of queries.ts's rule about never seeding ['customers','own'].
 *
 * And NO OPTIONAL taxNumber to "make one component serve both screens". Adding the key is how a
 * field a screen is specified not to show becomes `undefined` rendered as a blank row rather than an
 * absent one.
 */
export interface CustomerSelf {
  id: string;
  legalName: string;
  tradingName: string | null;
  addressLine1: string;
  addressLine2: string | null;
  addressCity: string;
  addressPostalCode: string;
  addressCountry: string;
  contactEmail: string;
  contactPhone: string;
  status: CustomerStatus;
}

/**
 * Mirrors Application/Dtos/ListCustomersRequestDto.cs:5-8. The BODY of a POST read
 * (CustomersEndpoints.cs:30) -- pageNumber and pageSize are body fields here, not query parameters.
 *
 * ALL FOUR KEYS ARE REQUIRED IN THIS TYPE ON PURPOSE, so every call sends the whole body. The DTO is
 * a required minimal-API parameter (CustomersEndpoints.cs:31), so an absent body is a 400 about a
 * missing request body rather than a 200 with the DTO's defaults.
 *
 * `status: null` means "both". NEVER ''. ListCustomersHandler.cs:31-33 trims and then compares
 * case-sensitively against the two constants, so "" and "active" are both
 * 422 "Unknown customer status."
 */
export interface ListCustomersRequest {
  status: CustomerStatus | null;
  /** <= 200 characters, or 422 (ListCustomersHandler.cs:46-47). ILIKE over legalName OR tradingName. */
  search: string | null;
  pageNumber: number;
  pageSize: number;
}

/**
 * Mirrors Application/Dtos/UpdateCustomerContactRequestDto.cs:5-12 -- customerId plus SEVEN.
 *
 * A FULL REPLACEMENT, not a patch: UpdateCustomerContactHandler.cs:47-53 assigns every field
 * unconditionally. Send all eight keys always, including the unchanged ones.
 */
export interface UpdateCustomerContactRequest {
  customerId: string;
  addressLine1: string;
  addressLine2: string | null;
  addressCity: string;
  addressPostalCode: string;
  addressCountry: string;
  contactEmail: string;
  contactPhone: string;
}

/**
 * Mirrors Application/Dtos/UpdateCustomerLegalRequestDto.cs:5-9 -- customerId plus FOUR.
 *
 * Also a full replacement (UpdateCustomerLegalHandler.cs:53-56). `onboardedOn` is NOT here: no
 * endpoint changes it (see the plan's section 15 question about whether it is meant to be immutable).
 */
export interface UpdateCustomerLegalRequest {
  customerId: string;
  legalName: string;
  tradingName: string | null;
  taxNumber: string;
  taxOffice: string | null;
}

/**
 * Mirrors Application/Dtos/SetCustomerStatusRequestDto.cs:5-6. ONE DTO FOR BOTH /suspend AND
 * /reactivate, and `reason` is optional in both -- so neither dialog may make it required.
 *
 * `reason` is normalised to <= 500 characters (CustomerValidation.cs:32) and written into the After
 * payload of the audit entry only (SuspendCustomerHandler.cs:56-67). It is not on the customers row,
 * not in CustomerDto, and not visible on any screen.
 */
export interface SetCustomerStatusRequest {
  customerId: string;
  reason: string | null;
}
