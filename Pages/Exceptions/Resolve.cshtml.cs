using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Nwp.InvoiceAutomation.Web.Models;
using Nwp.InvoiceAutomation.Web.Services;

namespace Nwp.InvoiceAutomation.Web.Pages.Exceptions;

public sealed class ResolveModel : PageModel
{
    private readonly IDocumentStore _docs;
    private readonly SupplierProfileStore _profiles;
    private readonly INwpSupplierDirectory _supplierDirectory;
    private readonly IInvoiceExtractionRepository _extractions;
    private readonly UserAccessService _userAccess;
    private readonly ManualInvoiceReprocessor _reprocessor;
    private readonly ILogger<ResolveModel> _logger;

    public ResolveModel(IDocumentStore docs, SupplierProfileStore profiles, INwpSupplierDirectory supplierDirectory,
        IInvoiceExtractionRepository extractions, UserAccessService userAccess,
        ManualInvoiceReprocessor reprocessor, ILogger<ResolveModel> logger)
    {
        _docs = docs;
        _profiles = profiles;
        _supplierDirectory = supplierDirectory;
        _extractions = extractions;
        _userAccess = userAccess;
        _reprocessor = reprocessor;
        _logger = logger;
    }

    public CapturedDocument? Document { get; private set; }
    public IReadOnlyList<NwpSupplier> Suppliers { get; private set; } = [];
    public bool Saved { get; private set; }
    public bool Reviewed { get; private set; }
    [TempData] public string? ReprocessSuccess { get; set; }
    [TempData] public string? ReprocessError { get; set; }

    [BindProperty] public InputModel Input { get; set; } = new();

    public async Task<IActionResult> OnGetAsync(Guid id, bool saved = false, bool reviewed = false, CancellationToken cancellationToken = default)
    {
        await LoadAsync(id, cancellationToken);
        Saved = saved;
        Reviewed = reviewed;
        if (Document is null) return Page();
        Input = InputModel.FromDocument(Document, Suppliers);
        return Page();
    }

    public async Task<IActionResult> OnPostReprocessAsync(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _reprocessor.ReprocessAsync(id, cancellationToken);
            ReprocessSuccess = result.Outcome switch
            {
                ProcessingOutcome.Extracted => "AI reprocessing completed. The extracted fields and raw response below are fresh.",
                ProcessingOutcome.Skipped => $"AI reprocessing completed, but classified this document as {result.ClassifiedDocumentType ?? "non-invoice"}.",
                _ => result.Error ?? "AI reprocessing did not produce a usable invoice response."
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            ReprocessError = ex.Message;
            _logger.LogWarning(ex, "Manual AI reprocessing failed for captured document {DocumentId}.", id);
        }

        return RedirectToPage("/Exceptions/Resolve", new { id });
    }

    public async Task<IActionResult> OnPostAsync(Guid id, string action, CancellationToken cancellationToken)
    {
        await LoadAsync(id, cancellationToken);
        if (Document is null) return Page();

        var supplier = string.IsNullOrWhiteSpace(Input.NwpSupplierOid)
            ? null
            : Suppliers.FirstOrDefault(s => string.Equals(s.Oid, Input.NwpSupplierOid, StringComparison.Ordinal));
        if (supplier is null)
            ModelState.AddModelError("Input.NwpSupplierOid", "Select a supplier from the live NWP supplier master.");
        if (!ModelState.IsValid) return Page();

        var approve = string.Equals(action, "approve", StringComparison.OrdinalIgnoreCase);
        if (Document.Extraction is { } extraction)
        {
            try
            {
                await _extractions.SaveReviewAsync(new InvoiceExtractionReview
                {
                    Id = extraction.Id, ExpectedRevision = Input.ExtractionRevision,
                    SupplierName = Input.ObservedSupplierName, InvoiceNumber = Input.InvoiceNumber,
                    InvoiceDate = Input.InvoiceDate, Reference = Input.BatchOrReference,
                    Total = Input.Total, Currency = Input.Currency,
                    ReviewedBy = _userAccess.GetEmail(User) ?? "Demo user", ReviewNote = Input.ResolutionNote,
                    Approve = approve,
                    Lines = Input.Lines.Select((line, index) => new InvoiceExtractionLineRequest
                    {
                        LineNumber = index + 1, Sku = line.Sku, Description = line.Description,
                        Quantity = line.Quantity, UnitPrice = line.UnitPrice, LineTotal = line.LineTotal
                    }).ToList()
                }, cancellationToken);
            }
            catch (InvoiceExtractionConcurrencyException ex)
            {
                ModelState.AddModelError(string.Empty, ex.Message);
                return Page();
            }
        }

        var sender = Document.FromAddress?.Trim();
        var at = sender?.LastIndexOf('@') ?? -1;
        var resolution = new ExceptionResolution
        {
            DocumentId = id,
            ResolvedBy = _userAccess.GetEmail(User) ?? "Demo user",
            NwpSupplierOid = supplier!.Oid,
            NwpSupplierReference = supplier.Reference,
            SupplierMasterName = supplier.Name,
            DocumentType = Input.DocumentType,
            InvoiceNumber = Input.InvoiceNumber?.Trim(), InvoiceDate = Input.InvoiceDate,
            BatchOrReference = Input.BatchOrReference?.Trim(), Total = Input.Total,
            Currency = string.IsNullOrWhiteSpace(Input.Currency) ? "GBP" : Input.Currency.Trim().ToUpperInvariant(),
            AssignedQueue = Input.AssignedQueue, MarkReviewed = approve, WorkflowOwner = Input.WorkflowOwner?.Trim(),
            TaughtSupplierAlias = Input.TeachSupplierAlias, TaughtNestedEmailRule = Input.TeachNestedEmailRule,
            TaughtMailboxRoute = Input.TeachMailboxRoute, TaughtExtractionPattern = Input.TeachExtractionPattern,
            ObservedSupplierName = Input.ObservedSupplierName?.Trim(), ObservedSender = sender,
            ObservedSenderDomain = at >= 0 && at < sender!.Length - 1 ? sender[(at + 1)..].ToLowerInvariant() : null,
            ResolutionNote = Input.ResolutionNote?.Trim()
        };

        _profiles.TeachFromResolution(resolution, Document);
        await _docs.ResolveExceptionAsync(id, resolution, cancellationToken);
        return RedirectToPage("/Exceptions/Resolve", new { id, saved = true, reviewed = approve });
    }

