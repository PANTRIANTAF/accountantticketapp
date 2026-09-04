# Documents Slice — Implementation Plan

Build this **sixth**, after `Audit`, `Notifications`, `Customers`, `Identity`, and `Employees`, and
**immediately before `Tickets`** — closer to it than any other pair in the system, because
`Tickets` registers this slice's HTTP endpoints (§0.2). Neither half works alone.

It is the smallest slice by behaviour and the most dangerous by consequence: it holds personal tax
and payroll data as bytes, and a single forgotten check serves a file to somebody who was told it
was gone.

Documents that govern this slice, in precedence order. Where any of them disagrees with this plan,
**they win and this plan is wrong**.

- [02-AuthorizationMatrix.md](../../02-AuthorizationMatrix.md) §8 (Documents), §6–§7 (Tickets, for
  the access rules a document inherits), §12
- [01-DomainModel.md](../../01-DomainModel.md) §6 (Document, and the upload-hygiene rules), §9.2
  (retention, and the mandatory global query filter), §9.10 (no generation)
- [04-Infrastructure.md](../../04-Infrastructure.md) §7 (bytes live in PostgreSQL, `BYTEA`)
- [03-SliceInventory.md](../../03-SliceInventory.md) §1 line 21, §2, §3 rules 1–7
- [App/GeneralAppArchitecture.md](../../App/GeneralAppArchitecture.md) §5 (transactions, and the
  read-only exception for downloads), §8

---

## 0. Prerequisites — read before writing any code

### 0.1 What this slice is, in one paragraph

It stores bytes and metadata, and it serves them back. It does not decide who may see them, it does
not generate anything, it does not scan anything, and it does not delete anything. Everything
interesting about a document — which ticket it belongs to, who may read that ticket, whether the
ticket is still open — is **`Tickets`'** knowledge, and this slice never learns it.

[03-SliceInventory.md](../../03-SliceInventory.md) §1, line 21, is the sentence to keep in view:

> `Documents` — Document. Upload, storage, and authorized download. No virus scanning. **Not owned:
> which ticket a document is allowed to be read through — that authorization comes from `Tickets`.**

### 0.2 This slice has **no HTTP endpoints** — decided, and it is the whole design

`02-AuthorizationMatrix.md` §8: *"A document inherits its access rules entirely from its ticket.
There is no way to reach a document except through a ticket the caller may already read. Download
URLs … must re-check authorization **at the moment of download**, not at the moment the URL was
issued."*

Those rules require the ticket's access decision on **every single request**. But
[03-SliceInventory.md](../../03-SliceInventory.md) §2 permits `Documents → Audit` and nothing else,
and `Tickets → Documents` already exists, so `Documents → Tickets` would be a **cycle** — forbidden
by dependency rule 1.

**The resolution, decided: `Tickets` registers the document routes.**

| Concern | Owner |
|---|---|
| `/api/documents/upload`, `/download`, `/list`, `/delete` — the routes, the DTOs, the handlers | **`Tickets`** |
| Authorizing the ticket, the Customer scope, the Creator/Subject check, the status check | **`Tickets`** |
| The `DocumentUploaded` / `DocumentDownloaded` / `DocumentSoftDeleted` audit entries | **`Tickets`** — it is the slice that knows the ticket and the actor's relationship to it |
| Storing and reading the bytes, the metadata row, the soft-delete flag, the global query filter, the magic-byte validation, the size cap | **`Documents`**, through `IDocumentApi` (§5) |

Consequences, all of them deliberate:

1. **There is no `DocumentsEndpoints.cs`, no `MapDocumentEndpoints()`, and no
   `DocumentsActionCatalogue`.** This slice contributes **one** line to `Program.cs`, not two.
   Every other slice contributes two.

   > [App/GeneralAppArchitecture.md](../../App/GeneralAppArchitecture.md) §7's `Program.cs` example
   > currently shows `app.MapDocumentEndpoints();`. **That line must be removed**, and a numbered
   > document takes precedence over this plan — so this is an **amendment to raise**, not something
   > to code around. §14 item 1. Do not create an empty `MapDocumentEndpoints()` to satisfy the
   > example; an extension method that maps nothing is worse than a corrected doc.

2. **The permission actions (`UploadDocument`, `DownloadDocument`, `DeleteDocument`) live in
   `TicketsActionCatalogue`.** Action names are globally unique, so they must not also appear in a
   `Documents` catalogue — a duplicate is a startup failure naming both slices.

3. **This is the second instance of the pattern already LOCKED for `Employees`**, which registers
   `/api/customers/onboard` ([03-SliceInventory.md](../../03-SliceInventory.md) §1). Same reasoning,
   same accepted cost: a route under one domain's prefix is registered from another slice's file,
   and the registration site carries a comment saying why (§5.6 rule 3).

4. **No second inverted interface is created.** `Documents` defines no `ITicketAccessCheck` and
   `Tickets` implements nothing for it. Dependency rule 7 warns that the inverted pattern *"is easy
   to abuse into a hidden cycle"*, and an inverted interface that returns an **authorization
   decision** rather than data is exactly that abuse. The one v1 inverted interface remains
   `IRecipientDirectory`.

5. **`IDocumentApi` applies no authorization at all**, and that is the sharpest rule in this plan.
   §5 states it four different ways because a contract that is safe only when every caller
   remembers something is a contract that will be called wrongly.

### 0.3 What `Tickets` must do on every call — restated here because a bug here is a data breach

This belongs in the `Tickets` plan and is repeated here so the two cannot drift. Before any
`IDocumentApi` call, `Tickets` must:

1. `RequireAsync(user, "UploadDocument" | "DownloadDocument" | "DeleteDocument")`.
2. Load the **ticket** with `.WhereInCustomerScope(user)`; not found → `404`.
3. For an `Employee` role, additionally require Creator **or** Subject (matrix §6). For a
   `CustomerAdmin`, own Customer is sufficient.
4. For a `Draft` ticket, require the caller to be the Creator — drafts are private to their Creator
   regardless of role, and **no Accountant ever sees a draft**
   ([01-DomainModel.md](../../01-DomainModel.md) §9.3).
5. **Verify the document actually belongs to that ticket:**
   `if (doc.TicketId != ticket.Id) → 404`.

   > This is the step that gets skipped, because the ticket check passed and the document was
   > found, so both halves look verified. They are not: the caller supplied **both** ids
   > independently. Pair a ticket you may read with a document id from a ticket you may not, and
   > without step 5 the bytes are served. It is a textbook IDOR, and every unit test that checks
   > "a document on my own ticket downloads" passes. §12.3.

6. Audit the operation.

### 0.4 `CustomerScope` — `Document` implements `ICustomerScoped`, and it is defence in depth

`Document` carries `CustomerId` and implements `ICustomerScoped`, even though the ticket check in
§0.3 already constrains the Customer. Both exist on purpose: if step 2 or step 5 is ever wrong, the
scope filter is a second, independent barrier standing between a caller and another Customer's
payroll data.

But **`IDocumentApi` cannot apply it** — it takes no `CurrentUser` (§5 rule 2). So the filter is
available to this slice's own internal queries and to `Tickets`, and the `CustomerId` column's real
job here is to make the mismatch **assertable**: `IDocumentApi.OpenAsync` returns `CustomerId`, and
`Tickets` compares it to the ticket's. §5.2 rule 4.

### 0.5 There is no virus scanning — do not add one

[01-DomainModel.md](../../01-DomainModel.md) §6 and
[04-Infrastructure.md](../../04-Infrastructure.md) §7, both explicit and both phrased as a
prohibition rather than an omission:

> **There is no virus scanning, and no scan state.** This is a deliberate decision, not an omission
> — do not add a `ScanState` field, a quarantine status, or a "pending scan" condition on download.

So: no `ScanState` column, no `Quarantined` status, no `IVirusScanner`, no background scan job (§9.2
forbids background jobs that touch data anyway), and **no condition on the download path** waiting
for a scan that will never happen. The allow-list (§3) and the size cap carry the entire defence.

### 0.6 There is no document generation — nothing to build

[01-DomainModel.md](../../01-DomainModel.md) §9.10, LOCKED. No templates, no WYSIWYG editor, no PDF
library, no `QuestPDF`, no `iText`, no HTML-to-PDF. An Accountant attaches pre-made files. If a
PDF-producing package appears in the project file, something is being built that the spec forbids.

### 0.7 The four decisions locked for this slice

| # | Decision |
|---|---|
| 1 | **`Tickets` registers the routes and does the authorizing.** §0.2. |
| 2 | **Bytes live in PostgreSQL, in a `BYTEA` column** — not a large object, not a volume, not object storage. [04-Infrastructure.md](../../04-Infrastructure.md) §7. |
| 3 | **Allow-list: `application/pdf`; `image/jpeg`, `image/png`, `image/tiff`; OOXML `.docx`/`.xlsx`; legacy OLE2 `.doc`/`.xls`. Maximum 25 MB.** §3. |
| 4 | **Soft delete only, with a mandatory EF global query filter.** [01-DomainModel.md](../../01-DomainModel.md) §9.2. §2.4. |

---

## 1. Database schema (SQL migration)

**File:** `Slices/Documents/Infrastructure/Migrations/20260903_001_CreateDocumentsSchema.sql`

**Two** tables — the metadata and the bytes, deliberately separated. §1.1 explains why.

```sql
CREATE TABLE documents (
    id                UUID PRIMARY KEY DEFAULT gen_random_uuid(),

    -- The tenant boundary (ICustomerScoped) and the ticket it belongs to.
    -- No foreign keys: both belong to other slices.
    customer_id       UUID NOT NULL,
    ticket_id         UUID NOT NULL,

    -- 'CustomerUpload' | 'AccountantResponse'
    origin            VARCHAR(30) NOT NULL,

    -- As supplied by the client, sanitised. NEVER used as a filesystem path.
    original_file_name VARCHAR(255) NOT NULL,

    -- The type this slice DETERMINED from the leading bytes -- not the declared header.
    content_type      VARCHAR(100) NOT NULL,
    size_bytes        BIGINT NOT NULL,

    -- SHA-256 of the content, hex. For integrity and duplicate reporting only -- see notes.
    content_hash      CHAR(64) NOT NULL,

    uploaded_by_user_account_id UUID NOT NULL,
    uploaded_at       TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    -- The one soft delete in the system. 01-DomainModel.md §9.2.
    deleted_at        TIMESTAMPTZ NULL,
    deleted_by_user_account_id UUID NULL,

    CONSTRAINT ck_documents_origin CHECK (origin IN ('CustomerUpload', 'AccountantResponse')),

    -- The two soft-delete columns are set together or not at all. A row with a
    -- deleted_at and no deleter cannot answer "who deleted it", which §6 requires.
    CONSTRAINT ck_documents_deletion CHECK (
        (deleted_at IS NULL     AND deleted_by_user_account_id IS NULL)
        OR
        (deleted_at IS NOT NULL AND deleted_by_user_account_id IS NOT NULL)
    ),

    CONSTRAINT ck_documents_size CHECK (size_bytes > 0 AND size_bytes <= 26214400)
);

CREATE TABLE document_contents (
    document_id UUID PRIMARY KEY REFERENCES documents(id),
    content     BYTEA NOT NULL
);
```

