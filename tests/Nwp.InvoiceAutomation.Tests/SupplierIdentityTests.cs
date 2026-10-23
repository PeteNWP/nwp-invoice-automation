using Nwp.InvoiceAutomation.Web.Models;
using Nwp.InvoiceAutomation.Web.Pages.Exceptions;
using Xunit;

namespace Nwp.InvoiceAutomation.Tests;

public sealed class SupplierIdentityTests
{
    [Fact]
    public void DisplayLabel_ContainsReferenceAndOfficialName()
    {
        var supplier = new NwpSupplier("42", "SUP-7", "Official Produce Ltd", null, null, null, "GBP");
        Assert.Equal("SUP-7 — Official Produce Ltd", supplier.DisplayLabel);
    }

    [Fact]
    public void ReviewInput_OnlyPreselectsPreviouslyApprovedStableIdentity()
    {
        var directory = new[] { new NwpSupplier("42", "SUP-7", "Official Produce Ltd", null, null, null, "GBP") };
        var unapproved = new CapturedDocument { DetectedSupplier = "Produce Trading", SupplierMasterName = "Official Produce Ltd" };
        var approved = new CapturedDocument { DetectedSupplier = "Produce Trading", SupplierMasterName = "Official Produce Ltd", NwpSupplierOid = "42" };

        var first = ResolveModel.InputModel.FromDocument(unapproved, directory);
        var second = ResolveModel.InputModel.FromDocument(approved, directory);

        Assert.Empty(first.NwpSupplierOid);
        Assert.Empty(first.SupplierSearchLabel);
        Assert.Equal("42", second.NwpSupplierOid);
        Assert.Equal("SUP-7 — Official Produce Ltd", second.SupplierSearchLabel);
    }
}
