using Microsoft.Extensions.Options;
using System.Security.Claims;

namespace Nwp.InvoiceAutomation.Web.Services;

public sealed class UserAccessService
{
    private readonly IOptionsMonitor<AccessOptions> _options;

    public UserAccessService(IOptionsMonitor<AccessOptions> options) => _options = options;

    public bool IsAdmin(ClaimsPrincipal user)
    {
        var email = GetEmail(user);
        if (string.IsNullOrWhiteSpace(email)) return false;

        return _options.CurrentValue.AdminEmails.Any(adminEmail =>
            string.Equals(adminEmail, email, StringComparison.OrdinalIgnoreCase));
    }

    public string? GetEmail(ClaimsPrincipal user) =>
        user.FindFirst("preferred_username")?.Value
        ?? user.FindFirst(ClaimTypes.Email)?.Value
        ?? user.Identity?.Name;
}
