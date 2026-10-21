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
