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
        var statusDefs = new (string Code, string Name)[]
        {
            ("DRAFT", "Draft"),
            ("APPROVED", "Approved"),
            ("DEPRECATED", "Deprecated"),
            ("PLANNED", "Planned"),
            ("RUNNING", "Running"),
            ("COMPLETED", "Completed"),
            ("REJECTED", "Rejected"),
            ("QUEUED", "Queued"),
            ("CANCELLED", "Cancelled"),
            ("FAILED", "Failed"),
            ("NOVO", "Novo"),
            ("ACEITE", "Aceite"),
            ("REPROVADO", "Reprovado"),
        };

        var existingStatuses = await statuses.QueryRaw("SELECT * FROM entity_statuses", new Dictionary<string, object>());
        var codeToId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in existingStatuses) codeToId[s.Code] = s.Id;

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
                ("Draft -> Approved", 1, "DRAFT", "APPROVED"),
                ("Approved -> Deprecated", 2, "APPROVED", "DEPRECATED"),
            }),
            (typeof(Rochas.AI.Manager.Domain.Profiles.InstructionProfile), new[]
            {
                ("Draft -> Approved", 1, "DRAFT", "APPROVED"),
                ("Approved -> Deprecated", 2, "APPROVED", "DEPRECATED"),
            }),
            (typeof(Rochas.AI.Manager.Domain.Harness.HarnessProfile), new[]
            {
                ("Draft -> Approved", 1, "DRAFT", "APPROVED"),
                ("Approved -> Deprecated", 2, "APPROVED", "DEPRECATED"),
            }),
            (typeof(Rochas.AI.Manager.Domain.Experiments.Experiment), new[]
            {
                ("Planned -> Running", 1, "PLANNED", "RUNNING"),
                ("Running -> Completed", 2, "RUNNING", "COMPLETED"),
                ("Completed -> Approved", 3, "COMPLETED", "APPROVED"),
                ("Completed -> Rejected", 4, "COMPLETED", "REJECTED"),
            }),
            (typeof(Rochas.AI.Manager.Domain.Training.TrainingJob), new[]
            {
                ("Queued -> Running", 1, "QUEUED", "RUNNING"),
                ("Running -> Completed", 2, "RUNNING", "COMPLETED"),
                ("Running -> Failed", 3, "RUNNING", "FAILED"),
                ("Queued -> Cancelled", 4, "QUEUED", "CANCELLED"),
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
            var existing = await steps.QueryRaw("SELECT * FROM workflow_steps", new Dictionary<string, object>());
            var existingForEntity = existing.Where(s => s.EntityHashCode == hash).ToList();
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
