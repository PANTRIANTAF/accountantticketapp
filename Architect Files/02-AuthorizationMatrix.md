# Authorization Matrix

Normative. Where this document and any other disagree, this document wins.

Terms are as defined in [00-Glossary.md](00-Glossary.md).

Column headings below are abbreviated: **AA** = Accountant Admin, **AU** = Accountant User,
**CA** = Customer Admin, **EMP** = Employee.

---

## 1. The scoping rules

Every authorization decision is the conjunction of a **role check** and a **scope check**.
The role check asks "may this role do this at all"; the scope check asks "may they do it to
*this particular record*". Passing the role check alone is never sufficient.



| Role | Scope |
|---|---|
| Accountant Admin | All Customers, plus app administration |
| Accountant User | All Customers, ticket and configuration work only |
| Customer Admin | Their own Customer only. Full visibility within it. |
| Employee | Tickets where they are the Creator or the Subject. Their own Employee record. |

**The scope check is server-side and mandatory.** It applies on every read and every write,
including reads by identifier. A request for a record outside the caller's scope is denied
even when the caller knows the identifier.

**Denial responses:** when the caller lacks the role, respond `403`. When the record is
outside the caller's scope, respond **`404`, not `403`** — a `403` confirms the record
exists, which leaks information across the Customer boundary. Every denial writes an Audit
Entry.

### The only four differences between the two Accountant roles

Everywhere else in this document, AA and AU are identical. These are the exceptions:

| Reserved to Accountant Admin |
|---|
| Create a Customer |
| Suspend or reactivate a Customer |
| Create, invite, suspend, promote, or demote an Accountant account |
| Read the audit log |

A builder should express the common case as "any Accountant" and treat these four as named
exceptions — not scatter role checks through every handler.

---

## 2. Accountant accounts

| Action | AA | AU | CA | EMP |
|---|---|---|---|---|
| List Accountant accounts | Yes | Yes, names only, so tickets can be assigned | No | No |
| Invite a new Accountant, either role | **Yes, only** | No | No | No |
| Suspend / reactivate an Accountant | **Yes, only** | No | No | No |
| Promote an Accountant User to Admin | **Yes, only** | No | No | No |
| Demote an Accountant Admin to User | **Yes, only** | No | No | No |
| Delete an Accountant account | **Nobody.** Suspension only. |

Constraints:

- **At least one `Active` Accountant Admin must always exist.** Any operation leaving zero
  is rejected.
- **An Accountant Admin cannot suspend, demote, or delete their own account.** Self-action
  on one's own role or status is rejected.
- The first Accountant Admin is created by seeding. No endpoint creates it.
- An Accountant User can *see* the list of Accountants, because assigning a ticket requires
  knowing who exists. They cannot change anything about them. Return names and identifiers
  only — not email addresses, login history, or status detail.

## 3. Customers

| Action | AA | AU | CA | EMP |
|---|---|---|---|---|
| Create a Customer | **Yes, only** | No | No | No |
| Suspend / reactivate a Customer | **Yes, only** | No | No | No |
| List all Customers | Yes | Yes | No | No |
| View own Customer's details | Yes | Yes | Yes | Yes, read-only, limited fields |
| Edit Customer contact details | Yes | Yes | Yes, own Customer | No |
| Edit Customer legal name or tax number | Yes | Yes | No | No |
| Delete a Customer | **Nobody.** Customers are never deleted. |

Creating a Customer includes registering and inviting its first Customer Admin, in one
operation — a Customer with no way to log in is useless. There is no self-service signup.

**That composite operation is implemented in the `Employees` slice, not `Customers`.** `Customers`
may depend only on `Audit`, so it cannot create an Employee or a UserAccount; `Employees` already
depends on `Customers`, `Identity`, and `Notifications`. `Customers` owns a Customer-only create as
a building block. Both are `AccountantAdmin`-only — wrapping the operation does not turn it into an
`AccountantUser` power. See [03-SliceInventory.md](03-SliceInventory.md) section 1.

An Accountant User can read every Customer and edit contact details, because that is routine
work. They cannot bring a Customer into existence or take it out of service.

## 4. Employees

