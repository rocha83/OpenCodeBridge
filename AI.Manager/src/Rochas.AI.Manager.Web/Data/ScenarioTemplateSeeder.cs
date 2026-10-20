using Rochas.AI.Manager.Domain.Experiments;
using Rochas.AI.Manager.Domain.Harness;
using Rochas.AI.Manager.Domain.Models;
using Rochas.AI.Manager.Domain.Profiles;
using Rochas.Data.Specification.Interfaces;
using System.Threading;

namespace Rochas.AI.Manager.Web.Data;

public static class ScenarioTemplateSeeder
{
    private static readonly (string Name, string Hypothesis, string PatternJson, double TargetAccuracy)[] Scenarios =
    {
        ("topics", "Topicos gerais: responder com profundidade sob demanda", "{\"prompt\":\"Responda ao pedido do usuario com profundidade sob demanda, sem enrolacao.\"}", 0.7),
        ("devpipe", "DevPipe: executar enunciado completo com build de integridade", "{\"prompt\":\"Execute o enunciado completo: crie todos os artefatos, valide build, anexe evidencia.\"}", 0.8),
        ("belt-azul", "Belt azul: sequencia de tarefas incrementais com scripts", "{\"prompt\":\"Orquestre: segmente em tarefas, componha scripts, execute e valide.\"}", 0.8),
        ("belt-marrom", "Belt marrom: CRUD + API + repositorios", "{\"prompt\":\"Implemente CRUD completo com repositorios e API REST.\"}", 0.8),
        ("belt-roxa", "Belt roxa: modelagem + scripts com validacao", "{\"prompt\":\"Modele dominio e valide scripts com bash -n.\"}", 0.8),
        ("belt-verde", "Belt verde: orquestracao + scripts", "{\"prompt\":\"Orquestre tarefas e componha scripts executaveis.\"}", 0.8),
        ("belt-preta", "Belt preta: missao completa com tools", "{\"prompt\":\"Missao com tools: use read/write/edit/shell/execute para cumprir o objetivo.\"}", 0.9),
        ("scientific", "Cientifico: hipotese -> experimento -> metricas", "{\"prompt\":\"Formule hipotese, execute experimento, registre metricas e conclusao.\"}", 0.9),
        ("mission-tools", "Missao tools: tool calling obrigatorio", "{\"prompt\":\"Use ferramentas (read/write/edit/shell/execute) para cumprir o objetivo. Sem tool call = falha.\"}", 0.9),
    };

    public static async Task SeedAsync(
        IGenericRepository<InstructionProfile> instructions,
        IGenericRepository<TrainingPattern> patterns,
        IGenericRepository<Experiment> experiments,
        IGenericRepository<HarnessProfile> profiles,
        IGenericRepository<ModelArtifact> models)
    {
        var existingInstr = await instructions.QueryRaw("SELECT * FROM instruction_profiles", new Dictionary<string, object>());
        var existingPat = await patterns.QueryRaw("SELECT * FROM training_patterns", new Dictionary<string, object>());
        var existingExp = await experiments.QueryRaw("SELECT * FROM experiments", new Dictionary<string, object>());
        var existingProf = await profiles.QueryRaw("SELECT * FROM harness_profiles", new Dictionary<string, object>());
        var existingModels = await models.QueryRaw("SELECT * FROM model_artifacts", new Dictionary<string, object>());

        int modelId = existingModels.Any() ? existingModels.First().Id : 0;

        foreach (var (name, hypothesis, patternJson, target) in Scenarios)
        {
            // InstructionProfile
            if (!existingInstr.Any(i => i.Name == $"Scenario: {name}"))
            {
                await instructions.Add(new InstructionProfile { Name = $"Scenario: {name}", Description = hypothesis, Version = "1.0", Status = "Approved", InstructionsJson = patternJson });
                existingInstr = await instructions.QueryRaw("SELECT * FROM instruction_profiles", new Dictionary<string, object>());
            }
            var instr = existingInstr.First(i => i.Name == $"Scenario: {name}");

            // Experiment
            if (!existingExp.Any(e => e.Name == $"Scenario: {name}"))
            {
                await experiments.Add(new Experiment { Name = $"Scenario: {name}", Hypothesis = hypothesis, Status = "Planned" });
                existingExp = await experiments.QueryRaw("SELECT * FROM experiments", new Dictionary<string, object>());
            }

            // TrainingPattern
            if (!existingPat.Any(p => p.PatternJson == patternJson))
            {
                await patterns.Add(new TrainingPattern { Name = $"Scenario: {name}", ProfileId = instr.Id, ModelArtifactId = modelId > 0 ? modelId : null, PatternJson = patternJson });
            }
        }
    }
}