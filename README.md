# NWP Invoice Automation

ASP.NET Core / Razor Pages shell for the Nationwide Produce invoice automation portal.
Built in the same design family as the NWP Ordering App.

Working principle: **rules extract, validations protect, humans approve exceptions.** The
system should become more automatic for common suppliers while financial control stays
explicit and auditable. AI/OCR is an optional future fallback, not a Phase 1 dependency.

## Milestone 1 — shell app (this commit)

- NWP-style layout and navigation shared with the Ordering App.
- Mock **Inbox classifier** seeded with the 9 real forwarded discovery examples.
- Working queues: Invoice Queue, Statements, Exceptions, Ignored / non-invoice.
- Supplier profile placeholders for the supplier-learning system.
- Document detail page with a placeholder for the original-document preview.

All data is mock/in-memory (`MockDocumentStore`, `SupplierProfileStore`). No mailbox or
SQL access yet.

## Run locally on Windows/macOS/Linux with .NET 8

```bash
cd nwp-invoice-automation
dotnet restore
dotnet run
```

Open the shown localhost URL.

## Pages

- `/` — capture dashboard with queue counts.
- `/Inbox` — all captured items with first-pass classification.
- `/Invoices` — items classified as supplier invoices and cleared for capture.
- `/Statements` — supplier account statements, separated as Maeve requested.
- `/Exceptions` — items a rule/validation could not clear (alias, nested email, wrong mailbox, …).
- `/Ignored` — order acknowledgements, ENS/customs and similar non-invoices.
- `/Suppliers` — supplier profile placeholders (admin).
- `/Documents/Detail?id=…` — original document beside extracted fields.

## Local secrets and Entra ID login

Authentication is disabled by default so the shell is easy to run. To test Entra login,
copy `appsettings.Local.json.example` to `appsettings.Local.json` and fill in the tenant,
client and admin details. `appsettings.Local.json` is git-ignored.

## Branches

- `main` — reviewed baseline.
- `dev` — active development.

## Next

Replace mock data with real capture, one piece at a time:

1. mailbox monitoring (`invoices-evesham@`, plus `accounts@` / forwarded chains);
2. attachment storage with content/MIME sniffing and nested `.eml` parsing;
3. deterministic PDF/Excel/email extraction;
4. supplier profiles with taught rules, aliases and layout-change detection;
5. validation gates and routing to Invoice Queue / Statements / Exceptions;
6. finance workflow (To Post / To Remit) and Sage export once Mike confirms the interface.

## Invoice extraction receiver

Apply `sql/001_invoice_extractions.sql` to the application database. The script is
idempotent and creates the extraction header/line tables and their constraints.
Configure these values through `appsettings.Local.json`, environment variables, or the
production secret store (never commit real values):

- `ConnectionStrings:InvoiceAutomation` (`ConnectionStrings__InvoiceAutomation` in environment variables)
- `InvoiceExtractionApi:ApiKey` (`InvoiceExtractionApi__ApiKey` in environment variables)

The temporary receiver is `POST /api/invoice-extractions`, accepts JSON only, is limited
to 1 MiB and 500 unique-numbered lines, and requires
`X-Invoice-Automation-Key`. It fails closed when the configured key is absent. Example:

```bash
curl -X POST https://localhost:5001/api/invoice-extractions \
  -H 'Content-Type: application/json' \
  -H "X-Invoice-Automation-Key: $INVOICE_AUTOMATION_KEY" \
  --data '{
    "driveId":"sharepoint-drive-id",
    "driveItemId":"sharepoint-item-id",
    "supplierName":"Example Supplier",
    "invoiceNumber":"INV-123",
    "invoiceDate":"2026-10-21",
    "currency":"GBP",
    "total":120.00,
    "confidence":0.98,
    "lines":[{"lineNumber":1,"description":"Produce","quantity":10,"unitPrice":12,"lineTotal":120}]
  }'
```

Posting the same `(driveId, driveItemId)` updates the header and atomically replaces its
lines. SharePoint Inbox reads enrich matching documents from SQL. If extraction storage
is unconfigured or unavailable, the failure is logged and SharePoint documents remain
available without extraction fields.

## SharePoint Incoming document cache

Apply `sql/002_captured_documents.sql` after `001_invoice_extractions.sql`. It is
idempotent and creates `dbo.CapturedDocument` (one row per SharePoint Incoming drive item,
keyed by `(DriveId, GraphItemId)`) and `dbo.GraphSyncState` (one row per sync worker).

The UI (`/Inbox`, `/Invoices`, `/Statements`, `/Exceptions`, document previews) reads this
table only — it never calls Microsoft Graph on a page request, so a page load never blocks
on, or fails because of, Graph. A background `GraphInboxSyncWorker` is the only thing that
calls Graph for inbox capture:

- It runs on a timer (`SharePointInbox:SyncIntervalSeconds`, default **300 seconds / 5
  minutes**, minimum 30) and only when `DocumentSource` is `SharePoint` and
  `GraphDocuments:Enabled` is `true`.
- The first cycle performs an initial Graph delta enumeration of the Incoming folder.
  Subsequent cycles follow the persisted delta cursor in `dbo.GraphSyncState`, so only
  added, changed or deleted items are processed. Changed records are transactionally
  upserted into `dbo.CapturedDocument`; Graph-reported deletions are soft-deleted
  (`IsDeleted = 1`). If Graph expires a cursor, the worker safely performs a fresh initial
  delta pass and rebuilds the cursor.
- `dbo.CapturedDocument` keeps capture/extraction columns separate from resolution columns
  (`AssignedQueue`, `WorkflowOwner`, `ReviewerNote`, `Resolved*`). The sync only ever writes
  the former, so a human's exception resolution always survives the next sync.
- If a sync cycle fails (including Graph throttling), it's logged and recorded in
  `dbo.GraphSyncState`; the UI keeps serving the last successfully synced SQL data instead
  of failing the request. Graph requests that return `429 Too Many Requests` are retried
  with bounded backoff, honouring the `Retry-After` header when Graph sends one (falling
  back to exponential backoff, capped at 30s, otherwise; up to 4 attempts per request).
- If the cache has never synced and Graph is unavailable, SharePoint reads fail closed
  unless `SharePointInbox:FallbackToMock` is `true`, in which case mock/test records are
  served and logged as a fallback.

Configure `ConnectionStrings:InvoiceAutomation` as above; no separate connection string is
needed for the document cache.