| Action | AA | AU | CA | EMP |
|---|---|---|---|---|
| Register an Employee | Yes, any Customer | Yes, any Customer | Yes, own Customer | No |
| List Employees | Yes, any | Yes, any | Yes, own Customer | No |
| View an Employee record | Yes, any | Yes, any | Yes, own Customer | Own record only |
| Edit an Employee record | Yes, any | Yes, any | Yes, own Customer | Own contact details only |
| Invite an Employee (create their account) | Yes, any | Yes, any | Yes, own Customer | No |
| Set an Employee's role to `CustomerAdmin` | Yes | Yes | Yes, own Customer | No |
| Mark an Employee `Departed` | Yes, any | Yes, any | Yes, own Customer | No |
| Reinstate a `Departed` Employee | Yes, any | Yes, any | Yes, own Customer | No |
| Change an Employee's login email | Yes, any | Yes, any | **No** | **No** |
| Suspend / reactivate an Employee's account | Yes, any | Yes, any | Yes, own Customer | No |
| Delete an Employee record | **Nobody.** |

Constraints:

- A Customer Admin cannot act on their own account's status or role — they cannot suspend
  themselves or remove their own `CustomerAdmin` role. This prevents a Customer locking
  itself out.
- **Reinstatement is a correction, not a re-hire.** It undoes a departure entered against the
  wrong record: the Employee returns to `Active` and their account is reactivated. Somebody who
  genuinely left and later returns is registered again as a new Employee record, because their two
  periods of employment are separate facts. Nothing can enforce that distinction — the audit entry
  records which one the caller made. Granted to exactly whoever may enter a departure, because a
  Customer Admin who can create a state they cannot undo turns every mistake into a support request.
- **Changing a login email is reserved to the Office, and nobody may change their own.** It is the
  one operation in this section a Customer Admin is refused. Whoever can move an account to a new
  address can move it to a mailbox they control; a Customer Admin doing it to a colleague is account
  takeover one step removed, and the colleague is the one who then cannot log in. Routing it through
  an Accountant puts a human outside the Customer in the loop and names them in the audit entry.
  Changing the **work** email is a different operation, on the Employee record, and stays under
  "Edit an Employee record".
- **A Customer must always retain at least one `Active` Customer Admin.** Any operation
  leaving zero is rejected. Only an Accountant can resolve such a situation.
- A Customer Admin can promote another Employee to `CustomerAdmin`, and can demote a
  different Customer Admin, subject to the rule above.
- **Registering and inviting are two separate operations.** The first creates an accountless
  Employee; the second gives them a login. A Customer Admin may do the first without ever
  doing the second.
- No Customer-side actor can create or modify an Accountant account. Employee role changes
  are restricted to `CustomerAdmin` and `Employee` — a request setting a role to either
  Accountant role is **rejected outright**, not silently ignored.

## 5. Ticket Types

| Action | AA | AU | CA | EMP |
|---|---|---|---|---|
| Create a Ticket Type | Yes | **Yes** | No | No |
| Edit a Ticket Type, creating a new version | Yes | **Yes** | No | No |
| Activate / deactivate a Ticket Type | Yes | **Yes** | No | No |
| List Ticket Types available to open | Yes | Yes | Yes, filtered | Yes, filtered |
| Read a version's field descriptors | Yes | Yes | Yes, filtered | Yes, filtered |
| Delete a Ticket Type or a version | **Nobody.** Existing tickets depend on them. |

**Ticket Type authoring is deliberately not Admin-only.** An Accountant User can change the
form catalogue, and the change applies to every Customer immediately. This was chosen
because form maintenance is routine accounting work. Do not restrict it to Accountant Admin,
and do not add an approval step.

"Filtered" means: only `Active` types, and only types whose audience permits the caller's
role. A type openable by Customer Admins only is **not returned by the API at all** to an
Employee — not greyed out in the UI.

Accountant-only Field Descriptors are stripped from responses to Customer-side callers, on
the server.

## 6. Tickets — read

