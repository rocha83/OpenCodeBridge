using Rochas.AI.Manager.Domain.Entities.Management;
using Rochas.AI.Manager.Domain.Helpers;
using Rochas.Data.Specification.Interfaces;
using System.Threading;

namespace Rochas.AI.Manager.Web.Data;

public static class WorkflowSeeder
{
    public static async Task SeedAsync(
        IGenericRepository<EntityStatus> statuses,
        IGenericRepository<WorkflowStep> steps)
    {
        // Estados possiveis (Gemma Forge)
        // Nomes de exibicao em pt-BR; codigos (Code) em en-US para o codigo.
        var statusDefs = new (string Code, string Name)[]
        {
            ("DRAFT", "Rascunho"),
            ("APPROVED", "Aprovado"),
            ("DEPRECATED", "Descontinuado"),
            ("PLANNED", "Planejado"),
            ("RUNNING", "Em execucao"),
            ("COMPLETED", "Concluido"),
            ("REJECTED", "Rejeitado"),
            ("QUEUED", "Na fila"),
            ("CANCELLED", "Cancelado"),
            ("FAILED", "Falhou"),
            ("NOVO", "Novo"),
            ("ACEITE", "Aceite"),
            ("REPROVADO", "Reprovado"),
        };

        var existingStatuses = await statuses.Query(new EntityStatus());
        var codeToId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var status in existingStatuses) codeToId[status.Code] = status.Id;

        foreach (var (code, name) in statusDefs)
        {
            if (codeToId.ContainsKey(code)) continue;
            var st = new EntityStatus { Code = code, Name = name, IsActive = true };
            await statuses.Add(st);
            codeToId[code] = st.Id;
        }

        // Steps por entidade: (entityType, list of (name, sortOrder, statusCode, nextStatusCode))
        int GetId(string code) => codeToId[code];

        var stepDefs = new List<(Type EntityType, (string Name, int Order, string Status, string Next)[] Steps)>
        {
            (typeof(Rochas.AI.Manager.Domain.Models.ModelArtifact), new[]
            {
                ("Rascunho -> Aprovado", 1, "DRAFT", "APPROVED"),
                ("Aprovado -> Descontinuado", 2, "APPROVED", "DEPRECATED"),
            }),
            (typeof(Rochas.AI.Manager.Domain.Profiles.InstructionProfile), new[]
            {
                ("Rascunho -> Aprovado", 1, "DRAFT", "APPROVED"),
                ("Aprovado -> Descontinuado", 2, "APPROVED", "DEPRECATED"),
            }),
            (typeof(Rochas.AI.Manager.Domain.Harness.HarnessProfile), new[]
            {
                ("Rascunho -> Aprovado", 1, "DRAFT", "APPROVED"),
                ("Aprovado -> Descontinuado", 2, "APPROVED", "DEPRECATED"),
            }),
            (typeof(Rochas.AI.Manager.Domain.Experiments.Experiment), new[]
            {
                ("Planejado -> Em execucao", 1, "PLANNED", "RUNNING"),
                ("Em execucao -> Concluido", 2, "RUNNING", "COMPLETED"),
                ("Concluido -> Aprovado", 3, "COMPLETED", "APPROVED"),
                ("Concluido -> Rejeitado", 4, "COMPLETED", "REJECTED"),
            }),
            (typeof(Rochas.AI.Manager.Domain.Training.TrainingJob), new[]
            {
                ("Na fila -> Em execucao", 1, "QUEUED", "RUNNING"),
                ("Em execucao -> Concluido", 2, "RUNNING", "COMPLETED"),
                ("Em execucao -> Falhou", 3, "RUNNING", "FAILED"),
                ("Na fila -> Cancelado", 4, "QUEUED", "CANCELLED"),
            }),
            (typeof(Rochas.AI.Manager.Domain.Entities.TrainingWorkflowCard), new[]
            {
                ("Novo -> Aceite", 1, "NOVO", "ACEITE"),
                ("Novo -> Reprovado", 2, "NOVO", "REPROVADO"),
            }),
        };

        foreach (var (entityType, stepsDef) in stepDefs)
        {
            uint hash = EntityHashCode.Compute(entityType.FullName ?? entityType.Name);
            var existing = await steps.Query(new WorkflowStep());
            var existingForEntity = existing.Where(step => step.EntityHashCode == hash).ToList();
            if (existingForEntity.Any()) continue;

            foreach (var (name, order, status, next) in stepsDef)
            {
                var step = new WorkflowStep(hash, name, order, GetId(status), GetId(next), null, true)
                {
                    Name = name,
                };
                await steps.Add(step);
            }
        }
    }
}