    private async Task LoadAsync(Guid id, CancellationToken cancellationToken)
    {
        Document = await _docs.GetAsync(id, cancellationToken);
        Suppliers = await _supplierDirectory.GetAllAsync(cancellationToken);
    }

    public sealed class InputModel
    {
        [Required, Display(Name = "Supplier master")] public string NwpSupplierOid { get; set; } = "";
        public string SupplierSearchLabel { get; set; } = "";
        public long ExtractionRevision { get; set; }
        [Display(Name = "Observed supplier name")] public string? ObservedSupplierName { get; set; }
        public List<LineInput> Lines { get; set; } = [];
        [Display(Name = "Document type")] public DocumentType DocumentType { get; set; } = DocumentType.Invoice;
        [Display(Name = "Invoice number")] public string? InvoiceNumber { get; set; }
        [Display(Name = "Invoice date")] public DateOnly? InvoiceDate { get; set; }
        [Display(Name = "Batch / reference")] public string? BatchOrReference { get; set; }
        public decimal? Total { get; set; }
        public string Currency { get; set; } = "GBP";
        [Required, Display(Name = "Move to queue")] public string AssignedQueue { get; set; } = "Invoice Queue";
        [Display(Name = "Workflow owner")] public string? WorkflowOwner { get; set; }
        public bool TeachSupplierAlias { get; set; }
        public bool TeachNestedEmailRule { get; set; }
        public bool TeachMailboxRoute { get; set; }
        public bool TeachExtractionPattern { get; set; }
        [Display(Name = "Resolution note")] public string? ResolutionNote { get; set; }

        public static InputModel FromDocument(CapturedDocument document, IReadOnlyList<NwpSupplier> suppliers)
        {
            var selected = string.IsNullOrWhiteSpace(document.NwpSupplierOid) ? null
                : suppliers.FirstOrDefault(s => string.Equals(s.Oid, document.NwpSupplierOid, StringComparison.Ordinal));
            var extraction = document.Extraction;
            return new InputModel
            {
                NwpSupplierOid = selected?.Oid ?? "", SupplierSearchLabel = selected?.DisplayLabel ?? "",
                ExtractionRevision = extraction?.ReviewRevision ?? 0,
                ObservedSupplierName = extraction?.SupplierName ?? document.DetectedSupplier,
                Lines = extraction?.Lines.Select(l => new LineInput { Sku=l.Sku, Description=l.Description, Quantity=l.Quantity, UnitPrice=l.UnitPrice, LineTotal=l.LineTotal }).ToList() ?? [],
                DocumentType = document.DocumentType, InvoiceNumber = extraction?.InvoiceNumber ?? document.InvoiceNumber, InvoiceDate = extraction?.InvoiceDate ?? document.InvoiceDate,
                BatchOrReference = extraction?.Reference ?? document.BatchOrReference, Total = extraction?.Total ?? document.Total, Currency = extraction?.Currency ?? document.Currency ?? "GBP",
                AssignedQueue = document.DocumentType == DocumentType.Statement ? "Statements" : "Invoice Queue",
                WorkflowOwner = document.WorkflowOwner,
                TeachSupplierAlias = false,
                TeachNestedEmailRule = document.IsNestedEmail,
                TeachMailboxRoute = !string.Equals(document.OriginalMailbox, "invoices-evesham@nationwideproduce.com", StringComparison.OrdinalIgnoreCase),
                ResolutionNote = document.ReviewerNote
            };
        }
    }

    public sealed class LineInput
    {
        [StringLength(100)] public string? Sku { get; set; }
        [Required, StringLength(1000)] public string Description { get; set; } = "";
        public decimal? Quantity { get; set; }
        public decimal? UnitPrice { get; set; }
        public decimal? LineTotal { get; set; }
    }
}