| Action | AA | AU | CA | EMP |
|---|---|---|---|---|
| List tickets across all Customers | Yes | Yes | No | No |
| List unassigned tickets awaiting pickup | Yes | Yes | No | No |
| List tickets assigned to oneself | Yes | Yes | n/a | n/a |
| List tickets for own Customer | Yes | Yes | Yes, all of them | No |
| List own tickets | Yes | Yes | Yes | Yes, where Creator or Subject |
| View field values | Yes | Yes | Yes, own Customer | Yes, where Creator or Subject |
| View revision history | Yes | Yes | Yes, own Customer | Yes, where Creator or Subject |
| View verifications and rejection reasons | Yes | Yes | Yes, own Customer | Yes, where Creator or Subject |
| View conversation | Yes, all kinds | Yes, all kinds | Excluding Internal Notes | Excluding Internal Notes |
| View Internal Notes | Yes | Yes | **No** | **No** |
| View the Assignee | Yes | Yes | Yes, own Customer | Yes, own tickets |
| View another Employee's ticket in same Customer | Yes | Yes | Yes | **No** |
| View a `Draft` ticket | No | No | Only own drafts | Only own drafts |

The Customer Admin's full visibility within their Customer is a **deliberate, accepted
decision**, including tickets containing payroll and personal tax data. Do not add
confidentiality flags or narrow this without an explicit instruction.

Drafts are private to their Creator regardless of role. No Accountant ever sees drafts.

Internal Notes are visible to **both** Accountant roles. They are the Office's private
channel, not the Admin's.

## 7. Tickets — write

| Action | AA | AU | CA | EMP |
|---|---|---|---|---|
| Open a ticket about oneself | Yes | Yes | Yes | Yes |
| Open a ticket for an Employee (on-behalf-of) | Yes, any | Yes, any | Yes, own Customer | **No** |
| Save a draft | Yes | Yes | Yes | Yes |
| Submit a ticket | Creator only | Creator only | Creator, or any ticket of own Customer | Creator only |
| Submit a correction revision | Yes | Yes | Yes, own Customer | Yes, where Creator or Subject |
| Post a message | Yes | Yes | Yes, own Customer | Yes, where Creator or Subject |
| Post an Internal Note | Yes | Yes | No | No |
| **Pick up a ticket (assign to self, → `InReview`)** | Yes | Yes | No | No |
| **Assign a ticket to another Accountant** | Yes | Yes | No | No |
| **Reassign an already-assigned ticket** | Yes | Yes | No | No |
| Verify / reject a field value | Yes | Yes | No | No |
| Set priority or due date | Yes | Yes | No | No |
| Change status to `Answered` / `Closed` | Yes | Yes | No | No |
| Cancel a ticket | Yes | Yes | Yes, own Customer | Own drafts and own `Submitted` tickets |
| Reopen a `Closed` ticket | **Nobody.** `Closed` is permanently terminal — see below. |
| Create a ticket continuing a `Closed` one | Yes | Yes | Yes, own Customer | Yes, where Subject |
| Delete a ticket | **Nobody.** Cancellation is the only removal. |

Notes on the ticket-write rules:

- **A `Closed` ticket is never reopened, by anybody.** There is no reopen operation to grant.
  A matter is continued by creating a **new** ticket that carries a Preceded-by link to the
  closed one, which is an ordinary create and is authorized exactly like one — see
  [01-DomainModel.md](01-DomainModel.md) §9.1. The linked predecessor must belong to the same
  Customer and be `Closed`; a predecessor the caller cannot see is `404`, not `403`.
- **Serving tickets is fully open to both Accountant roles.** Verifying, responding,
  assigning, and closing are all available to an Accountant User. This is the core
  of what the role exists for.
- **Assignment is not exclusive.** Any Accountant may act on any ticket regardless of who the
  Assignee is. The Assignee records accountability, not a lock. An Accountant User may
  reassign a ticket away from an Accountant Admin — there is no seniority in assignment.
- **Picking up a ticket and setting the Assignee are one atomic operation.** A transition to
  `InReview` that would leave the Assignee null is rejected.
- Field values may be edited only in `Draft` or `AwaitingInformation`, and only by a
  Customer-side actor within scope. An Accountant may edit **only** Accountant-only fields,
  never a Customer-supplied value — rejecting it and requesting a correction is the only
  path, because overwriting destroys the record of who claimed what.
- An Employee cannot open a ticket on behalf of anyone, including a colleague.

## 8. Documents