| Column | Note |
|---|---|
| `customer_id` | The `ICustomerScoped` property. Denormalised from the ticket — see §1.2. |
| `ticket_id` | What `Tickets` checks in §0.3 step 5. Not a foreign key: `Tickets` owns that table. |
| `origin` | `CustomerUpload` or `AccountantResponse` ([01-DomainModel.md](../../01-DomainModel.md) §6). Derived from the uploader's role by `Tickets`, never client-supplied — §5.1 rule 6. |
| `original_file_name` | `VARCHAR(255)`, sanitised on the way in **and** on the way out (§4.3). |
| `content_type` | The **sniffed** type, never the `Content-Type` header. §3 rule 2. |
| `size_bytes` | `BIGINT`, and constrained to the 25 MB cap in the database as well as the app. Belt and braces: an app-side cap that is changed in one of two places leaves the other wrong, and the database is the one that cannot be bypassed. |
| `content_hash` | SHA-256 hex. **Not** a uniqueness key — see below. |
| `deleted_at`, `deleted_by_user_account_id` | Who deleted it and when, exactly as §6 of the domain model requires. |

`26214400` is 25 × 1024 × 1024. Write it as the literal with that comment; a builder computing
`25 * 1000 * 1000` produces a different number from the one Caddy is configured with, and uploads
between the two limits fail at the proxy with an error the app never sees.

### 1.1 Why the bytes are in a second table

Both tables are in this slice and there is exactly one row of `document_contents` per `documents`
row, so this is not normalisation for its own sake. It is because **PostgreSQL will otherwise TOAST
the `BYTEA` into a side table anyway**, and because:

1. **`SELECT * FROM documents` is safe.** Listing a ticket's ten documents must not read 250 MB of
   bytes into memory to render ten file names. With one table, any query that forgets an explicit
   projection does exactly that — and EF's default materialisation of an entity reads every mapped
   column. A separate table makes the expensive read impossible to trigger accidentally.
2. **The metadata entity can be `AsNoTracking`-projected freely** without a `Select` that a
   maintainer might "simplify".
3. **The `REFERENCES documents(id)` is intra-slice**, so a real foreign key is correct here — the
   only one in this schema. It guarantees no orphaned bytes and no bytes without metadata.

> There is **no** `ON DELETE CASCADE`, because nothing is ever deleted
> ([01-DomainModel.md](../../01-DomainModel.md) §9.2). A cascade clause is harmless but it
> advertises an operation that must not exist. Leave it off.

### 1.2 Why `customer_id` is denormalised onto the document

The ticket already knows its Customer, and this slice cannot join to `tickets`. Three reasons this
copy is correct rather than a stale-data risk:

1. **A ticket's owning Customer is immutable.** A ticket belongs to the Customer it was opened for,
   and there is no operation that moves one — the same argument that makes
   `employees.customer_id` immutable ([the Employees plan](../Employees/IMPLEMENTATION_PLAN.md) §1)
   and that justifies `user_accounts.customer_id` ([the Identity plan](../Identity/IMPLEMENTATION_PLAN.md)
   §1). A copy of an immutable value cannot go stale.
2. **It is what makes `ICustomerScoped` possible here**, and therefore what makes the second,
   independent barrier in §0.4 exist.
3. **`Tickets` supplies it at upload time and this slice never derives it.** §5.1 rule 3.

Contrast with what is **not** copied: the **ticket's status**. That is mutable — `Draft` →
`Submitted` → `InReview` → … — and it governs whether a Customer-side actor may still delete their
own upload (matrix §8, "before `InReview`"). A copy would be wrong the moment an Accountant picks
the ticket up. **`Tickets` evaluates the status itself, live, and this slice has no status column.**
The two rules point in opposite directions and they live in the same table; §1.2 exists so nobody
"completes" the denormalisation.

### 1.3 `content_hash` is not a uniqueness constraint

There is **no unique index on `content_hash`**, and there must not be. The same PDF legitimately
appears on two tickets, at two Customers, uploaded by two people — deduplicating it would mean one
row's bytes serving two documents, and then soft-deleting one of them either breaks the other or
does nothing.

What the hash is for: verifying at download that the bytes have not been corrupted, and reporting
"you already uploaded this file to this ticket" as a **warning**, never a rejection. Compute it
during the same pass that sniffs the leading bytes.

### 1.4 Indexes

```sql
-- The one query that matters: a ticket's live documents, in upload order.
-- Partial, matching the global query filter, so the filter is free.
CREATE INDEX idx_documents_ticket
    ON documents (ticket_id, uploaded_at)
    WHERE deleted_at IS NULL;

-- Defence in depth for the scope filter, and the only cross-Customer query shape.
CREATE INDEX idx_documents_customer
    ON documents (customer_id)
    WHERE deleted_at IS NULL;

-- "Has this exact file already been put on this ticket?" (§1.3) -- NOT unique.
CREATE INDEX idx_documents_ticket_hash
    ON documents (ticket_id, content_hash);
```

`idx_documents_ticket`'s `WHERE deleted_at IS NULL` mirrors the global query filter exactly. If the
two ever disagree the index silently stops being usable for the slice's main query, so **change them
together or not at all**.

### 1.5 No deletes

No `DELETE` statement anywhere, in any handler, in any script. `Document` is the **only** entity in
the system with a soft delete, and a soft delete is not a delete: the row stays, **the bytes stay**,
and the flag hides it ([01-DomainModel.md](../../01-DomainModel.md) §9.2).

- **There is no undelete**, and no endpoint that finishes the job later.
- **There is no purge job.** §9.2 rule 2 forbids background work that removes data; the one hosted
  service in the system is the `Notifications` email drainer.
- **`document_contents` is never touched by a soft delete.** A handler that sets `deleted_at` and
  then nulls the bytes to save space has hard-deleted the document while leaving a row that claims
  otherwise.

---

## 2. EF Core entities and DbContext

### 2.0 Column naming — mandatory

Entities PascalCase, columns snake_case, **no automatic conversion configured**. Every property
needs an explicit `HasColumnName`, or one code path fails at runtime with
`42703: column d.TicketId does not exist`.

### 2.1 `Core/Document.cs`

```csharp
public sealed class Document : ICustomerScoped
{
    public Guid Id { get; set; }
    public Guid CustomerId { get; set; }
    public Guid TicketId { get; set; }

    public string Origin { get; set; } = DocumentOrigin.CustomerUpload;

    public string OriginalFileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string ContentHash { get; set; } = string.Empty;

    public Guid UploadedByUserAccountId { get; set; }
    public DateTimeOffset UploadedAt { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }
    public Guid? DeletedByUserAccountId { get; set; }

    public bool IsDeleted => DeletedAt is not null;
}

// NOT in this file. See the amendment below.
public static class DocumentOrigin { … }
```

> **AMENDED 2026-09-02 — `DocumentOrigin` moves to `ExternalInterfaces/DocumentOrigin.cs`.** This
> section previously declared it in `Core/Document.cs`, immediately below the entity, and that is
> where it was built. It cannot stay there. `Tickets` is the ONLY caller that ever has to produce an
> `Origin` — it derives one from the uploader's role — and dependency rule 2 forbids a slice from
> reading another slice's `Core`. So the contract required one of two exact strings while hiding both
> in the one folder its only caller may not read. `StoreDocumentRequest` (in `ExternalInterfaces`)
> validates `Origin` against `DocumentOrigin.All` with an `Ordinal` comparer and throws on a miss, so
> the values are contract vocabulary by definition.
>
> The builder that hit this duplicated the two literals as private consts in
> `Tickets/Application/Handlers/UploadDocumentHandler.cs` and reported it, which was the right call
> given the spec — but a second definition of a string matched with `StringComparer.Ordinal` is one
> typo away from a throw at the boundary. `Core/Document.cs` now carries `using
> …Slices.Documents.ExternalInterfaces;` for the `Origin` default; a slice reading its OWN
> `ExternalInterfaces` is fine. `All` is declared there too, with the Ordinal rationale
> (`ck_documents_origin` is case-sensitive, so accepting `"customerupload"` turns a 422 into a 500).

Three notes:

- **There is no `byte[] Content` property on `Document`.** That is the point of §1.1. The bytes are
  a separate entity, reached by one deliberate method (§2.2). Adding a `Content` property — even a
  lazy-loaded one — reintroduces the accidental 250 MB read.
- **There is no `TicketStatus`, no `ScanState`, no `IsQuarantined`.** §0.5, and §1.2.
- **There is no navigation to `Ticket` or `Customer`.** Other slices' entities; a navigation would
  require this context to map their tables.

### 2.2 `Core/DocumentContent.cs`

```csharp
public sealed class DocumentContent
{
    public Guid DocumentId { get; set; }
    public byte[] Content { get; set; } = [];
}
```

It has no `ICustomerScoped`, no audit columns, and no soft-delete flag — it is addressed only by
`DocumentId`, and it is only ever reached after the corresponding `Document` has been found through
the filtered query (§5.2 rule 2). **It must not be reachable by any other path.**

### 2.3 `Infrastructure/DocumentsDbContext.cs`

```csharp
public sealed class DocumentsDbContext : DbContext
{
    public DocumentsDbContext(DbContextOptions<DocumentsDbContext> options) : base(options) { }

    public DbSet<Document> Documents => Set<Document>();
    public DbSet<DocumentContent> DocumentContents => Set<DocumentContent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new DocumentConfiguration());
        modelBuilder.ApplyConfiguration(new DocumentContentConfiguration());
    }
}
```

