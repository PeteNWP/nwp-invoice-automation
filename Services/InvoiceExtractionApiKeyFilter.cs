using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Nwp.InvoiceAutomation.Web.Services;

public sealed class InvoiceExtractionApiKeyFilter : IAuthorizationFilter
{
    private readonly IConfiguration _configuration;
    public InvoiceExtractionApiKeyFilter(IConfiguration configuration) => _configuration = configuration;

    public void OnAuthorization(AuthorizationFilterContext context)
    {
        var expected = _configuration["InvoiceExtractionApi:ApiKey"];
        var valid = !string.IsNullOrEmpty(expected)
            && context.HttpContext.Request.Headers.TryGetValue("X-Invoice-Automation-Key", out var values)
            && values.Count == 1
            && !string.IsNullOrEmpty(values[0])
            && CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(Encoding.UTF8.GetBytes(expected)),
                SHA256.HashData(Encoding.UTF8.GetBytes(values[0]!)));
        if (!valid) context.Result = new UnauthorizedResult();
    }
}