| Action | AA | AU | CA | EMP |
|---|---|---|---|---|
| Upload to a ticket | Yes | Yes | Yes, own Customer | Yes, where Creator or Subject |
| Download from an open ticket | Yes | Yes | Yes, own Customer | Yes, where Creator or Subject |
| Download from a `Closed` ticket | Yes | Yes | Yes, own Customer | Yes, where Creator or Subject |
| Delete a document | Soft-delete only | Soft-delete only | Own uploads, before `InReview` | Own uploads, before `InReview` |

**Every cell in that last row is a soft delete.** There is no hard delete of a Document for
anyone — the row and the bytes are kept permanently and the flag only hides it, because
retention is indefinite. `Document` is the only entity in the system with a soft delete, and the
`Documents` slice must enforce the exclusion with a global query filter rather than a
per-handler `WHERE` clause. See [01-DomainModel.md](01-DomainModel.md) §9.2.

A soft-deleted document must be absent from a ticket's document list and must return `404` on
download — not `403`, which would confirm it exists.

A document inherits its access rules entirely from its ticket. There is no way to reach a
document except through a ticket the caller may already read. Download URLs must not be
guessable, must not be permanently valid, and must re-check authorization **at the moment of
download**, not at the moment the URL was issued.

Downloading from a closed ticket is explicitly permitted — it is a stated requirement.

## 9. Notifications

| Action | AA | AU | CA | EMP |
|---|---|---|---|---|
| List own notifications | Yes | Yes | Yes | Yes |
| Mark own notification read | Yes | Yes | Yes | Yes |
| Read another actor's notifications | **Nobody, including Accountant Admins.** |

## 10. Audit

| Action | AA | AU | CA | EMP |
|---|---|---|---|---|
| Read the audit log | **Yes, only** | No | No | No |
| Write to the audit log | **Nobody.** Written only by the application. |
| Edit or delete an audit entry | **Nobody.** No API exists for this. |

The audit log is restricted to Accountant Admin **because it records what Accountant Users
did**. If an Accountant User could read it, it would stop functioning as oversight of the
people most able to cause harm. That is the reason for the restriction, and it should not be
relaxed for convenience.

## 11. Authentication and account self-service

| Action | AA | AU | CA | EMP |
|---|---|---|---|---|
| Log in | Yes, if `Active` | Yes, if `Active` | Yes, if `Active` and Customer `Active` | Yes, if `Active` and Customer `Active` |
| Change own password | Yes | Yes | Yes | Yes |
| Request a password reset | Yes | Yes | Yes | Yes |
| Accept an invitation, set first password | Yes | Yes | Yes | Yes |
| Reset another person's password directly | **Nobody.** Re-issue an invitation or trigger a reset email instead. |

Suspending a Customer immediately blocks login for every Customer Admin and Employee
belonging to it. Accountants of both roles are unaffected.

**"Immediately" means the login handler reads the Customer's current status on every login**, via
`ICustomerApi.IsActiveAsync`. It is not a cascade of writes performed at suspension time: suspending
a Customer changes exactly one row in `customers` and touches no UserAccount. This is why
`Identity → Customers` exists in the dependency table — see
[03-SliceInventory.md](03-SliceInventory.md) section 2. Do not cache or denormalise the status onto
`user_accounts`; a stale copy fails at the one moment it is needed.

Note the converse, which is correct and will look like a bug: reactivating a Customer does **not**
reactivate its people's accounts. Those have their own status, owned by `Identity`. A reactivated
Customer whose Customer Admin is still `Suspended` still cannot log in.

---

## 12. Rules a builder must not violate

1. Never trust a role claim without also checking scope.
2. Never rely on the React app to hide data. Internal Notes, Accountant-only fields, and
   out-of-scope records must be **absent from the API response**, not merely unrendered.
3. Return `404` for out-of-scope records, `403` for role failures.
4. Never expose a cross-Customer listing endpoint to a Customer-side role, not even one
   returning an empty list — the endpoint itself must reject the role.
5. Every denial is audited.
6. **Accountant Admin is the ceiling.** There is no super-admin above it, and no bypass.
7. The two Accountant roles differ in exactly the four powers listed in section 1. Do not
   invent further distinctions, and do not collapse the four.
