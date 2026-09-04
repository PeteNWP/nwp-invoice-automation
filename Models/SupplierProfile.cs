namespace Nwp.InvoiceAutomation.Web.Models;

/// <summary>
/// Placeholder for the supplier-learning capture profile. The real system will hold
/// deterministic extraction rules (field positions, table bounds, tolerances, layout
/// fingerprint). For the shell we keep just enough to show the Supplier Profiles page.
/// </summary>
public sealed class SupplierProfile
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Name as it appears in the NWP supplier master.</summary>
    public string MasterName { get; init; } = "";

    /// <summary>Alternate names/branding seen on documents (e.g. "GMF" -> Green Mushroom Farm BV).</summary>
    public List<string> Aliases { get; init; } = new();

    public List<string> KnownSenders { get; init; } = new();

    public List<string> KnownMailboxes { get; init; } = new();

    public string? UsualFileType { get; init; }

    /// <summary>Who handles this supplier's invoices (null = Maeve's standard print queue).</summary>
    public string? WorkflowOwner { get; set; }

    public bool SendsNestedEmail { get; set; }

    /// <summary>True once extraction rules have been taught; false = capture only for now.</summary>
    public bool HasExtractionRules { get; set; }

    public string? Notes { get; init; }
}