1. **The `DbContextOptions<DocumentsDbContext>` constructor is required.** §6 rule 1.
2. **Never `AddScoped<DocumentsDbContext>()`.**
3. It maps exactly two entities, both owned by this slice.

### 2.4 The global query filter — mandatory, and it is a normative requirement

[01-DomainModel.md](../../01-DomainModel.md) §9.2 does not merely recommend this; it specifies the
mechanism and the reason:

> A soft-delete column's real cost is that every query must exclude the deleted rows, and forgetting
> once serves a file a user was told was gone. **Discipline is not the mechanism.**

```csharp
// DocumentConfiguration.Configure
builder.HasQueryFilter(d => d.DeletedAt == null);
```

Rules:

1. **It is declared on the entity configuration**, so the default for every LINQ query in this slice
   is already correct and a handler that forgets a `WHERE` still behaves.
2. **`IgnoreQueryFilters()` must appear nowhere in this slice.** §9.2 rule 2 is explicit: *"At the
   time of writing no handler needs this, so a use of `IgnoreQueryFilters()` should be treated as a
   mistake until a spec says otherwise."* Treat it as a review-blocking finding. If a genuine need
   appears, that is a spec change to raise.
3. **`DocumentContent` gets no filter**, because it has no `DeletedAt` column — and that is exactly
   why `OpenAsync` must find the `Document` **through the filtered query first** and only then read
   the bytes by id (§5.2 rule 2). Reading `DocumentContents` directly bypasses the entire
   soft-delete mechanism, and it is a one-line mistake that no test catches unless §12.2 has the
   case.
4. **The download path re-checks `DeletedAt` at download time**, which the filter gives for free —
   §9.2 rule 3. A link handed out before the delete must stop working after it.
5. **The soft-delete write itself does not need `IgnoreQueryFilters`.** It loads a live document
   (the filter permits it), sets the two columns, and saves. Deleting an already-deleted document is
   a `404`, because the filtered query does not find it — which is the correct answer anyway (§5.3
   rule 5).

### 2.5 Configuration

`Infrastructure/Configurations/DocumentConfiguration.cs` and `DocumentContentConfiguration.cs`.
Every property gets `HasColumnName`, and `HasMaxLength` matching the DDL exactly.

Specifics:

- `builder.HasQueryFilter(d => d.DeletedAt == null)` — §2.4.
- `builder.Property(d => d.ContentHash).HasColumnName("content_hash").HasMaxLength(64).IsFixedLength()`
  — the column is `CHAR(64)`; without `IsFixedLength()` EF and PostgreSQL disagree about padding and
  an equality comparison silently never matches.
- `builder.Property(d => d.SizeBytes).HasColumnName("size_bytes")` — `long`, mapping to `BIGINT`.
  An `int` overflows at 2 GB, which the 25 MB cap makes unreachable today and which costs nothing to
  get right.
- `DocumentContentConfiguration`: `builder.HasKey(c => c.DocumentId)`,
  `builder.Property(c => c.Content).HasColumnName("content").HasColumnType("bytea")`, and
  `builder.ToTable("document_contents")`.

---

## 3. Upload validation — the allow-list and the size cap

`Application/UploadValidation.cs`. One static class, called by `IDocumentApi.StoreAsync` **and by
nothing else**, so the rules cannot be applied inconsistently.

Decision 3, and the reason the docs give it so much space: *"Because there is no scanner, upload
hygiene carries the whole defence"* ([01-DomainModel.md](../../01-DomainModel.md) §6).

### 3.1 The allow-list

| Accepted type | Leading bytes (hex) | Note |
|---|---|---|
| `application/pdf` | `25 50 44 46` (`%PDF`) | |
| `image/jpeg` | `FF D8 FF` | |
| `image/png` | `89 50 4E 47 0D 0A 1A 0A` | The full 8-byte signature, not just `89 50` |
| `image/tiff` | `49 49 2A 00` or `4D 4D 00 2A` | Little- and big-endian; both are valid TIFF |
| `.docx` (`application/vnd.openxmlformats-officedocument.wordprocessingml.document`) | `50 4B 03 04` | ZIP container — see §3.2 |
| `.xlsx` (`application/vnd.openxmlformats-officedocument.spreadsheetml.sheet`) | `50 4B 03 04` | ZIP container — see §3.2 |
| `.doc` (`application/msword`) | `D0 CF 11 E0 A1 B1 1A E1` | OLE2 compound file — see §3.3 |
| `.xls` (`application/vnd.ms-excel`) | `D0 CF 11 E0 A1 B1 1A E1` | Same signature as `.doc` — see §3.3 |
| `.csv` (`text/csv`) | **none** | Text has no signature — see §3.3a. §13 item 7, **decided** |
| `.txt` (`text/plain`) | **none** | Same — see §3.3a. §13 item 7, **decided** |

`.csv` and `.txt` were added by the §13 item 7 decision; `.pptx` was considered and **excluded** as
implausible for an accounting office. Widening later is easy, so the list stays at the narrowest
shape that covers real Customer behaviour.

**Explicitly excluded, and each for a stated reason:**

| Rejected | Why |
|---|---|
| `image/svg+xml` | SVG is XML that executes script. The SPA and API **share an origin** ([01-DomainModel.md](../../01-DomainModel.md) §6), so an SVG served from this app runs with the session cookie available. |
| `text/html` | Same reason, more obviously. |
| `application/zip`, `.rar`, `.7z`, `.tar`, `.gz` | An archive hides its contents from the sniffer entirely. Accepting one accepts everything inside it. |
| Everything else | It is an allow-list, not a block-list ([01-DomainModel.md](../../01-DomainModel.md) §6). An unrecognised signature is a `422`, never a "store it and hope". |

### 3.2 The OOXML problem — read this, it is the one genuinely hard case

A `.docx` and a `.xlsx` are **ZIP archives**, and their leading bytes are `50 4B 03 04` — byte for
byte identical to a plain `.zip`, which §3.1 rejects. So the signature alone cannot distinguish an
allowed Office file from a forbidden archive.

Distinguishing them requires opening the container and reading the `[Content_Types].xml` entry.
Rules:

1. **Read the central directory, not the whole archive into memory.** `System.IO.Compression.ZipArchive`
   over a `MemoryStream` in `ZipArchiveMode.Read` lists entries without inflating them.
2. **Require `[Content_Types].xml` at the archive root, and decide from the entry *names*.** A
   `word/document.xml` entry means `.docx`; `xl/workbook.xml` means `.xlsx`. Both present, or neither
   → reject as a plain archive.

   > **Corrected 2026-09-02. This rule previously said to "read the declared default/override content
   > types from it", which directly contradicted rule 5 below** — you cannot read that XML without
   > inflating the entry holding it, and rule 5 forbids inflating entries. The contradiction was found
   > during implementation and resolved in favour of rule 5, because the entry names are *sufficient*:
   > they already distinguish the two accepted formats from a plain archive, so parsing the XML buys
   > nothing and costs the one thing this section is trying to avoid. `[Content_Types].xml` is
   > therefore **presence-checked, never opened**. Criterion 12 is worded accordingly.
   >
   > If someone later genuinely needs the declared content types, rule 5 has to be relaxed
   > deliberately, with its own size cap on that single entry — not quietly, and not by treating this
   > note as permission.
3. **Cap the entry count and the total uncompressed size before inflating anything.** A zip bomb is
   a 25 MB upload that decompresses to gigabytes, and this validation path is the one place the
   application voluntarily parses attacker-controlled structure.

   **The numbers, so they are specified rather than invented at each rewrite: 512 entries and
   100 MiB total uncompressed.** Both are generous for a real Office document and neither is close to
   a memory problem. Read them from the central directory, which `ZipArchive` in
   `ZipArchiveMode.Read` exposes without inflating anything.
4. **Wrap the whole thing in a `try`/`catch` and treat any exception as a `422`**, not a `500`.
   `App/GeneralAppArchitecture.md` §8: a client-triggerable value is always a `4xx`. A malformed
   ZIP is entirely client-triggerable.
5. **Do not inflate entry contents to inspect them.** The entry *names* and the declared content
   types are enough. Inflating is where the bomb detonates.
6. **The extension must also be an allowed one.** A real `.docx` renamed `.zip` passes every check in
   rules 1-5 — signature and container both say OOXML — and is nonetheless a **`422`**. §13 item 4,
   **decided**. The reasoning: `.zip` is not in §3.1, a plain `.zip` is already rejected, and
   accepting this case would mean the allow-list is a list of *contents* in one branch and a list of
   *extensions* in every other. One rule is worth more than the marginal convenience of accepting a
   misnamed Office file, which a user can fix by renaming it back. So OOXML acceptance requires
   **both** the container inspection to succeed **and** the extension to be `.docx` or `.xlsx`; the
   resulting `content_type` is the one the container proved, never the one the extension implied.

> If this feels like a lot of machinery for two file types, it is — and it is the price of decision 3
> including Office formats. The alternative was PDF and images only, which was considered and not
> chosen because a Customer emailing an `.xlsx` payroll sheet is the single most common
> Customer-side upload.

### 3.3 The OLE2 problem — `.doc` and `.xls` share one signature

`D0 CF 11 E0 A1 B1 1A E1` is the OLE2 compound-file header, and it is identical for `.doc`, `.xls`,
`.ppt`, and several other legacy formats. Telling them apart requires parsing the compound-file
directory structure, which is considerably nastier than a ZIP.

**Do not parse it.** Accept the OLE2 signature and resolve the specific type from the **file
extension**, then store that as `content_type`:

- `.doc` → `application/msword`
- `.xls` → `application/vnd.ms-excel`
- any other extension with an OLE2 header → `422`

### 3.3a The text problem — `.csv` and `.txt` have no signature at all

Added by the §13 item 7 decision. Every other entry in §3.1 is verified by its leading bytes; a text
file has none, so there is nothing to sniff. **Signature sniffing cannot verify a text file, and
pretending otherwise is worse than admitting it** — the verification here is by *content shape*, and
these are the rules:

1. **Reject any file containing a `NUL` (`0x00`) byte.** Text does not contain NULs; binary content
   reliably does. This one check rejects the overwhelming majority of binaries mislabelled `.txt`.
