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
    IGenericRepository<HarnessProfile> profileRepository,
    IGenericRepository<Experiment> experimentRepository,
    IGenericRepository<ModelArtifact> modelRepository,
    IGenericRepository<InstructionProfile> instructionRepository,
    IGenericRepository<TrainingWorkflowCard> cardRepository) : Controller
{
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var harnessProfiles = await profileRepository.QueryRaw("SELECT * FROM harness_profiles WHERE status != 'Approved'", new Dictionary<string, object>());
        var experiments = await experimentRepository.QueryRaw("SELECT * FROM experiments WHERE status IN ('Planned','Running','Completed')", new Dictionary<string, object>());
        var modelArtifacts = await modelRepository.QueryRaw("SELECT * FROM model_artifacts WHERE status = 'Draft'", new Dictionary<string, object>());
        var instructionProfiles = await instructionRepository.QueryRaw("SELECT * FROM instruction_profiles WHERE status = 'Draft'", new Dictionary<string, object>());
        var trainingCards = await cardRepository.QueryRaw("SELECT * FROM training_workflow_cards WHERE status_id = 11", new Dictionary<string, object>());

        ViewBag.HarnessProfiles = harnessProfiles;
        ViewBag.Experiments = experiments;
        ViewBag.ModelArtifacts = modelArtifacts;
        ViewBag.InstructionProfiles = instructionProfiles;
        ViewBag.TrainingCards = trainingCards;
        return View();
    }
}
