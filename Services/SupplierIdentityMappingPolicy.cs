using Nwp.InvoiceAutomation.Web.Models;

namespace Nwp.InvoiceAutomation.Web.Services;

/// <summary>Shared, testable guard rails mirrored by the SQL supplier-identity updates.</summary>
public static class SupplierIdentityMappingPolicy
{
    public static string? Normalize(string? observedName) =>
        string.IsNullOrWhiteSpace(observedName) ? null : observedName.Trim().ToLowerInvariant();

    public static bool IsMatch(string? observedName, string? mappedAlias) =>
        Normalize(observedName) is { } observed
        && string.Equals(observed, Normalize(mappedAlias), StringComparison.Ordinal);

    public static bool CanApply(
        bool isDeleted, ClassificationStatus? status, DateTimeOffset? resolvedAt,
        ExtractionReviewStatus reviewStatus, string? supplierName, string? supplierOid,
        string? supplierReference, bool allowReplacingAutomaticMapping = false) =>
        !isDeleted
        && resolvedAt is null
        && status is null or ClassificationStatus.Unclassified or ClassificationStatus.Exception or ClassificationStatus.ReviewRequired
        && reviewStatus == ExtractionReviewStatus.Pending
        && (allowReplacingAutomaticMapping
            || (string.IsNullOrWhiteSpace(supplierName)
                && string.IsNullOrWhiteSpace(supplierOid)
                && string.IsNullOrWhiteSpace(supplierReference)));
}