2. **Reject anything that is not valid UTF-8.** Decode strictly, with
   `new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)`, and treat
   the exception as a `422`. A UTF-8 BOM is permitted and skipped before the check.
3. **Reject any control character other than TAB (`0x09`), LF (`0x0A`) and CR (`0x0D`).**
4. **Reject a file whose leading bytes match *any* signature in §3.1 or in the excluded table.** A
   PDF renamed `.txt` is a `422`, an OOXML container renamed `.csv` is a `422`. The
   extension-must-not-contradict-the-bytes rule still holds in this direction, which is the
   direction that matters.

   > **The hard part, added 2026-09-02 after implementation exposed it: the two most dangerous
   > excluded types have no signature to match.** SVG and HTML are text — that is the entire reason
   > §3.1 excludes them — so a rule phrased as "match a signature" is silently unenforceable against
   > precisely the content it most needs to stop, and rules 1-3 all *pass* an SVG. Without this, a
   > `.txt` rename is a clean bypass of the SVG and HTML exclusions.
   >
   > So for the text branch, treat these leading markup prefixes as the signature those formats lack,
   > case-insensitively and after skipping leading whitespace and a BOM: `<?xml`, `<svg`, `<html`,
   > `<!doctype html`, `<!doctype svg`, `<!--`. Any of them → `422`.
   >
   > Same shape of problem for `tar`, which the excluded table lists and which has no leading
   > signature either: detect it by the `ustar` magic at **offset 257**.
   >
   > This does **not** try to stop a text file that merely contains markup somewhere in the middle —
   > that is undecidable, and it is what §4.1's `attachment` + `nosniff` is for. It stops a file whose
   > *whole content* is a markup document wearing a `.txt` name.
5. **Resolve `content_type` from the extension** — `.csv` → `text/csv`, `.txt` → `text/plain` — and
   store that. This is the **second** documented relaxation of "never trust the extension", after
   §3.3, and it is narrower than it looks: rules 1-4 have already established that the bytes *are*
   text, so the extension is only choosing between two labels for the same verified content.
6. **No CSV parsing, no delimiter sniffing, no line-ending normalisation, no encoding conversion.**
   The bytes are stored exactly as uploaded (§1 requires a byte-identical round-trip).

> **Why a `.txt` full of `<script>` is safe here, and why that is not luck.** It would be dangerous
> served `inline` from a shared origin — which is precisely why §4.1 makes every download
> `Content-Disposition: attachment` with no exception and §4.2 adds `X-Content-Type-Options:
> nosniff`. Those two headers are what make a text allow-list entry defensible at all. **If anyone
> ever adds an `inline` download path, `.csv` and `.txt` must come out of the allow-list in the same
> change.** Cross-reference this note from §4.1.

This is a deliberate, narrow relaxation of the "never trust the extension" rule, and it is safe
because the *signature* was still verified: an attacker can mislabel a `.xls` as a `.doc`, which
achieves nothing, but cannot get an HTML file past the OLE2 check. **Comment it at the code**, or
the next reader will read it as the mistake the docs warn about.

### 3.4 Rules

1. **Validate before the row is written and before the bytes are stored.** One transaction, and the
   `422` happens first.
2. **Never trust the declared `Content-Type` header or the file extension** — except for the narrow
   OLE2 disambiguation in §3.3, which is commented. Both are attacker-controlled
   ([04-Infrastructure.md](../../04-Infrastructure.md) §7).
3. **The stored `content_type` is the type this slice determined**, and it is what the download sets
   on the response. A sniffed type stored alongside a client-declared one used on download defeats
   the whole exercise.
4. **Enforce the size cap before the body is buffered**, per §7 of the infrastructure doc — via
   `RequestSizeLimit` / `MultipartBodyLengthLimit` on the endpoint (which `Tickets` owns) and the
   `ck_documents_size` constraint as the backstop. **Caddy must be configured with a matching
   `request_body max_size`** or the two limits disagree; §14 item 3.
5. **A zero-byte file is a `422`.** `ck_documents_size` requires `> 0`. An empty upload is a client
   bug, and storing it produces a document that downloads as nothing.
6. **Sanitise `original_file_name` on the way in**: strip any path separator, any `..`, any control
   character, and cap at 255. [01-DomainModel.md](../../01-DomainModel.md) §6: *"The stored filename
   is never used as a filesystem path, and is sanitised before being echoed back."* It is sanitised
   **again** on the way out (§4.3), because the two escape into different contexts.
7. **Read the leading bytes from the stream, then rewind before hashing and storing** — or read the
   whole body once into a buffer and work over it. A stream consumed by the sniffer and then stored
   writes zero bytes, and the `ck_documents_size` constraint is what catches it.

---

## 4. Download response shaping

`ExternalInterfaces/DownloadShaping.cs` — the **rules** are this slice's, because they are properties
of serving bytes, not of authorizing a ticket. Put the helper here and let `Tickets` call it, so the
headers cannot be got right in one place and wrong in another.

> **AMENDED 2026-09-02 — the folder is `ExternalInterfaces`, and "or inline in the `Tickets`
> endpoint" is withdrawn.** This section said `Application/DownloadShaping.cs`, which is where it was
> built, and that made the single call the plan mandates — `DownloadShaping.For(...)` in
> `Tickets`' download handler — a dependency-rule-2 violation on paper while being the only correct
> thing to write. `Tickets` is obliged to call this (its download route cannot write the headers
> without it) and it takes and returns only `ExternalInterfaces` types, so it IS part of the contract.
> `DownloadHeaders` moved with it, since `Tickets` reads `ContentTypeOptionsHeaderName` from it.
>
> The withdrawn alternative was never really an alternative: inlining the rules in the endpoint puts
> the always-`attachment` disposition and `nosniff` in the one place this slice cannot test, and §4.1
> explains why those two are the entire defence for `.csv` and `.txt`.

[04-Infrastructure.md](../../04-Infrastructure.md) §7 and
[01-DomainModel.md](../../01-DomainModel.md) §6:

### 4.1 `Content-Disposition: attachment`, always

> An HTML or SVG file served **inline** from the app's own origin runs scripts with the session
> cookie available.

- **Always `attachment`, never `inline`, never absent.** Not configurable, not a query parameter,
  not "inline for images because the SPA wants a preview". If the SPA wants a preview it can build
  one from the downloaded blob client-side; that is its problem and it does not get an `inline`
  header to solve it.
- The allow-list already excludes SVG and HTML, so this is defence in depth — which is the point.
  Two independent controls, either of which would have been sufficient.
- **For `.csv` and `.txt` it is not defence in depth — it is the only defence.** Those two entries
  (§3.3a, added by the §13 item 7 decision) accept arbitrary text, which means they accept the text
  of an HTML document or a script. Rules 1-4 there establish only that the bytes *are* text, not that
  the text is harmless, and no allow-list check can establish that. The `attachment` header and
  `nosniff` are what make the entries defensible. **If an `inline` download path is ever added,
  `.csv` and `.txt` must leave the allow-list in the same change** — and since "always `attachment`"
  is stated above as non-negotiable, the practical form of that rule is: this bullet is the tripwire
  if anybody weakens the one above it.

### 4.2 `X-Content-Type-Options: nosniff`, always

Without it a browser may sniff the content and disregard the declared type, which reintroduces
exactly what §4.1 prevents. Set it on the response, on every download, alongside the explicit
`Content-Type` from the stored `content_type` column (§3.4 rule 3).

### 4.3 The filename in the header must be sanitised again

The name was sanitised for storage (§3.4 rule 6). It now escapes into an **HTTP header**, a
different context with different dangerous characters:

- A `CR` or `LF` in a header value is **response splitting**. The storage sanitiser already strips
  control characters; do not rely on that alone, because the two sanitisers protect different things
  and one of them may be relaxed later.
- Use the `filename*=UTF-8''<percent-encoded>` form for non-ASCII names, with a plain ASCII
  `filename=` fallback. Greek filenames are the normal case for this application, not an edge case,
  and a raw non-ASCII byte in a header is not valid.
- Quote the value and escape any `"` and `\`.

### 4.4 Every download is audited — this is a requirement, not a nicety

[01-DomainModel.md](../../01-DomainModel.md) §6: *"Every download is recorded as an Audit Entry.
This is a requirement, not a nicety — these files contain personal tax and payroll data."*

`App/GeneralAppArchitecture.md` §5 rule 4 names this as **the one exception** to "read-only handlers
open no transaction":

> Read-only handlers open no transaction. They call neither `BeginAsync` nor `IAuditApi`, with one
> exception: a document **download** is audited, so it behaves like a mutation even though it
> changes nothing.

So the download handler in `Tickets` **opens a transaction**, writes `DocumentDownloaded`, and
commits. Two consequences a builder will get wrong:

1. **Commit before streaming the bytes**, not after. Once the response body has started, the status
   code is sent and a failed commit cannot be reported — and `RequestTransaction.DisposeAsync()`
   rolls back, so a commit attempted after streaming discards the audit entry **while the caller
   receives the file**. A downloaded document with no audit row is the exact failure §6 of the
   domain model exists to prevent. This is the same class of trap as the login-failure path in
   [the Identity plan](../Identity/IMPLEMENTATION_PLAN.md) §7.0 rule D.
2. **A denied download is audited too**, by `PermissionChecker`, with no transaction (§5 rule 3 of
   the architecture doc). Both the grant and the denial are on the record.

---

## 5. The `IDocumentApi` contract — this slice's entire public surface

**Files:** `Slices/Documents/ExternalInterfaces/IDocumentApi.cs`, `DocumentApi.cs`

One slice calls this: **`Tickets`**.

```csharp
public sealed record DocumentSummary(
    Guid Id,
    Guid TicketId,
    Guid CustomerId,
    string Origin,
    string OriginalFileName,
    string ContentType,
    long SizeBytes,
    Guid UploadedByUserAccountId,
    DateTimeOffset UploadedAt);

/// <summary>Metadata plus the bytes. Returned only by OpenAsync.</summary>
public sealed record DocumentContentResult(DocumentSummary Document, byte[] Content);

public sealed record StoreDocumentRequest(
    Guid TicketId,
    Guid CustomerId,
    string Origin,
    string OriginalFileName,
    string DeclaredContentType,
    Stream Content,
    Guid UploadedByUserAccountId);

