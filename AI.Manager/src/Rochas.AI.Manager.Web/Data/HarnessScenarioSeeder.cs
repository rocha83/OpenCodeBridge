using Rochas.AI.Manager.Domain.Harness;
using Rochas.Data.Specification.Interfaces;
using System.Threading;

namespace Rochas.AI.Manager.Web.Data;

public static class HarnessScenarioSeeder
{
    public static async Task SeedAsync(
        IGenericRepository<HarnessProfile> profileRepository,
        IGenericRepository<HarnessProfileVersion> versionRepository,
        IGenericRepository<HarnessAttachment> attachmentRepository)
    {
        var scenarios = new (string Name, string Description, string SystemTail)[]
        {
            ("devpipe", "Cenario devpipe (opencode run --standalone)", "Voce esta em devpipe. Execute tool calls completas. Nao truncar. Build de integridade obrigatorio."),
            ("belt", "Testes belts (sequencia de tarefas incrementais)", "Modo belt: execute tarefas em sequencia, cada uma com ACEITE. Reporte progresso."),
            ("scientific", "Testes cientificos (hipotese -> experimento -> metricas)", "Modo cientifico: formule hipotese, execute experimento, registre metricas e conclusao."),
            ("mission-tools", "Missao tools (tool calling obrigatorio)", "Missao tools: use ferramentas (read/write/edit/shell/execute) para cumprir o objetivo. Sem tool call = falha."),
            ("general", "Topicos gerais (qualquer pedido)", "Topicos gerais: responda com profundidade sob demanda, sem enrolacao."),
            ("build-puro", "Build puro (sem orquestracao/scripts)", "Build puro: implemente diretamente os artefatos; conclua com build/teste verdes e evidencia. Sem segmentacao, sem scripts, sem orquestracao."),
        };

        var existingProfiles = await profileRepository.Query(new HarnessProfile());

        foreach (var (scenarioName, description, systemTail) in scenarios)
        {
            int profileId;
            if (existingProfiles.Any(profile => profile.Name == $"Scenario: {scenarioName}"))
            {
                profileId = existingProfiles.First(profile => profile.Name == $"Scenario: {scenarioName}").Id;
            }
            else
            {
                var profile = new HarnessProfile { Name = $"Scenario: {scenarioName}", Description = description, Status = "Approved" };
                await profileRepository.Add(profile);
                profileId = profile.Id;
                existingProfiles = await profileRepository.Query(new HarnessProfile());
            }

            var versions = await versionRepository.Query(new HarnessProfileVersion());
            if (!versions.Any(version => version.ProfileId == profileId))
            {
                var version = new HarnessProfileVersion { ProfileId = profileId, Version = 1, Mode = "ClientNative", ApprovedBy = "seeder", ApprovedAt = DateTime.UtcNow };
                await versionRepository.Add(version);
                await attachmentRepository.Add(new HarnessAttachment { VersionId = version.Id, Kind = "Core", Role = "general", Content = systemTail, CharCount = systemTail.Length });
            }
        }
    }
}
