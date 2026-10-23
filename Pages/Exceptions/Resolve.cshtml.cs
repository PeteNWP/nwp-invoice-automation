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
    private readonly UserAccessService _userAccess;

    public ResolveModel(IDocumentStore docs, SupplierProfileStore profiles, INwpSupplierDirectory supplierDirectory, UserAccessService userAccess)
    {
        _docs = docs;
        _profiles = profiles;
        _supplierDirectory = supplierDirectory;
        _userAccess = userAccess;
    }

    public CapturedDocument? Document { get; private set; }
    public IReadOnlyList<NwpSupplier> Suppliers { get; private set; } = [];
    public bool Saved { get; private set; }

    [BindProperty] public InputModel Input { get; set; } = new();

    public async Task<IActionResult> OnGetAsync(Guid id, bool saved = false, CancellationToken cancellationToken = default)
    {
        await LoadAsync(id, cancellationToken);
        Saved = saved;
        if (Document is null) return Page();
        Input = InputModel.FromDocument(Document, Suppliers);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(Guid id, CancellationToken cancellationToken)
    {
        await LoadAsync(id, cancellationToken);
        if (Document is null) return Page();

        var supplier = string.IsNullOrWhiteSpace(Input.NwpSupplierOid)
            ? null
            : Suppliers.FirstOrDefault(s => string.Equals(s.Oid, Input.NwpSupplierOid, StringComparison.Ordinal));
        if (supplier is null)
            ModelState.AddModelError("Input.NwpSupplierOid", "Select a supplier from the live NWP supplier master.");
        if (!ModelState.IsValid) return Page();

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
            AssignedQueue = Input.AssignedQueue, WorkflowOwner = Input.WorkflowOwner?.Trim(),
            TaughtSupplierAlias = Input.TeachSupplierAlias, TaughtNestedEmailRule = Input.TeachNestedEmailRule,
            TaughtMailboxRoute = Input.TeachMailboxRoute, TaughtExtractionPattern = Input.TeachExtractionPattern,
            ObservedSupplierName = Document.DetectedSupplier?.Trim(), ObservedSender = sender,
            ObservedSenderDomain = at >= 0 && at < sender!.Length - 1 ? sender[(at + 1)..].ToLowerInvariant() : null,
            ResolutionNote = Input.ResolutionNote?.Trim()
        };

        _profiles.TeachFromResolution(resolution, Document);
        await _docs.ResolveExceptionAsync(id, resolution, cancellationToken);
        return RedirectToPage("/Exceptions/Resolve", new { id, saved = true });
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
            return new InputModel
            {
                NwpSupplierOid = selected?.Oid ?? "", SupplierSearchLabel = selected?.DisplayLabel ?? "",
                DocumentType = document.DocumentType, InvoiceNumber = document.InvoiceNumber, InvoiceDate = document.InvoiceDate,
                BatchOrReference = document.BatchOrReference, Total = document.Total, Currency = document.Currency ?? "GBP",
                AssignedQueue = document.DocumentType == DocumentType.Statement ? "Statements" : "Invoice Queue",
                WorkflowOwner = document.WorkflowOwner,
                TeachSupplierAlias = false,
                TeachNestedEmailRule = document.IsNestedEmail,
                TeachMailboxRoute = !string.Equals(document.OriginalMailbox, "invoices-evesham@nationwideproduce.com", StringComparison.OrdinalIgnoreCase),
                ResolutionNote = document.ReviewerNote
            };
        }
    }
}