public interface IDocumentApi
{
    // ── WRITES. Enlist in the caller's transaction; never commit. ──

    /// <summary>Validates (§3), stores metadata and bytes, returns the new summary.
    /// Throws AppException(422) on a rejected type, size, or empty body.
    /// DOES NOT AUTHORIZE ANYTHING. See rule 1.</summary>
    Task<DocumentSummary> StoreAsync(StoreDocumentRequest request, CancellationToken ct = default);

    /// <summary>Sets deleted_at and deleted_by. Returns false when no live document with
    /// that id exists. Never removes a row and never touches the bytes.
    /// DOES NOT AUTHORIZE ANYTHING.</summary>
    Task<bool> SoftDeleteAsync(Guid documentId, Guid deletedByUserAccountId,
                               CancellationToken ct = default);

    // ── READS. No transaction. ──

    /// <summary>Metadata only, no bytes. Null when not found or soft-deleted.</summary>
    Task<DocumentSummary?> FindAsync(Guid documentId, CancellationToken ct = default);

    /// <summary>A ticket's live documents, oldest first. Empty for an unknown ticket.
    /// Unpaginated -- see rule 8.</summary>
    Task<IReadOnlyList<DocumentSummary>> ListByTicketAsync(Guid ticketId,
                                                           CancellationToken ct = default);

    /// <summary>Metadata AND bytes. Null when not found or soft-deleted. This is the only
    /// method that reads document_contents. DOES NOT AUTHORIZE ANYTHING -- the caller must
    /// have already authorized the ticket AND must verify Document.TicketId. See rule 1.</summary>
    Task<DocumentContentResult?> OpenAsync(Guid documentId, CancellationToken ct = default);
}
```

### 5.0 Rule 1, stated four times because once is not enough

> **`IDocumentApi` performs no authorization. None. It has no `CurrentUser` parameter, it applies no
> Customer scope filter, and it will hand any caller the bytes of any live document in the system
> given only its id.**

It is built that way deliberately, and there is no safe alternative given the dependency graph:
this slice cannot evaluate a ticket's access rules (§0.2), so a contract that *pretended* to
authorize would be worse than one that visibly does not.

Therefore:

1. **The XML doc comment on `OpenAsync` and `StoreAsync` says so in capitals**, as written above.
   The one place a caller reads a contract is IntelliSense.
2. **`Tickets` performs every check in §0.3 first**, including step 5.
3. **A test asserts the unsafe behaviour deliberately** (§12.2): calling `OpenAsync` with a
   different Customer's document id **returns the bytes**. That test looks like it documents a
   vulnerability, and that is precisely its value — it fails the day somebody adds a filter here
   and quietly moves the security boundary without moving the responsibility.
4. **No other slice may ever be given this dependency.** Only `Tickets` may depend on `Documents`
   ([03-SliceInventory.md](../../03-SliceInventory.md) §2), and this is why that matters more here
   than for any other contract in the system.

### 5.1 `StoreAsync` rules

1. **It enlists in the caller's transaction and never commits.** `IRequestTransaction.EnlistAsync`,
   the same pattern as `IIdentityApi`'s write methods
   ([the Identity plan](../Identity/IMPLEMENTATION_PLAN.md) §9.1 rule 6). The metadata row, the
   bytes, the ticket change that prompted the upload, and the audit entry are one atomic unit — and
   because the bytes are in PostgreSQL rather than on a volume, that atomicity is real. There is no
   orphaned-file cleanup job in this system because there can be no orphaned file
   ([04-Infrastructure.md](../../04-Infrastructure.md) §7).
2. **It writes both tables**, `documents` then `document_contents`, in one `SaveChangesAsync`.
3. **`CustomerId` is supplied by the caller**, from the ticket it already loaded (§1.2 rule 3).
   This slice does not and cannot derive it. **`Tickets` must pass the *ticket's* Customer, not
   `user.CustomerId`** — for an Accountant that is null, and the row would violate `NOT NULL` at
   best and be silently wrong at worst.
4. **`Origin` is supplied by the caller and must be one of the two constants.** Validate it and
   throw on anything else; `ck_documents_origin` is the backstop.
5. **`Origin` is derived from the uploader's role, by `Tickets`, and is never client-supplied.** An
   Accountant uploading gives `AccountantResponse`; a Customer-side actor gives `CustomerUpload`
   ([01-DomainModel.md](../../01-DomainModel.md) §6). If it came from the request body, a Customer
   could mark their own upload as an Accountant response and change what the ticket appears to say.
   **State this in the `Tickets` plan as well.**
6. **It validates via §3 and throws `AppException(422)`** — never `500`, and never a stored row with
   an unvalidated type.
7. **It computes `ContentHash` in the same pass as the sniffing** (§1.3, §3.4 rule 7).
8. **It does not audit.** `Tickets` writes `DocumentUploaded`, because `Tickets` knows the ticket and
   the actor's relationship to it. §5.5.

### 5.2 `OpenAsync` rules

1. **It is the only method that reads `document_contents`**, and `DocumentApi` is the only class
   that may.
2. **It finds the `Document` through the filtered query first, then reads the bytes by id.**

   ```csharp
   var doc = await _db.Documents.AsNoTracking()
       .FirstOrDefaultAsync(d => d.Id == documentId, ct);   // global filter applies
   if (doc is null) return null;

   var content = await _db.DocumentContents.AsNoTracking()
       .FirstOrDefaultAsync(c => c.DocumentId == documentId, ct);
   ```

   > **`DocumentContents` has no soft-delete column and therefore no filter** (§2.4 rule 3).
   > Querying it directly — or joining from it — serves the bytes of a deleted document. Two
   > queries in this order is the mechanism; a single join in the wrong direction is the bug, and
   > it is one line.

3. **A soft-deleted document returns `null`, and `Tickets` turns that into `404`** — never `403`.
   Matrix §8: *"A soft-deleted document must be absent from a ticket's document list and must
   return `404` on download — not `403`, which would confirm it exists."*
4. **It returns `CustomerId` and `TicketId` on the summary so the caller can assert them**, which is
   what makes §0.3 step 5 and §0.4 possible at all.
5. **It verifies the content hash** and throws `AppException(500)` on a mismatch — the one place a
   `500` is right, because corrupted bytes are a server fault, not a client one. Serving silently
   corrupted tax data is worse than failing.
6. **It reads the whole byte array into memory.** At 25 MB that is acceptable at one-Office scale;
   streaming straight from `BYTEA` would need a data reader this slice does not otherwise use.
   Record it in §13.
7. **It does not audit.** `Tickets` writes `DocumentDownloaded` and commits **before streaming**
   (§4.4 rule 1).

### 5.3 `SoftDeleteAsync` rules

1. **It sets `deleted_at` and `deleted_by_user_account_id` together**, or `ck_documents_deletion`
   rejects the row.
2. **It never touches `document_contents`** (§1.5).
3. **It enlists in the caller's transaction and never commits.**
4. **It returns `false` rather than throwing when nothing live matches**, so `Tickets` maps it to
   `404` with its own message.
5. **Deleting an already-deleted document returns `false` → `404`.** Not `422`, and not an
   idempotent `200`: the global filter simply does not find it, and `404` is the correct answer for
   a document the caller can no longer see — consistent with rule 3 of §5.2.
6. **There is no `UndeleteAsync`, and there is no `HardDeleteAsync`.** §9.2: *"There is no undelete
   endpoint, and no hard-delete endpoint that finishes the job later."* Do not add either, not even
   marked internal.
7. **The permission rule it does not enforce:** matrix §8 gives Accountants any document on a ticket
   they can see, and gives Customer-side actors **their own uploads only, and only while the ticket
   has not yet reached `InReview`**. Both halves need the ticket's status and the uploader's
   identity, so **`Tickets` evaluates them** — the uploader from `DocumentSummary.UploadedByUserAccountId`,
   the status from its own row (§1.2). State the full rule in the `Tickets` plan.

### 5.4 `ListByTicketAsync` and `FindAsync` rules

1. **The global filter excludes soft-deleted documents**, satisfying matrix §8's *"absent from a
   ticket's document list"* structurally rather than by a `WHERE` somebody must remember.
2. **Oldest first**, matching `idx_documents_ticket`'s `(ticket_id, uploaded_at)`.
3. **An unknown ticket id returns an empty list, not an error.** The caller has already established
   the ticket exists; a throw here would turn its `404` into a `500`.
4. **Neither returns bytes.** That is `OpenAsync` alone, and it is why `DocumentSummary` has no
   `Content` property.
5. **`ListByTicketAsync` is unpaginated.** A ticket with 500 attachments returns 500 rows — of
   metadata only, so it is bounded and small. Acceptable; noted in §13.
6. **Neither applies a scope filter** (§5.0).

### 5.5 What this slice never does

- **It writes no audit entries.** All three codes — `DocumentUploaded`, `DocumentDownloaded`,
  `DocumentSoftDeleted` — already exist in `Slices/Audit/ExternalInterfaces/AuditActions.cs`, and
  **`Tickets` writes all three.** This slice therefore does not inject `IAuditApi` at all, despite
  `Documents → Audit` being its one permitted edge.

  > That leaves this slice with a declared dependency it does not use, exactly as `Employees` has an
  > unused `Notifications` edge ([the Employees plan](../Employees/IMPLEMENTATION_PLAN.md) §6 rule
  > 3). **Do not add an audit call to make the edge look used.** Two entries for one upload is worse
  > than an unused edge, and the entry `Tickets` writes is the better one — it knows the ticket, the
  > Customer, and the actor's relationship to both. §14 item 2 raises whether the edge should be
  > removed from the dependency table.

- **It sends no notifications** and has no `Notifications` edge.
- **It checks no permissions** and does not inject `IPermissionChecker`.
- **It reads no other slice's table** and names no other slice's types.
- **It has no `IHostedService`**, no background work, and no scheduled anything (§9.2 rule 2).
- **It exposes no handler**, no endpoint, and no action catalogue (§0.2).

---

## 6. Service registration

### 6.1 `Slices/Documents/DocumentsRegistration.cs`

```csharp
public static IServiceCollection AddDocumentsSlice(
    this IServiceCollection services, IConfiguration configuration)
{
    // The SHARED request connection overload. See 6.3 rule 1.
    services.AddDbContext<DocumentsDbContext>((serviceProvider, options) =>
        options.UseNpgsql(serviceProvider.GetRequiredService<RequestConnection>().Connection));

    services.AddScoped<IDocumentApi, DocumentApi>();

    return services;
}
```

That is the whole file. **No handlers, no endpoints, no action catalogue** — three registrations
fewer than any other slice, because §0.2.

### 6.2 What `Program.cs` adds

**Exactly one line**, not two:

```csharp
builder.Services.AddDocumentsSlice(builder.Configuration);
```

There is no `app.MapDocumentEndpoints();`. See the boxed note in §0.2 point 1: the example in
`App/GeneralAppArchitecture.md` §7 still shows that line, that document takes precedence over this
plan, and correcting it is §14 item 1 — **raise it, do not silently diverge**.

The registration must still appear **before** `AddTicketsSlice`, matching the dependency order the
architecture doc's example uses.

### 6.3 Registration traps

1. **`AddDbContext` must use the `(serviceProvider, options)` overload and `RequestConnection`.**
   The plain `options => options.UseNpgsql(connectionString)` overload compiles and gives this slice
   its **own connection** — at which point `StoreAsync`'s `EnlistAsync` joins nothing, and an upload
   commits independently of the ticket change and the audit entry that were supposed to be atomic
   with it. **The bytes then survive a rolled-back ticket operation**, which is the one failure mode
   the "bytes in PostgreSQL" decision exists to make impossible
   ([04-Infrastructure.md](../../04-Infrastructure.md) §7). Nothing fails visibly; §12.1 has the
   test.
2. **Never `AddScoped<DocumentsDbContext>()`.**
3. **`IDocumentApi` is `AddScoped`, not `AddSingleton`** — it holds a scoped DbContext. A singleton
   captures one context for the process lifetime.
4. **Do not register an `IActionCatalogue` for this slice.** `UploadDocument`, `DownloadDocument`,
   and `DeleteDocument` belong to `TicketsActionCatalogue` (§0.2 point 2). Registering them in both
   is a startup failure naming both slices — which is the designed behaviour, but a confusing first
   symptom for a builder who does not know why.
5. **Do not add `Microsoft.EntityFrameworkCore.InMemory`** to reach the `BYTEA` column in a test. It
   is banned from the API project, and it cannot represent this schema anyway (§12.1).

### 6.4 Startup smoke check

There is no endpoint to curl. The check therefore runs through `Tickets`, and it is worth writing
the moment both slices exist:

```bash
# 1. Upload a real PDF to a ticket.
curl -sb jar.txt -X POST localhost:5000/api/documents/upload \
  -F 'ticketId=<guid>' -F 'file=@sample.pdf'
