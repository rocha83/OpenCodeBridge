using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Rochas.AI.Manager.Domain.Interfaces;

namespace Rochas.AI.Manager.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/agents")]
public sealed class AgentsController : ControllerBase
{
    private readonly IAgentService _svc = null!;
    public AgentsController(IAgentService svc) { _svc = svc; }
    [HttpGet] public async Task<IActionResult> List(CancellationToken ct) => Ok(await _svc.ListAsync(ct));
    [HttpGet("{id:int}")] public async Task<IActionResult> Get(int id, CancellationToken ct) { var x = await _svc.GetAsync(id, ct); return x is null ? NotFound() : Ok(x); }
    [HttpPost] public async Task<IActionResult> Create([FromBody] AgentsCreateRequest req, CancellationToken ct) { var id = await _svc.CreateAsync(req.Name, req.Model, req.BridgeUrl, req.Temperature, req.Thinking, req.Mode, req.Tools, req.SystemPrompt, ct); return CreatedAtAction(nameof(Get), new { id }, new { id }); }
    [HttpPut("{id:int}")] public async Task<IActionResult> Update(int id, [FromBody] AgentsUpdateRequest req, CancellationToken ct) { await _svc.UpdateAsync(id, req.Name, req.Model, req.BridgeUrl, req.Temperature, req.Thinking, req.Mode, req.Tools, req.SystemPrompt, req.Active, ct); return NoContent(); }
    [HttpDelete("{id:int}")] public async Task<IActionResult> Delete(int id, CancellationToken ct) { await _svc.DeleteAsync(id, ct); return NoContent(); }
}
public sealed record AgentsCreateRequest(string Name, string Model, string BridgeUrl, double Temperature, string Thinking, string Mode, string Tools, string SystemPrompt);
public sealed record AgentsUpdateRequest(string Name, string Model, string BridgeUrl, double Temperature, string Thinking, string Mode, string Tools, string SystemPrompt, bool Active);
