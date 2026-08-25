using Nwp.InvoiceAutomation.Web.Models;

namespace Nwp.InvoiceAutomation.Web.Services;

/// <summary>
/// Seeds the shell app with the 9 real forwarded examples from the discovery pack so the
/// classifier, queues and exception views have realistic data to render. No real mailbox
/// access here — this is the placeholder that deterministic capture will replace.
/// </summary>
public sealed class MockDocumentStore : IDocumentStore
{
    private readonly List<CapturedDocument> _docs = Seed();

    public IReadOnlyList<CapturedDocument> All() => _docs;

    public CapturedDocument? Get(Guid id) => _docs.FirstOrDefault(d => d.Id == id);

    public IReadOnlyList<CapturedDocument> ByType(DocumentType type) =>
        _docs.Where(d => d.DocumentType == type).ToList();

    public IReadOnlyList<CapturedDocument> ByStatus(ClassificationStatus status) =>
        _docs.Where(d => d.Status == status).ToList();

    private static List<CapturedDocument> Seed()
    {
        const string primary = "invoices-evesham@nationwideproduce.com";

        return new List<CapturedDocument>
        {
            // 1. Food Heroes order acknowledgement — do not print.
            new()
            {
                Subject = "Fw: The Food Heroes Marketing Ltd Order Acknowledgement - 0000055137",
                OriginalMailbox = primary,
                ReceivedDate = new DateOnly(2026, 8, 18),
                AttachmentName = "Order Acknowledgement - 0000055137.pdf",
                AttachmentMime = "application/pdf",
                DocumentType = DocumentType.OrderAcknowledgement,
                Status = ClassificationStatus.Ignored,
                DetectedSupplier = "The Food Heroes Marketing Ltd",
                BatchOrReference = "492TSE",
                Total = 1705.00m,
                Currency = "GBP",
                ReviewerNote = "Would not print — order acknowledgement, not an invoice."
            },

            // 2. GMF digital invoice — print; needs alias mapping + MIME sniffing.
            new()
            {
                Subject = "Fw: Digitale factuur",
                OriginalMailbox = primary,
                ReceivedDate = new DateOnly(2026, 8, 18),
                AttachmentName = "4840371_1405DigFactuur_V101.pdf",
                AttachmentMime = "application/octet-stream",
                DocumentType = DocumentType.Invoice,
                Status = ClassificationStatus.Exception,
                ExceptionReason = "Supplier alias unresolved: document branded 'GMF', maps to Green Mushroom Farm BV.",
                DetectedSupplier = "GMF",
                SupplierMasterName = "Green Mushroom Farm BV",
                InvoiceNumber = "FAK2603197",
                InvoiceDate = new DateOnly(2026, 8, 17),
                BatchOrReference = "PO24483nwe",
                Total = 2400.00m,
                Currency = "GBP",
                ReviewerNote = "Would print. Supplier is 'Green Mushroom Farm BV' in our system, not 'GMF'."
            },

            // 3. Growers Direct account statement — separate statements queue.
            new()
            {
                Subject = "Fw: Customer Account Statement",
                OriginalMailbox = primary,
                ReceivedDate = new DateOnly(2026, 8, 19),
                AttachmentName = "Attachment.pdf",
                AttachmentMime = "application/pdf",
                DocumentType = DocumentType.Statement,
                Status = ClassificationStatus.Classified,
                AssignedQueue = "Statements",
                DetectedSupplier = "Growers Direct Ltd",
                InvoiceDate = new DateOnly(2026, 8, 19),
                Total = 16344.00m,
                Currency = "GBP",
                ReviewerNote = "Statements come in too — asked for these to be separated into their own file."
            },

            // 4. Growers Direct normal invoice.
            new()
            {
                Subject = "Fw: Invoice from Growers Direct",
                OriginalMailbox = primary,
                ReceivedDate = new DateOnly(2026, 8, 18),
                AttachmentName = "Attachment.pdf",
                AttachmentMime = "application/pdf",
                DocumentType = DocumentType.Invoice,
                Status = ClassificationStatus.Classified,
                AssignedQueue = "Invoice Queue",
                DetectedSupplier = "Growers Direct Ltd",
                SupplierMasterName = "Growers Direct Ltd",
                InvoiceNumber = "100035",
                InvoiceDate = new DateOnly(2026, 8, 18),
                BatchOrReference = "GD / 24274NWE-N",
                Total = 2912.00m,
                Currency = "GBP"
            },

            // 5. Invoice nested inside a forwarded .eml attachment.
            new()
            {
                Subject = "Fw: INVOICE 434358",
                OriginalMailbox = primary,
                ReceivedDate = new DateOnly(2026, 8, 18),
                AttachmentName = "INVOICE 434358.eml (contains QPRINT1#splf.pdf)",
                AttachmentMime = "message/rfc822",
                IsNestedEmail = true,
                DocumentType = DocumentType.Invoice,
                Status = ClassificationStatus.Exception,
                ExceptionReason = "Invoice PDF nested inside an attached .eml — needs recursive email parsing.",
                InvoiceNumber = "434358",
                InvoiceDate = new DateOnly(2026, 8, 18),
                BatchOrReference = "DN9904 24361NWE/N",
                Total = 2860.00m,
                Currency = "GBP",
                ReviewerNote = "Invoice comes as an email attachment with the PDF inside. Two suppliers do this."
            },

            // 6. Morgan Cargo invoice handled by someone else.
            new()
            {
                Subject = "Fw: Invoice: SINV052290",
                OriginalMailbox = primary,
                ReceivedDate = new DateOnly(2026, 8, 17),
                AttachmentName = "INVOICE.pdf",
                AttachmentMime = "application/pdf",
                DocumentType = DocumentType.Invoice,
                Status = ClassificationStatus.Classified,
                AssignedQueue = "Other owner",
                WorkflowOwner = "Handled by another team",
                DetectedSupplier = "MORGAN CARGO LTD",
                SupplierMasterName = "Morgan Cargo Ltd",
                InvoiceNumber = "SINV052290",
                InvoiceDate = new DateOnly(2026, 8, 17),
                BatchOrReference = "SFM-260816-02",
                ReviewerNote = "Morgan Cargo invoices are not printed by Maeve — someone else picks them up."
            },

            // 7. Spanish ENS / security document — not an invoice.
            new()
            {
                Subject = "Fw: documentación para la carga de 17-08-2026 (albarán y factura)",
                OriginalMailbox = primary,
                ReceivedDate = new DateOnly(2026, 8, 18),
                AttachmentName = "SSD-39502378-20260818-1501.pdf",
                AttachmentMime = "application/pdf",
                DocumentType = DocumentType.EnsCustomsSecurity,
                Status = ClassificationStatus.Ignored,
                ReviewerNote = "Would not print — this is an ENS / security document, despite 'factura' in the subject."
            },

            // 8. Overdue invoice routed internally via Lorna.
            new()
            {
                Subject = "Fw: Overdue invoice",
                OriginalMailbox = primary,
                ForwardedBy = "Lorna",
                ReceivedDate = new DateOnly(2026, 8, 24),
                AttachmentName = "NATIONWIDE PRODUCE PLC_Invoice 33330.pdf",
                AttachmentMime = "application/pdf",
                DocumentType = DocumentType.Invoice,
                Status = ClassificationStatus.Classified,
                AssignedQueue = "Invoice Queue",
                DetectedSupplier = "South Eastern Produce Ltd",
                SupplierMasterName = "South Eastern Produce Ltd",
                InvoiceNumber = "33330",
                InvoiceDate = new DateOnly(2026, 6, 22),
                BatchOrReference = "21506NWE",
                Total = 1890.00m,
                Currency = "GBP",
                ReviewerNote = "Went to Lorna directly, who forwarded it on. Invoice date is much older than received date."
            },

            // 9. J N Fox invoice sent to the wrong mailbox.
            new()
            {
                Subject = "Fw: Invoice F55011836 - WED 17/6 - J N Fox & Sons",
                OriginalMailbox = "accounts@nationwideproduce.com",
                ReceivedDate = new DateOnly(2026, 8, 18),
                AttachmentName = "FreshoInvoice#F55011836.pdf",
                AttachmentMime = "application/pdf",
                DocumentType = DocumentType.Invoice,
                Status = ClassificationStatus.Exception,
                ExceptionReason = "Wrong mailbox: arrived at accounts@, not invoices-evesham@.",
                DetectedSupplier = "J N Fox & Sons",
                SupplierMasterName = "J N Fox & Sons",
                InvoiceNumber = "F55011836",
                InvoiceDate = new DateOnly(2026, 6, 17),
                Total = 810.00m,
                Currency = "GBP",
                ReviewerNote = "Went to accounts@nationwideproduce.com, not the invoices mailbox."
            }
        };
    }
}
