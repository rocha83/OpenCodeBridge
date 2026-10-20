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
        var allProfiles = await profileRepository.Query(new HarnessProfile());
        var harnessProfiles = allProfiles.Where(x => x.Status != "Approved").ToList();
        var allExperiments = await experimentRepository.Query(new Experiment());
        var experiments = allExperiments.Where(x => x.Status == "Planned" || x.Status == "Running" || x.Status == "Completed").ToList();
        var allModels = await modelRepository.Query(new ModelArtifact());
        var modelArtifacts = allModels.Where(x => x.Status == "Draft").ToList();
        var allInstr = await instructionRepository.Query(new InstructionProfile());
        var instructionProfiles = allInstr.Where(x => x.Status == "Draft").ToList();
        var allCards = await cardRepository.Query(new TrainingWorkflowCard());
        var trainingCards = allCards.Where(x => x.StatusId == 11).ToList();

        ViewBag.HarnessProfiles = harnessProfiles;
        ViewBag.Experiments = experiments;
        ViewBag.ModelArtifacts = modelArtifacts;
        ViewBag.InstructionProfiles = instructionProfiles;
        ViewBag.TrainingCards = trainingCards;
        return View();
    }
}
