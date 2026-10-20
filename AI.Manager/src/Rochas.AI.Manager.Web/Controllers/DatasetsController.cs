using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Rochas.AI.Manager.Domain.Interfaces;
using System.IO;
using System.Security.Cryptography;

namespace Rochas.AI.Manager.Web.Controllers;

[Authorize]
public sealed class DatasetsController(IDatasetService svc, IWebHostEnvironment env) : Controller
{
    public async Task<IActionResult> Index(CancellationToken ct) => View(await svc.ListAsync(ct));

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(string name, string format, string? template, IFormFile? file, CancellationToken ct)
    {
        string? filePath = null;
        string? fileHash = null;

        // Template nativo tem prioridade se nenhum arquivo customizado for enviado
        if ((file is null || file.Length == 0) && !string.IsNullOrWhiteSpace(template))
        {
            var tplName = template.ToLowerInvariant() switch { "dpo" => "dpo.jsonl", "rm" => "rm.jsonl", _ => "sft.jsonl" };
            var tplPath = Path.Combine(env.ContentRootPath, "assets", "gemma-trainer", "templates", tplName);
            if (System.IO.File.Exists(tplPath))
            {
                var dir = Path.Combine(env.WebRootPath ?? env.ContentRootPath, "uploads", "datasets");
                Directory.CreateDirectory(dir);
                filePath = Path.Combine(dir, $"{Guid.NewGuid()}_{tplName}");
                System.IO.File.Copy(tplPath, filePath, overwrite: true);
                using var sha = SHA256.Create();
                using var fs = System.IO.File.OpenRead(filePath);
                fileHash = Convert.ToHexString(await sha.ComputeHashAsync(fs, ct)).ToLowerInvariant();
                if (string.IsNullOrWhiteSpace(format)) format = "jsonl";
            }
        }

        if (file is not null && file.Length > 0)
        {
            var dir = Path.Combine(env.WebRootPath ?? env.ContentRootPath, "uploads", "datasets");
            Directory.CreateDirectory(dir);
            var safeName = Path.GetFileName(file.FileName);
            filePath = Path.Combine(dir, $"{Guid.NewGuid()}_{safeName}");
            using (var fs = System.IO.File.Create(filePath))
            {
                await file.CopyToAsync(fs, ct);
            }
            using var sha = SHA256.Create();
            using var fs2 = System.IO.File.OpenRead(filePath);
            fileHash = Convert.ToHexString(await sha.ComputeHashAsync(fs2, ct)).ToLowerInvariant();
        }

        await svc.CreateAsync(name, string.IsNullOrWhiteSpace(format) ? "jsonl" : format, null, filePath, fileHash, ct);
        return RedirectToAction("Index");
    }

    [HttpGet]
    public IActionResult DownloadTemplate(string mode)
    {
        mode = (mode ?? "sft").ToLowerInvariant();
        var fileName = mode switch { "dpo" => "dpo.jsonl", "rm" => "rm.jsonl", _ => "sft.jsonl" };
        var path = Path.Combine(env.ContentRootPath, "assets", "gemma-trainer", "templates", fileName);
        if (!System.IO.File.Exists(path)) return NotFound();
        return PhysicalFile(path, "application/x-ndjson", fileName);
    }
}
