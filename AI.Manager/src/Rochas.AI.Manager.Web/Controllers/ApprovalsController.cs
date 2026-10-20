using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Rochas.AI.Manager.Domain.Entities;
using Rochas.AI.Manager.Domain.Experiments;
using Rochas.AI.Manager.Domain.Harness;
using Rochas.AI.Manager.Domain.Interfaces;
using Rochas.AI.Manager.Domain.Models;
using Rochas.AI.Manager.Domain.Profiles;
using Rochas.Data.Specification.Interfaces;

namespace Rochas.AI.Manager.Web.Controllers;

[Authorize(Roles = "Admin")]
public sealed class ApprovalsController(
    IGenericRepository<HarnessProfile> profiles,
    IGenericRepository<Experiment> experiments,
    IGenericRepository<ModelArtifact> models,
    IGenericRepository<InstructionProfile> instructions,
    IGenericRepository<TrainingWorkflowCard> cards) : Controller
{
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var hp = await profiles.QueryRaw("SELECT * FROM harness_profiles WHERE status != 'Approved'", new Dictionary<string, object>());
        var exp = await experiments.QueryRaw("SELECT * FROM experiments WHERE status IN ('Planned','Running','Completed')", new Dictionary<string, object>());
        var ma = await models.QueryRaw("SELECT * FROM model_artifacts WHERE status = 'Draft'", new Dictionary<string, object>());
        var ip = await instructions.QueryRaw("SELECT * FROM instruction_profiles WHERE status = 'Draft'", new Dictionary<string, object>());
        var tw = await cards.QueryRaw("SELECT * FROM training_workflow_cards WHERE status_id = 11", new Dictionary<string, object>()); // NOVO
        ViewBag.HarnessProfiles = hp;
        ViewBag.Experiments = exp;
        ViewBag.ModelArtifacts = ma;
        ViewBag.InstructionProfiles = ip;
        ViewBag.TrainingCards = tw;
        return View();
    }
}