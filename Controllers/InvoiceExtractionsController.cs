using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nwp.InvoiceAutomation.Web.Models;
using Nwp.InvoiceAutomation.Web.Services;

namespace Nwp.InvoiceAutomation.Web.Controllers;

[ApiController]
[AllowAnonymous]
[Route("api/invoice-extractions")]
[RequestSizeLimit(1_048_576)]
[ServiceFilter(typeof(InvoiceExtractionApiKeyFilter))]
public sealed class InvoiceExtractionsController : ControllerBase
{
    private const int MaximumLines = 500;
    private readonly IInvoiceExtractionRepository _repository;
    public InvoiceExtractionsController(IInvoiceExtractionRepository repository) => _repository = repository;

    [HttpPost]
    [Consumes("application/json")]
    public async Task<IActionResult> Post(InvoiceExtractionRequest request, CancellationToken cancellationToken)
    {
        if (request.Lines is null)
        {
            ModelState.AddModelError(nameof(request.Lines), "Lines are required.");
            return ValidationProblem(ModelState);
        }
        if (request.Lines.Count > MaximumLines)
            ModelState.AddModelError(nameof(request.Lines), $"At most {MaximumLines} lines are allowed.");
        if (request.Lines.Select(x => x.LineNumber).Distinct().Count() != request.Lines.Count)
            ModelState.AddModelError(nameof(request.Lines), "Line numbers must be unique.");
        if (string.IsNullOrWhiteSpace(request.DriveId))
            ModelState.AddModelError(nameof(request.DriveId), "DriveId cannot be blank.");
        if (string.IsNullOrWhiteSpace(request.DriveItemId))
            ModelState.AddModelError(nameof(request.DriveItemId), "DriveItemId cannot be blank.");
        if (request.Lines.Any(x => string.IsNullOrWhiteSpace(x.Description)))
            ModelState.AddModelError(nameof(request.Lines), "Line descriptions cannot be blank.");
        if (!ModelState.IsValid) return ValidationProblem(ModelState);

        try
        {
            var result = await _repository.UpsertAsync(request, cancellationToken);
            return result.Created
                ? StatusCode(StatusCodes.Status201Created, result)
                : Ok(result);
        }
        catch (InvalidOperationException)
        {
            return Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Invoice extraction storage is unavailable.");
        }
        catch (Microsoft.Data.SqlClient.SqlException)
        {
            return Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Invoice extraction storage is unavailable.");
        }
    }

}
