using Rochas.AI.Manager.Domain.Harness;
using Rochas.Data.Specification.Interfaces;
using System.Threading;

namespace Rochas.AI.Manager.Web.Data;

public static class HarnessScenarioSeeder
{
    public static async Task SeedAsync(IGenericRepository<HarnessProfile> profiles, IGenericRepository<HarnessProfileVersion> versions, IGenericRepository<HarnessAttachment> attachments)
    {
        var scenarios = new (string Name, string Description, string SystemTail)[]
        {
            ("devpipe", "Cenario devpipe (opencode run --standalone)", "Voce esta em devpipe. Execute tool calls completas. Nao truncar. Build de integridade obrigatorio."),
            ("belt", "Testes belts (sequencia de tarefas incrementais)", "Modo belt: execute tarefas em sequencia, cada uma com ACEITE. Reporte progresso."),
            ("scientific", "Testes cientificos (hipotese -> experimento -> metricas)", "Modo cientifico: formule hipotese, execute experimento, registre metricas e conclusao."),
            ("mission-tools", "Missao tools (tool calling obrigatorio)", "Missao tools: use ferramentas (read/write/edit/shell/execute) para cumprir o objetivo. Sem tool call = falha."),
            ("general", "Topicos gerais (qualquer pedido)", "Topicos gerais: responda com profundidade sob demanda, sem enrolacao."),
        };
        var existing = await profiles.QueryRaw("SELECT * FROM harness_profiles", new Dictionary<string, object>());
        foreach (var (name, desc, tail) in scenarios)
        {
            int pid;
            if (existing.Any(p => p.Name == $"Scenario: {name}"))
            {
                pid = existing.First(p => p.Name == $"Scenario: {name}").Id;
            }
            else
            {
                var p = new HarnessProfile { Name = $"Scenario: {name}", Description = desc, Status = "Approved" };
                await profiles.Add(p);
                pid = p.Id;
                existing = await profiles.QueryRaw("SELECT * FROM harness_profiles", new Dictionary<string, object>());
            }
            var vers = await versions.QueryRaw("SELECT * FROM harness_profile_versions", new Dictionary<string, object>());
            if (!vers.Any(v => v.ProfileId == pid))
            {
                var v = new HarnessProfileVersion { ProfileId = pid, Version = 1, Mode = "ClientNative", ApprovedBy = "seeder", ApprovedAt = DateTime.UtcNow };
                await versions.Add(v);
                var content = tail;
                await attachments.Add(new HarnessAttachment { VersionId = v.Id, Kind = "Core", Role = "general", Content = content, CharCount = content.Length });
            }
        }
    }
}