#    expect 200

# 2. The bytes came back byte-identical.
curl -sb jar.txt -X POST localhost:5000/api/documents/download \
  -d '{"ticketId":"<guid>","documentId":"<guid>"}' -o out.pdf
sha256sum sample.pdf out.pdf    # must match

# 3. The download response headers.
#    expect: Content-Disposition: attachment; ...
#            X-Content-Type-Options: nosniff
#            Content-Type: application/pdf

# 4. Rename an HTML file to .pdf and upload it.
cp evil.html evil.pdf && curl -sb jar.txt -X POST .../upload -F 'file=@evil.pdf'
#    expect 422 -- the extension says PDF, the leading bytes do not

# 5. Soft-delete it, then download it again.
#    expect 404 -- NOT 403, and NOT the bytes

# 6. Confirm the row and the bytes are still there.
psql -c "select deleted_at is not null, octet_length(c.content) > 0
         from documents d join document_contents c on c.document_id = d.id
         where d.id = '<guid>';"
#    expect (t, t) -- soft delete keeps the bytes
```

Steps 4, 5, and 6 are the ones that actually matter. Step 6 is the only check that distinguishes a
correct soft delete from a hard delete wearing a flag.

---

## 7. Migrations — SQL scripts, not `dotnet ef`

**File:** `Slices/Documents/Infrastructure/Migrations/20260903_001_CreateDocumentsSchema.sql`

- `YYYYMMDD_###_Description.sql`. The sequence restarts at `001` **per slice**, which is why the
  runner tracks the **slice-relative path with forward slashes**, never `Path.GetFileName`
  (`App/GeneralAppArchitecture.md` §6 — LOCKED).
- **Never `dotnet ef migrations add`.** If a `Migrations/` folder with C# files appears, delete it.
- One script: both tables, all three `CHECK` constraints, the intra-slice foreign key, all three
  indexes.
- **No rollback script.** Append-only.
- Set the build action so the file is copied to the output directory, or every query fails with
  `42P01: relation "documents" does not exist`.

---

## 8. Endpoints

**None.** §0.2.

The routes that reach this slice's data are registered by `Tickets` and specified in its plan. They
are listed here for cross-reference only, and the `Tickets` plan is authoritative:

| Method | Route | Registered by | Roles |
|---|---|---|---|
| `POST` | `/api/documents/upload` | `Tickets` | AA, AU, CA (own Customer), EMP (Creator or Subject) |
| `POST` | `/api/documents/list` | `Tickets` | same |
| `POST` | `/api/documents/download` | `Tickets` | same, **including from a `Closed` ticket** |
| `POST` | `/api/documents/delete` | `Tickets` | AA, AU any; CA/EMP own uploads before `InReview` |

Four rules for whoever builds those routes:

1. **A comment at each registration site** naming this plan §0.2 and saying in one line why a
   `/api/documents/*` route is registered from the `Tickets` slice. Without it, somebody will
   "tidy" them into `Documents` and create the cycle.
2. **Downloading from a `Closed` ticket is explicitly permitted** — matrix §8: *"Downloading from a
   closed ticket is explicitly permitted — it is a stated requirement."* A blanket "no writes on a
   `Closed` ticket" guard in `Tickets` must not catch the download path.
