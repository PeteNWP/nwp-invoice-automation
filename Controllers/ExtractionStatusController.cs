using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nwp.InvoiceAutomation.Web.Services;

namespace Nwp.InvoiceAutomation.Web.Controllers;

/// <summary>
/// Read-only health view of the extraction worker. Hit <c>/api/extraction-status</c> in a
/// browser to see whether the worker is enabled and what it last did. No secrets are exposed.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("api/extraction-status")]
public sealed class ExtractionStatusController : ControllerBase
{
    private readonly ExtractionWorkerState _state;
    public ExtractionStatusController(ExtractionWorkerState state) => _state = state;

    [HttpGet]
    public IActionResult Get() => Ok(new
    {
        _state.Enabled,
        _state.DisabledReason,
        _state.StartedAtUtc,
        _state.LastCycleAtUtc,
        _state.LastCycleClaimed,
        _state.TotalExtracted,
        _state.TotalSkipped,
        _state.TotalFailed,
        _state.LastExtractedFile,
        _state.LastError
    });
}
