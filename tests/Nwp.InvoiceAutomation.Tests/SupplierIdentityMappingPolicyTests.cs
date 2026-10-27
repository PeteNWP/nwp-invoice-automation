using Nwp.InvoiceAutomation.Web.Models;
using Nwp.InvoiceAutomation.Web.Services;
using Xunit;

namespace Nwp.InvoiceAutomation.Tests;

public sealed class SupplierIdentityMappingPolicyTests
{
    [Fact]
    public void Backfill_AppliesToMatchingOpenUnreviewedInvoice()
    {
        Assert.True(SupplierIdentityMappingPolicy.IsMatch("  GMF PRODUCE ", "gmf produce"));
        Assert.True(SupplierIdentityMappingPolicy.CanApply(
            false, ClassificationStatus.ReviewRequired, null, ExtractionReviewStatus.Pending,
            null, null, null, allowReplacingAutomaticMapping: true));
    }

    [Fact]
    public void FutureMapping_AppliesOnlyWhenAuthoritativeIdentityIsEmpty()
    {
        Assert.True(SupplierIdentityMappingPolicy.CanApply(
            false, null, null, ExtractionReviewStatus.Pending, null, null, null));
        Assert.False(SupplierIdentityMappingPolicy.CanApply(
            false, null, null, ExtractionReviewStatus.Pending, "Explicit Supplier", "42", "SUP-42"));
    }

    [Theory]
    [InlineData(ClassificationStatus.Classified, ExtractionReviewStatus.Approved, false, false)]
    [InlineData(ClassificationStatus.Ignored, ExtractionReviewStatus.Pending, false, false)]
    [InlineData(ClassificationStatus.ReviewRequired, ExtractionReviewStatus.Corrected, false, false)]
    [InlineData(ClassificationStatus.ReviewRequired, ExtractionReviewStatus.Pending, true, false)]
    public void ReviewedIgnoredAndDeletedHistory_IsProtected(
        ClassificationStatus status, ExtractionReviewStatus reviewStatus, bool deleted, bool expected)
    {
        Assert.Equal(expected, SupplierIdentityMappingPolicy.CanApply(
            deleted, status, null, reviewStatus, null, null, null, allowReplacingAutomaticMapping: true));
    }

    [Fact]
    public void UnmatchedAlias_DoesNotResolve()
    {
        Assert.False(SupplierIdentityMappingPolicy.IsMatch("GMF Produce", "Green Mushroom Farm"));
        Assert.False(SupplierIdentityMappingPolicy.IsMatch(null, "Green Mushroom Farm"));
    }
}