3. **`/api/documents/upload` is the one multipart endpoint in the system.** Every other route takes
   a JSON body. It needs `DisableRequestSizeLimit` replaced by an explicit
   `RequestSizeLimit(DocumentLimits.MaxUploadSizeBytes)` and a matching `MultipartBodyLengthLimit`,
   and it is the one place an `IFormFile` appears.

   > **AMENDED 2026-09-02 — this rule used to write `RequestSizeLimit(26_214_400)`,** a bare literal,
   > which contradicts criterion 30 in this same document ("the 25 MB limit is declared once as a
   > named constant"). Two statements of one policy disagree only for uploads sized between them, so
   > the bug is invisible until a file lands in the gap. Take the number from
   > `ExternalInterfaces/DocumentLimits.cs`.
   >
   > It also needs `.DisableAntiforgery()`, which no rule here mentioned. That is not optional:
   > minimal-API form binding demands antiforgery validation, this application registers no antiforgery
   > services and no `UseAntiforgery` middleware, and so without it the route throws on every request —
   > at REQUEST time, not startup. The CSRF defence for this endpoint is the auth cookie's
   > `SameSite=Strict`, which a cross-site form post cannot carry.
4. **No route parameters** — ids go in the body, even for the download. Kebab-case for any
   multi-word segment.

---

## 9. Cross-slice boundaries

| Slice | Relationship |
|---|---|
| `Tickets` | **Calls this slice**, through `IDocumentApi`. Registers its routes. Does all authorization and all auditing. |
| `Audit` | The one permitted dependency — **and it is unused** (§5.5, §14 item 2). |
| Everything else | No relationship in either direction. |

Five boundary rules:

1. **`Documents` never references `Tickets`.** Not its `ExternalInterfaces`, not a status enum, not
   a constant. `Tickets → Documents` exists, so the reverse is a cycle (§0.2). This is the rule the
   whole slice is shaped around.
2. **`Documents` defines no inverted interface** (§0.2 point 4). Dependency rule 7: *"Do not invent
   a second inverted interface without raising it."* It was raised, and the answer was no.
3. **`Documents` names no other slice's `Core` types.** It has no reason to — its contract types are
   built from `Guid`, `string`, `long`, `byte[]`, and `DateTimeOffset`.
4. **No slice other than `Tickets` may be given a `Documents` dependency**, ever, for the reason in
   §5.0 rule 4: `IDocumentApi` will hand any caller any document's bytes.
5. **`Documents` has no `Notifications` and no `Identity` edge**, and needs neither. It stores
   `uploaded_by_user_account_id` as an opaque `Guid` and never resolves it to a name — `Tickets`
   does that, through `IIdentityApi`, for the slice that renders the list.

---

## 10. What is deliberately absent

A checklist, because this is the slice where a builder is most likely to add something helpful that
the spec forbids. **None of these may appear:**

| Absent | Authority |
|---|---|
| A virus scanner, `ScanState`, quarantine status, or pending-scan condition | [01-DomainModel.md](../../01-DomainModel.md) §6; [04-Infrastructure.md](../../04-Infrastructure.md) §7 |
| Any PDF generation, template, or WYSIWYG editor | [01-DomainModel.md](../../01-DomainModel.md) §9.10, LOCKED |
| A hard delete, an undelete, or a purge job | [01-DomainModel.md](../../01-DomainModel.md) §9.2 |
| `IgnoreQueryFilters()` anywhere in the slice | [01-DomainModel.md](../../01-DomainModel.md) §9.2 rule 2 |
| Object storage, a filesystem volume, or a PostgreSQL large object | [04-Infrastructure.md](../../04-Infrastructure.md) §7 |
| Thumbnail or preview generation | Would mean decoding attacker-controlled image data, which is what §0.5 declines to do |
| A `Content-Disposition: inline` path, or a content-type from the client | §4.1, §3.4 rule 3 |
| Image resizing, PDF page counting, text extraction, or OCR | Same reason as thumbnails; nothing in the spec asks for them |
| A signed or time-limited download URL | The re-check-at-download rule (matrix §8) means the authorization must run per request anyway, which makes a signed URL a second mechanism with no added guarantee |
| An `IActionCatalogue`, an endpoint file, or a handler | §0.2 |
| A `ZIP`, `SVG`, or `HTML` entry in the allow-list | §3.1 |

---

## 11. Tests

### 11.1 At least one test must run against real PostgreSQL — mandatory

`Microsoft.EntityFrameworkCore.InMemory` is banned from the API project and permitted only in the
test project. For this slice it is not merely inadequate, it is **incapable**:

- There is no `BYTEA`, so the central thing this slice does cannot be exercised
- All three `CHECK` constraints are ignored
- The intra-slice foreign key is ignored
- **There is no real transaction**, so the `EnlistAsync` behaviour in §6.3 trap 1 — the single most
  damaging registration mistake here — cannot be detected
- Partial indexes do not exist, so the filter/index correspondence in §1.4 is untested

So: a real-PostgreSQL test covering, at minimum, a byte-identical round trip of a multi-megabyte
file, a rolled-back transaction leaving **neither** a `documents` row **nor** a `document_contents`
row, a soft delete leaving both rows intact, and a zero-byte insert rejected by
`ck_documents_size`.

> Docker is currently not starting on this machine, so no PostgreSQL exists and **no part of this
> schema has ever been applied**. Every SQL statement in §1 and §7 is unverified — including
> `gen_random_uuid()`, which requires `pgcrypto` or PostgreSQL 13+ built-in. When Docker works,
> apply the migration first and fix the script before trusting any of this plan's DDL.

### 11.2 Behavioural cases

| Case | Expected |
|---|---|
| Round-trip a 10 MB PDF | bytes are byte-identical; SHA-256 matches |
| Round-trip a file with a Greek filename | the name survives, percent-encoded in the header |
| Upload a valid PDF | `content_type` is `application/pdf`, sniffed |
| Upload an HTML file renamed `.pdf` | `422` |
| Upload an SVG | `422` |
| Upload a plain `.zip` | `422` — not mistaken for OOXML |
| Upload a real `.docx` | `200`; `[Content_Types].xml` inspected |
| Upload a real `.xlsx` | `200` |
| Upload a `.docx` renamed `.zip` | `422` — **decided** (§13 item 4). The container says OOXML, but `.zip` is not an allowed extension, and one rule beats a carve-out |
| Upload a `.csv` of ordinary text | `200`, `content_type` `text/csv` from the extension (§3.3a rule 5) |
| Upload a `.txt` of ordinary text | `200`, `content_type` `text/plain` |
| Upload a `.txt` with a UTF-8 BOM | `200`, and the round-tripped bytes **still contain the BOM** — it is skipped for validation, never stripped from storage |
| Upload a `.txt` containing a `NUL` byte | `422` (§3.3a rule 1) |
| Upload a `.txt` of invalid UTF-8 (e.g. a lone `0xFF`) | `422` (§3.3a rule 2), not a `500` and not a replacement character |
| Upload a `.txt` containing `0x1B` | `422` (§3.3a rule 3) — a control character that is not TAB/CR/LF |
| Upload a `.txt` containing a TAB, a CRLF and a bare LF | `200` — all three are permitted |
| Upload a real PDF renamed `.txt` | `422` (§3.3a rule 4) — the signature check runs in this direction too |
| Upload a `.docx` renamed `.csv` | `422` (§3.3a rule 4) — an OOXML container is not text |
| Upload a `.txt` containing `<script>alert(1)</script>` | `200` — and the download asserts `attachment` + `nosniff`, which is the whole reason this is safe (§3.3a's boxed note) |
| Upload a zip bomb (small file, huge uncompressed) | `422`, and **no out-of-memory** |
| Upload a malformed ZIP with an OOXML extension | `422`, **not `500`** |
| Upload a `.xls` (OLE2) | `200`, `content_type` `application/vnd.ms-excel` from the extension |
| Upload an OLE2 file with a `.pdf` extension | `422` |
| Upload a 0-byte file | `422` |
| Upload a 26 MB file | `422`, and the body is **not** fully buffered first |
| Upload with `Content-Type: application/pdf` on a PNG | `200`, `content_type` stored as `image/png` |
| Upload with a filename of `../../etc/passwd` | stored name is sanitised; no path separator survives |
| Upload with a filename containing `\r\n` | sanitised; the download header is not split |
| Upload with `origin` absent or invalid | throws — the contract validates it |
| `Tickets` passes `user.CustomerId` (null, Accountant) as `CustomerId` | fails loudly, not a null row |
| `StoreAsync` inside a transaction that then rolls back | **neither** table has a row |
| `StoreAsync` writes both tables | one `SaveChangesAsync`, both rows present |
| `ListByTicketAsync` after a soft delete | the document is **absent** |
| `FindAsync` on a soft-deleted document | `null` |
| `OpenAsync` on a soft-deleted document | `null` → `404` from `Tickets`, **never `403`** |
| Soft delete, then check the database | row present, `deleted_at` set, `deleted_by` set, **bytes still present** |
| Soft delete an already-deleted document | `false` → `404` |
| Soft delete with a null deleter | `ck_documents_deletion` rejects it |
| A download link issued before a soft delete, used after | `404` |
| Download response headers | `attachment`, `nosniff`, explicit `Content-Type` |
| Download response header | **never** `inline`, for any type, including images |
| Download from a `Closed` ticket | `200` — matrix §8, a stated requirement |
| Download | writes a `DocumentDownloaded` audit entry |
| Download where the audit commit fails | the bytes are **not** streamed — §4.4 rule 1 |
| Download of a document whose stored hash does not match its bytes | `500`, not corrupted bytes |
| **`OpenAsync` with another Customer's document id, called directly** | **returns the bytes** — §5.0 rule 3 |
| `Tickets` download with a valid ticket and a document from a **different** ticket | `404` — §0.3 step 5 |
| Two documents with identical content on two tickets | both stored, both independent; no dedup |
| Soft-deleting one of two identical-content documents | the other still downloads |
| A denied upload/download/delete | writes an Audit entry |
| The slice's source, grepped for `IgnoreQueryFilters` | zero matches |
| The slice's source, grepped for `DELETE`/`Remove(` | zero matches |
| `DocumentSummary` type | has no `Content` property — assert by reflection |

### 11.3 The four tests that are easy to write wrongly

1. **The transaction test must query the database in a new scope** after the request completed, and
   must check **both** tables. Checking only `documents` passes when the bytes leaked, and checking
   the response status passes either way. This is the test for §6.3 trap 1, the most damaging
   mistake in the slice.
2. **The soft-delete test must assert the bytes are still there** (`octet_length(content) > 0`), not
   just that the download returned `404`. A hard delete produces the same `404` and passes every
   test that only looks at the API.
3. **The `OpenAsync`-returns-another-Customer's-bytes test is not a bug report.** Write it, name it
   so its intent is unmistakable — `OpenAsync_AppliesNoAuthorization_ByDesign` — and put §5.0's
   reasoning in a comment. If somebody later adds a filter inside `DocumentApi`, this test fails and
   forces the conversation about where the security boundary lives, instead of the boundary quietly
   moving to a place that cannot enforce it.
4. **The IDOR test needs two tickets the caller can see, and two they cannot.** The interesting case
   is a ticket the caller **may** read paired with a document from a ticket they **may not** — that
   is the one §0.3 step 5 exists for, and the one that passes when step 5 is missing.

---

## 12. Known constraints

1. **A 25 MB upload is buffered in application memory** to sniff the leading bytes and hash the
   content (§3.4 rule 7), and a download reads the whole array (§5.2 rule 6). Ten concurrent
   uploads is a few hundred megabytes of RSS. Fine on one host at one-Office scale; it is a real
   number to size against, not a free parameter.
2. **Every byte is in every `pg_dump`, forever.** Retention is indefinite
   ([01-DomainModel.md](../../01-DomainModel.md) §9.2) and soft-deleted documents keep their bytes,
   so the database grows monotonically and backups grow with it. Accepted by
   [04-Infrastructure.md](../../04-Infrastructure.md) §7 for the benefit of one atomic backup.
3. **No deduplication.** Ten copies of the same PDF are ten copies of the bytes (§1.3). The right
   trade at this scale, and it is why `content_hash` is indexed but not unique.
4. **No virus scanning** (§0.5). A malicious PDF or Office file is stored and served. The
   `attachment`-only download, the `nosniff` header, and the allow-list are the whole defence, and
   they stop a file executing **against this origin** — they do not stop it exploiting the user's
   local PDF reader.
5. **The OOXML container inspection parses attacker-controlled ZIP structure** (§3.2). It is bounded
   by an entry-count and uncompressed-size cap and inflates nothing, which is as safe as this can
   reasonably be made — but it is the one place the application voluntarily parses hostile input,
   and it is worth knowing that.
6. **`.doc` and `.xls` are distinguished by file extension** (§3.3), a deliberate narrow exception to
   "never trust the extension". Mislabelling one as the other is possible and harmless.
7. **`ListByTicketAsync` is unpaginated** (§5.4 rule 5). Metadata only, so bounded and small, but a
   ticket with hundreds of attachments returns them all.
8. **`IDocumentApi` is unauthenticated by design** (§5.0). The security of every document in the
   system rests on `Tickets` performing all six steps of §0.3 on every call. There is no second
   line of defence inside this slice, and that is a consequence of the dependency graph, not an
   oversight.
9. **A soft delete cannot be undone through the API** (§5.3 rule 6). Since the bytes are kept, a
   mistaken delete is recoverable by an operator against the database — which is the same out-of-band
   channel §9.2 rule 3 already establishes for deletion requests.
10. **No document is reachable without its ticket.** If a ticket ever became unreachable — an
    impossible state today, since nothing is deleted — its documents would be unreachable with it.
    Worth knowing before anybody proposes a ticket-removal operation.

---

## 13. Questions to flag rather than answer

1. ~~**`App/GeneralAppArchitecture.md` §7 shows `app.MapDocumentEndpoints();` and it must be
   removed.**~~ **RESOLVED — already amended, and this item is stale.** That document's §7 now reads
   `// NO app.MapDocumentEndpoints() -- Documents has no endpoints. Tickets registers /api/documents/*
   instead.`, followed by the paragraph explaining the one-line exception and a boxed warning against
   creating an empty extension method. Nothing to do; the two documents agree. Success criterion 30's
   first clause is likewise already satisfied.
2. **Should `Documents → Audit` be removed from the dependency table?** This slice writes no audit
   entries (§5.5) because `Tickets` is better placed to. The edge in
   [03-SliceInventory.md](../../03-SliceInventory.md) §2 is therefore unused. Keeping it costs
   nothing and leaves room for a future audit need; removing it makes the table honest. Either is
   defensible — but an unused edge is an invitation for somebody to "use" it and produce duplicate
   entries.
3. **Caddy's `request_body max_size` must be set to match 25 MB**
   ([04-Infrastructure.md](../../04-Infrastructure.md) §7: *"enforced at both the proxy and the
   application"*). The infrastructure doc does not give the number, and if the proxy limit is lower
   the app never sees the request and returns nothing useful; if it is higher the proxy buffers
   bytes the app will reject. Set both from one documented constant.

   > **BLOCKED, and the reason is larger than this slice: there is no `Caddyfile` in the repository,
   > and no deployment layer at all.** The repository root contains only `AccountantApp.Api/`,
   > `AccountantApp.Tests/`, `AccountantApp.slnx` and `Architect Files/`. There is no `Caddyfile`, no
   > `docker-compose.yml`, and no `Dockerfile`, although [04-Infrastructure.md](../../04-Infrastructure.md)
   > §5 mounts `./Caddyfile:/etc/caddy/Caddyfile:ro` and its own `### Caddyfile` section specifies the
   > contents. **Do not create a `Caddyfile` as part of this slice** — a deployment file invented to
   > satisfy a one-line cross-reference would be the only such file in the repo, unreviewed, and
   > wrong in the ninety details this slice has no opinion about. The application-side half of the
   > limit (`RequestSizeLimit` / `MultipartBodyLengthLimit`, §3.4 rule 4) **is** in scope and must be
   > set from the documented 25 MB constant. The proxy-side half is deferred to whoever builds the
   > deployment layer, and the constant is where they will find the number. Remove the
   > `Caddyfile` row from the files checklist until that layer exists.
4. ~~**Is a `.docx` renamed to `.zip` accepted or rejected?**~~ **DECIDED: rejected, `422`.** See
   §3.2 rule 6, which now carries the rule and the reasoning, and §11.2's row. Predictability won:
   accepting it would make the allow-list a list of contents in one branch and a list of extensions
   in every other, and a user can rename the file back.
5. **What is the real maximum attachment count per ticket?** §5.4 rule 5 and §12 constraint 7 both
   depend on there being no answer. A number would let the list be sized, or paginated if it needs
   to be.
6. **Should an upload of a file already on the ticket warn, or is it silent?** §1.3 says the hash
   exists partly to report duplicates as a warning, but no DTO carries a warning field and no
   `NotificationEvents` kind covers it. Either add it to the upload response shape (a `Tickets`
   decision) or drop the duplicate-reporting rationale and keep the hash for integrity alone.
7. ~~**Are there other accepted formats the Office actually needs?**~~ **DECIDED: add `.csv` and
   `.txt`; exclude `.pptx`.** See §3.1's table and **§3.3a**, which carries the content-shape rules
   these two need because neither has a signature to sniff. The original text is kept below because
   its closing argument — widening is easy, narrowing breaks a workflow — is why `.pptx` stayed out.

   [01-DomainModel.md](../../01-DomainModel.md) §6 says *"office documents **as needed**"*, which
   defers to the operator. `.csv` and `.txt` are plausible and both are trivially safe under an
   `attachment`-only download; `.pptx` is unlikely for an accounting office. Confirm the list
   against real Customer behaviour before the allow-list ships, because widening it later is easy
   and narrowing it breaks a workflow somebody has come to rely on.

---

## Files checklist

| File | Action |
|---|---|
| `Slices/Documents/Infrastructure/Migrations/20260903_001_CreateDocumentsSchema.sql` | New |
| `Slices/Documents/Core/Document.cs` | New. **NOT** `DocumentOrigin` — see §2.1's amendment |
| `Slices/Documents/Core/DocumentContent.cs` | New |
| `Slices/Documents/Infrastructure/DocumentsDbContext.cs` | New |
| `Slices/Documents/Infrastructure/Configurations/DocumentConfiguration.cs` | New — incl. the global query filter |
| `Slices/Documents/Infrastructure/Configurations/DocumentContentConfiguration.cs` | New |
| `Slices/Documents/Application/UploadValidation.cs` | New — §3. The allow-list and the sniffing only; the two LIMITS live in `ExternalInterfaces/DocumentLimits.cs` |
| `Slices/Documents/ExternalInterfaces/DownloadShaping.cs` | New — §4 (incl. `DownloadHeaders`). `ExternalInterfaces`, not `Application` — §4's amendment |
| `Slices/Documents/ExternalInterfaces/DocumentOrigin.cs` | New — §2.1's amendment. Contract vocabulary: `StoreDocumentRequest` throws on anything not in `All` |
| `Slices/Documents/ExternalInterfaces/DocumentLimits.cs` | New — `MaxUploadSizeBytes` and `MaxFileNameLength`, declared ONCE. The `Tickets` upload endpoint must apply the size cap as a `RequestSizeLimit`, and this slice has no endpoints to apply it on |
| `Slices/Documents/ExternalInterfaces/IDocumentApi.cs` | New (incl. `DocumentSummary`, `DocumentContentResult`, `StoreDocumentRequest`) |
| `Slices/Documents/ExternalInterfaces/DocumentApi.cs` | New |
| `Slices/Documents/DocumentsRegistration.cs` | New — **two** registrations (the `DbContext` and `IDocumentApi`), no handlers. §6.1's code block is the authority; this row previously said three |
| `Program.cs` | Edit — **one** line, and no `MapDocumentEndpoints()` |
| `App/GeneralAppArchitecture.md` §7 | ~~Amend~~ — **already done**, nothing to do. §13 item 1 |
| ~~`Caddyfile`~~ | **Deferred — the file does not exist and neither does any deployment layer.** §13 item 3. Set the application-side limit only |
| `AccountantApp.Tests/Documents/` | New — §12 |
| **Not this slice:** `DocumentsEndpoints.cs`, `DocumentsActionCatalogue.cs`, any handler | Must not exist — §0.2 |

---

## Success criteria

1. The migration applies to a fresh PostgreSQL database; both tables, all three `CHECK`
   constraints, the foreign key, and all three indexes exist.
2. A 0-byte and a 26 MB insert are both rejected by `ck_documents_size`, in the database.
3. A `documents` row with `deleted_at` set and `deleted_by_user_account_id` null is rejected by the
   database.
4. A multi-megabyte file round-trips byte-identically, and its SHA-256 matches the stored hash.
5. `Document` has no `Content` property, and `DocumentSummary` has no `Content` property —
   asserted by reflection.
6. `document_contents` is read by exactly one method, `OpenAsync`, and only after the filtered
   `Document` query succeeded.
7. `HasQueryFilter(d => d.DeletedAt == null)` is declared, and `IgnoreQueryFilters()` appears
   nowhere in the slice.
8. A soft-deleted document is absent from `ListByTicketAsync`, returns `null` from `FindAsync` and
   `OpenAsync`, and yields `404` — never `403` — on download.
9. After a soft delete, the row **and the bytes** are still in the database.
10. There is no undelete method, no hard-delete method, no `DELETE` statement, and no `Remove()`
    call anywhere in the slice.
11. An HTML file renamed `.pdf`, an SVG, a plain `.zip`, and a real `.docx` renamed `.zip` are each
    `422` (§3.2 rule 6).
11a. A `.csv` and a `.txt` of ordinary text are accepted, with `content_type` `text/csv` and
    `text/plain` resolved from the extension; a file containing a `NUL` byte, one that is not valid
    UTF-8, one containing a control character other than TAB/CR/LF, and a PDF renamed `.txt` are each
    `422` (§3.3a). A UTF-8 BOM is accepted and the bytes still round-trip **including** the BOM.
12. A real `.docx` and a real `.xlsx` are accepted, with `[Content_Types].xml` **required to be
    present** and **no entry inflated** — including that one. See §3.2 rule 2's correction note: the
    original wording of this criterion said "inspected", which was unsatisfiable alongside rule 5.
    The format decision comes from the entry names.
13. A zip bomb is rejected with `422` and no out-of-memory; a malformed ZIP is `422`, never `500`.
14. A `.xls` is accepted with `content_type` resolved from the extension, and an OLE2 file with a
    `.pdf` extension is `422`.
15. The stored `content_type` is always the sniffed type, never the client-declared one, and it is
    what the download sets.
16. A filename containing a path separator, a `..`, or a `CR`/`LF` is sanitised on storage and again
    in the download header.
17. Every download response carries `Content-Disposition: attachment` and
    `X-Content-Type-Options: nosniff`, and **no** response path ever emits `inline`.
18. A non-ASCII filename survives the download header, percent-encoded, with an ASCII fallback.
19. `StoreAsync` and `SoftDeleteAsync` enlist in the caller's transaction and never commit; a
    rolled-back transaction leaves **neither** table with a row.
20. A download whose audit commit fails does **not** stream the bytes.
21. Every download writes a `DocumentDownloaded` audit entry, written by `Tickets`, and this slice
    injects no `IAuditApi`.
22. `IDocumentApi` has no `CurrentUser` parameter, applies no scope filter, and a test asserts
    `OpenAsync` returns another Customer's bytes **by design**, named so its intent is
    unmistakable.
23. A download pairing a readable ticket with a document from an unreadable one returns `404`.
24. The slice registers **no** endpoints, **no** handlers, and **no** action catalogue, and
    contributes exactly **one** line to `Program.cs`.
25. `UploadDocument`, `DownloadDocument`, and `DeleteDocument` exist in `TicketsActionCatalogue`
    and in no other catalogue.
26. Downloading from a `Closed` ticket succeeds.
27. Two documents with identical content are stored independently; soft-deleting one leaves the
    other downloadable.
28. Nothing in §10's absent-checklist exists anywhere in the slice: no scanner, no `ScanState`, no
    PDF generation, no thumbnails, no signed URLs, no object storage, no hosted service.
29. `Documents` references no other slice's types, and no slice other than `Tickets` depends on it.
30. `App/GeneralAppArchitecture.md` §7 does not contain `app.MapDocumentEndpoints();` — **already
    true**, see §13 item 1. The 25 MB limit is declared once as a named constant —
    `ExternalInterfaces/DocumentLimits.MaxUploadSizeBytes`, and "once" means the `Tickets` endpoint
    reads THAT rather than restating the number (§7 rule 3) — and enforced on the
    application side; the matching Caddy `request_body max_size` is **deferred**, because no
    `Caddyfile` and no deployment layer exist in this repository (§13 item 3). Do not report this
    criterion as fully met — report it as half met, by design, with the deferred half recorded.
