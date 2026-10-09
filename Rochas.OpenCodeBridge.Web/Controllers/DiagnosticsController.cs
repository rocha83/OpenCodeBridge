using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Rochas.OpenCodeBridge.Web.Services;

namespace Rochas.OpenCodeBridge.Web.Controllers;

// Diagnóstico: snapshots do ServiceObserver + contagem de atividades (modo diagnóstico).
[Authorize]
public sealed class DiagnosticsController(IDiagnosticTelemetry telemetry) : Controller
{
    [HttpGet("/Chat/Diagnostics")]
    public IActionResult Snapshot() => Json(telemetry.Snapshot());
}
