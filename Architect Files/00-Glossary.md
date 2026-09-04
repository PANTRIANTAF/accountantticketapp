# Glossary

Normative vocabulary. Use exactly these terms in code, in identifiers, and in UI copy.
Where a term is banned, do not use it anywhere.

## Banned terms

**"Admin"** — banned when used alone. It is ambiguous between the Office side and a
customer business's manager, who have completely different permissions. Always write
**Accountant Admin** or **Customer Admin**.

**"Firm"** — banned. The accounting practice is the **Office**.

**"Business"** — fine in prose, but the entity is named **Customer**. Do not create a
`Business` type.

**"User"** — banned when used *alone* as a role name. It is fine as a generic word for
"whoever is logged in", and it is fine as the suffix in **Accountant User**, which is a
real role. There is no role called just "User".

**"Client"** — banned. Use **Customer** for the company, and reserve "client" for the
React application when talking about HTTP.

---

## Actors and roles

**Office** — the accounting practice that operates this deployment. It is **not an
entity in the database**; it is the deployment itself. There is exactly one, and it is
never a row, a foreign key, or a token claim. Do not build an Offices table, and do not
scope anything by Office.

**Accountant** — the **collective term for anyone on the Office side**, covering both
Office roles below. Not itself a role. When a permission in these documents is granted to
"an Accountant", both Office roles have it. Accountants are never scoped to a Customer;
they work across all of them.

**Accountant Admin** — the principal operator of the app; the person who runs the Office.
Everything an Accountant User can do, plus the four powers reserved to this role:
creating a Customer, suspending or reactivating a Customer, managing Accountant accounts,
and reading the audit log. There may be more than one, and **there is always at least
one**.

**Accountant User** — Office staff. Serves tickets: picks them up, verifies fields,
responds, produces documents, and closes them. Also authors and versions Ticket Types.
**Cannot create a Customer**, cannot suspend one, cannot manage Accountant accounts, and
cannot read the audit log. Many may exist.

The distinction in one sentence: **an Accountant User does the accounting work; an
Accountant Admin additionally runs the business.**

**Customer** — a **business** that is a client of the Office. Always a company, never a
natural person. This is the **tenant boundary**: almost every query in the system is
scoped by Customer, and data must never leak between Customers. An Office has many
Customers.

**Customer Admin** — a person at a Customer who manages that Customer's Employees.
Can register Employees, invite them to the system, and open tickets on their behalf.
Sees everything belonging to their own Customer. Sees nothing belonging to any other
Customer.

**Employee** — a person who works for a Customer. A Customer has many Employees. An
Employee can open tickets about themselves and see their own tickets, and cannot see
other Employees' tickets.

### The four roles

Every UserAccount holds exactly one of these:

| Role | Side | Scope |
|---|---|---|
| `AccountantAdmin` | Office | All Customers, plus app administration |
| `AccountantUser` | Office | All Customers, ticket work only |
| `CustomerAdmin` | Customer | Own Customer, everything in it |
| `Employee` | Customer | Own tickets only |

### The hierarchy, in one line

> One **Office**, staffed by **Accountant Admins** and **Accountant Users** → serving many
> **Customers** (businesses) → each with one or more **Customer Admins** and many
> **Employees**.

## Ticket work

**Assignee** — the one Accountant responsible for a Ticket. Assignment is **required** the
moment a Ticket is picked up for work, so no Ticket is ever in progress without exactly
one named person accountable for it. See the lifecycle in
[01-DomainModel.md](01-DomainModel.md).

**Reassignment** — moving a Ticket's Assignee to a different Accountant, for holidays,
absence, or workload. Distinct from picking up an unassigned Ticket.

## Identity

**Employee record** — the record of a person who works for a Customer: their name,
contact details, identifying numbers, employment dates. This record exists whether or
not the person can log in. Creating an Employee record is called **registering** the
Employee.

**User Account** — a set of login credentials, with exactly one role. A User Account
is linked to an Employee record, except Accountant accounts, which are not.

**Registered** — an Employee record exists. Says nothing about login ability.

**Subscribed** — an Employee record exists *and* has a linked User Account, so the
person can log in. Making a registered Employee subscribed is done by **inviting**
them. "Subscribing an Employee to the system" (the phrase used in the original
requirements) means exactly this.

**Accountless Employee** — a registered Employee with no User Account. Tickets can be
opened *for* them by a Customer Admin, but they cannot log in to see those tickets.

## Tickets

**Ticket** — a single request from a Customer to the Office. It has a type, a set of
field values, a status, a conversation, and zero or more documents.

**Ticket Type** — the template that defines what a Ticket of that kind contains: the
list of fields, their data types, and their validation rules. Ticket Types are
authored by Accountants and are shared across all Customers.

**Field Descriptor** — the definition of one input on a Ticket Type: its key, label,
data type, whether it is required, and its validation rules. Part of the Ticket Type,
not of the Ticket.

**Field Value** — the answer an actor gave for one Field Descriptor on one Ticket.

**Revision** — an immutable snapshot of a Ticket's Field Values at one point in time.
Correcting a Ticket creates a new Revision; the previous Revision is retained forever.
The **current Revision** is the newest one.

**Creator** — the actor who submitted the Ticket. Always a logged-in person.

**Subject** — the Employee the Ticket is *about*. Usually the same person as the
Creator. Differs when a Customer Admin opens a Ticket on behalf of an Employee.
Every Ticket has exactly one Subject.

**On-behalf-of Ticket** — a Ticket whose Creator and Subject are different people.

**Verification** — an Accountant's judgement on one Field Value: accepted, or rejected
with a reason. Recorded per field, not per Ticket.

**Correction round** — the cycle where an Accountant rejects one or more Field Values,
the Ticket returns to the Customer for fixing, and the Customer submits a new
Revision.

**Response** — an Accountant's answer on a Ticket, posted into the conversation. May
carry Documents.

**Internal Note** — a message on a Ticket visible only to Accountants. Never visible
to a Customer Admin or an Employee, and never included in any response the Customer
can reach.

**Ticket Reference** — the human-readable identifier of a Ticket, used on the phone and
in email. Distinct from the database key.

## Documents

**Document** — a file attached to a Ticket. Either **uploaded** by a Customer-side
actor as evidence, or **produced** by an Accountant as the deliverable.

## Other

**Notification** — a message telling an actor that something happened on a Ticket they
care about.

**Audit Entry** — an immutable record that some actor did something, at some time, to
some entity. Never edited, never deleted.

**Slice** — a vertical feature module in the backend, as defined in
[03-SliceInventory.md](03-SliceInventory.md).
