using Nwp.InvoiceAutomation.Web.Models;

namespace Nwp.InvoiceAutomation.Web.Services;

/// <summary>
/// Placeholder supplier profiles derived from the discovery examples. No extraction rules
/// are taught yet — this exists so the Supplier Profiles page shows the intended shape of
/// the supplier-learning system.
/// </summary>
public sealed class SupplierProfileStore
{
    private readonly List<SupplierProfile> _profiles = new()
    {
        new SupplierProfile
        {
            MasterName = "Green Mushroom Farm BV",
            Aliases = { "GMF" },
            UsualFileType = "PDF (often sent as application/octet-stream)",
            Notes = "Document branding differs from master name — alias mapping required."
        },
        new SupplierProfile
        {
            MasterName = "Growers Direct Ltd",
            KnownSenders = { "accounts@growersdirect.example" },
            UsualFileType = "PDF",
            Notes = "Sends both invoices and account statements. Customer Order Number carries the NWP reference."
        },
        new SupplierProfile
        {
            MasterName = "Morgan Cargo Ltd",
            UsualFileType = "PDF",
            WorkflowOwner = "Handled by another team (not Maeve's print queue)",
            Notes = "Freight/import charges. Route to the owning team, not the standard invoice queue."
        },
        new SupplierProfile
        {
            MasterName = "South Eastern Produce Ltd",
            UsualFileType = "PDF",
            Notes = "Sometimes arrives late/overdue and via internal forwarding."
        },
        new SupplierProfile
        {
            MasterName = "J N Fox & Sons",
            UsualFileType = "PDF (Fresho)",
            Notes = "Has been sent to accounts@ instead of the invoices mailbox."
        }
    };

    public IReadOnlyList<SupplierProfile> All() => _profiles;
}
