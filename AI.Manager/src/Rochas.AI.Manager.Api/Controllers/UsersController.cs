using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Rochas.AI.Manager.Domain.Interfaces;

namespace Rochas.AI.Manager.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/users")]
public sealed class UsersController : ControllerBase
{
    private readonly IUserService _svc = null!;
    public UsersController(IUserService svc) { _svc = svc; }
    [HttpGet] public async Task<IActionResult> List(CancellationToken ct) => Ok(await _svc.ListAsync(ct));
    [HttpGet("{id:int}")] public async Task<IActionResult> Get(int id, CancellationToken ct) { var x = await _svc.GetAsync(id, ct); return x is null ? NotFound() : Ok(x); }
    [HttpPost] public async Task<IActionResult> Create([FromBody] UsersCreateRequest req, CancellationToken ct) { var id = await _svc.CreateAsync(req.Name, req.Email, req.PasswordHash, req.IsAdmin, ct); return CreatedAtAction(nameof(Get), new { id }, new { id }); }
    [HttpPut("{id:int}")] public async Task<IActionResult> Update(int id, [FromBody] UsersUpdateRequest req, CancellationToken ct) { await _svc.UpdateAsync(id, req.Name, req.Email, req.IsAdmin, req.Active, ct); return NoContent(); }
    [HttpDelete("{id:int}")] public async Task<IActionResult> Delete(int id, CancellationToken ct) { await _svc.DeleteAsync(id, ct); return NoContent(); }
}
public sealed record UsersCreateRequest(string Name, string Email, string PasswordHash, bool IsAdmin);
public sealed record UsersUpdateRequest(string Name, string Email, bool IsAdmin, bool Active);
