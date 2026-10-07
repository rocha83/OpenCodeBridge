using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using System.IO;

namespace Rochas.OpenCodeBridge.Web.Controllers;

[AllowAnonymous]
[Route("iso")]
public sealed class IsoController : Controller
{
    // GET /iso/lmde-7-cinnamon-64bit.iso
    [HttpGet("lmde-7-cinnamon-64bit.iso")]
    public IActionResult DownloadLmde()
    {
        var path = "/media/mint/3686C649614854E6/VMachine/lmde-7-cinnamon-64bit.iso";
        if (!System.IO.File.Exists(path))
            return NotFound();
        
        return PhysicalFile(path, "application/octet-stream", "lmde-7-cinnamon-64bit.iso", enableRangeProcessing: true);
    }
}
