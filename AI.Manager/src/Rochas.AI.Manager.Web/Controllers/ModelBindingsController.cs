using Microsoft.AspNetCore.Mvc;
using Rochas.AI.Manager.Application.Abstractions;
using Rochas.AI.Manager.Domain.Harness;

namespace Rochas.AI.Manager.Web.Controllers;

[ApiController]
[Route("api/modelbindings")]
public sealed class ModelBindingsController(IModelBindingService svc) : ControllerBase
{
    [HttpGet] public async Task<IActionResult> List(CancellationToken ct) => Ok(await svc.ListAsync(ct));
    [HttpGet("{id:int}")] public async Task<IActionResult> Get(int id, CancellationToken ct) { var x = await svc.GetAsync(id, ct); return x is null ? NotFound() : Ok(x); }
    [HttpPost] public async Task<IActionResult> Create([FromBody] CreateModelBindingRequest req, CancellationToken ct)
    { var id = await svc.CreateAsync(req.Provider, req.ModelId, req.MaxTokens, req.ContextWindow, req.Thinking, req.Temperature, ct); return CreatedAtAction(nameof(Get), new { id }, new { id }); }
    [HttpPut("{id:int}")] public async Task<IActionResult> Update(int id, [FromBody] UpdateModelBindingRequest req, CancellationToken ct)
    { await svc.UpdateAsync(id, req.MaxTokens, req.ContextWindow, req.Thinking, req.Temperature, ct); return NoContent(); }
    [HttpDelete("{id:int}")] public async Task<IActionResult> Delete(int id, CancellationToken ct) { await svc.DeleteAsync(id, ct); return NoContent(); }
}
public sealed record CreateModelBindingRequest(string Provider, string ModelId, int MaxTokens, int ContextWindow, string Thinking, double Temperature);
public sealed record UpdateModelBindingRequest(int MaxTokens, int ContextWindow, string Thinking, double Temperature);
