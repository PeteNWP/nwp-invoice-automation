# SharePoint email metadata for captured invoices

The `Invoice Automation` document library stores one PDF per captured attachment. The app reads the PDF through Microsoft Graph and reads its source-email metadata from custom columns on the same SharePoint file item.

## Required library columns

Create these columns on the **Invoice Automation** document library. Use the exact names below without spaces so their SharePoint internal names remain predictable.

| Column | SharePoint type | Purpose |
|---|---|---|
| `EmailSubject` | Single line of text | Original email subject |
| `EmailFromName` | Single line of text | Sender display name, when available |
| `EmailFromAddress` | Single line of text | Sender email address |
| `EmailTo` | Multiple lines of text (plain text) | To recipients |
| `EmailCc` | Multiple lines of text (plain text) | Cc recipients |
| `EmailReceivedAt` | Date and time | Original received timestamp |
| `EmailMessageId` | Single line of text | Outlook connector message ID |
| `EmailInternetMessageId` | Single line of text | RFC/Internet message ID, when available |
| `EmailConversationId` | Single line of text | Outlook conversation ID |
| `EmailBodyPreview` | Multiple lines of text (plain text) | Original body preview; avoid storing active HTML |
| `SourceMailbox` | Single line of text | Mailbox/distribution-list address that received the email |

Do not make these columns required. This lets the app continue to show files captured before metadata was added.

## Power Automate flow change

Immediately after **Create file**, add **SharePoint → Update file properties**:

- Site Address: `Communication site`
- Library Name: `Invoice Automation`
- Id: the **ItemId** returned by **Create file**

Map the columns from **When a new email arrives (V3)**:

- `EmailSubject` ← Subject
- `EmailFromName` ← From (Name), if the trigger exposes it
- `EmailFromAddress` ← From
- `EmailTo` ← To
- `EmailCc` ← CC
- `EmailReceivedAt` ← Received time
- `EmailMessageId` ← Message Id
- `EmailInternetMessageId` ← Internet Message Id, if exposed; otherwise leave blank
- `EmailConversationId` ← Conversation Id
- `EmailBodyPreview` ← Body Preview
- `SourceMailbox` ← `invoices-evesham@nationwideproduce.com`

The update action belongs inside the attachment loop and after the PDF condition, so each created PDF receives the parent email's metadata.

## Existing files

Do not delete or recreate the current Incoming library as a first step. Validate the enhanced flow with one new test email first.

For the existing backlog, use a temporary backfill flow that reads the original emails, matches each PDF attachment to its SharePoint file, and updates the same columns. Filename alone is not a safe permanent key because duplicate attachment names are possible. The durable design should also store the Exchange message ID plus attachment identity/hash in SQL for deduplication and audit.
