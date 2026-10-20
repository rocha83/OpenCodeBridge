using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Rochas.AI.Manager.Domain.Interfaces;
using Rochas.AI.Manager.Domain.Harness;

namespace Rochas.AI.Manager.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/modelbindings")]
public sealed class ModelBindingsController : ControllerBase
{
    private readonly IModelBindingService _svc = null!;
    public ModelBindingsController(IModelBindingService svc) { _svc = svc; }
    [HttpGet] public async Task<IActionResult> List(CancellationToken ct) => Ok(await _svc.ListAsync(ct));
    [HttpGet("{id:int}")] public async Task<IActionResult> Get(int id, CancellationToken ct) { var x = await _svc.GetAsync(id, ct); return x is null ? NotFound() : Ok(x); }
    [HttpPost] public async Task<IActionResult> Create([FromBody] ModelBindingsCreateRequest req, CancellationToken ct) { var id = await _svc.CreateAsync(req.Provider, req.ModelId, req.MaxTokens, req.ContextWindow, req.Thinking, req.Temperature, ct); return CreatedAtAction(nameof(Get), new { id }, new { id }); }
    [HttpPut("{id:int}")] public async Task<IActionResult> Update(int id, [FromBody] ModelBindingsUpdateRequest req, CancellationToken ct) { await _svc.UpdateAsync(id, req.MaxTokens, req.ContextWindow, req.Thinking, req.Temperature, ct); return NoContent(); }
    [HttpDelete("{id:int}")] public async Task<IActionResult> Delete(int id, CancellationToken ct) { await _svc.DeleteAsync(id, ct); return NoContent(); }
}
public sealed record ModelBindingsCreateRequest(string Provider, string ModelId, int MaxTokens, int ContextWindow, string Thinking, double Temperature);
public sealed record ModelBindingsUpdateRequest(int MaxTokens, int ContextWindow, string Thinking, double Temperature);
