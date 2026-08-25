namespace Nwp.InvoiceAutomation.Web.Models;

/// <summary>
/// First-pass classification of an item that arrived in the invoice mailbox.
/// Derived from the 9 real forwarded examples in the NWP Inv Auto discovery pack.
/// </summary>
public enum DocumentType
{
    Unknown = 0,
    Invoice = 1,
    Statement = 2,
    OrderAcknowledgement = 3,
    EnsCustomsSecurity = 4,
    Other = 5
}

/// <summary>Where the item currently sits in the human workflow.</summary>
public enum ClassificationStatus
{
    /// <summary>Awaiting a human or rule to confirm the document type.</summary>
    Unclassified = 0,
    /// <summary>Classified and routed to a working queue.</summary>
    Classified = 1,
    /// <summary>Held in the exceptions queue for a specific reason.</summary>
    Exception = 2,
    /// <summary>Deliberately not printed/captured (e.g. acknowledgements, ENS docs).</summary>
    Ignored = 3
}
