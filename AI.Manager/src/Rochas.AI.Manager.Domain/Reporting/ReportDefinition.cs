using System;
using System.Collections.Generic;

namespace Rochas.AI.Manager.Domain.Reporting;

public sealed record ReportDefinition(
    string Id,
    string Title,
    string Entity,
    string RootTable,
    string DefaultDateAxis,
    IReadOnlyList<string> AllowedDateAxes,
    IReadOnlyList<string> Filters,
    IReadOnlyList<string> ExportFormats);

public static class ReportCatalog
{
    public static readonly IReadOnlyList<ReportDefinition> All = new List<ReportDefinition>
    {
        new("R1", "Jobs por periodo", "TrainingJob", "training_jobs", "created_at", new[]{"created_at","started_at","completed_at"}, new[]{"status","method","artifactId","datasetId","experimentId","profileName","advancedOnly","accuracyMin","accuracyMax"}, new[]{"csv","xlsx","pdf","html"}),
        new("R2", "Funil de experimentos", "Experiment", "experiments", "created_at", new[]{"created_at"}, new[]{"status","baseArtifactId","tunedArtifactId","family"}, new[]{"csv","xlsx","pdf","html"}),
        new("R4", "Qualidade do treino", "TrainingJob", "training_jobs", "started_at", new[]{"started_at","completed_at"}, new[]{"status","accuracyMin","accuracyMax"}, new[]{"csv","xlsx","pdf","html"}),
        new("R5", "Falhas de treino", "TrainingJob", "training_jobs", "completed_at", new[]{"completed_at"}, new[]{"status"}, new[]{"csv","xlsx","pdf","html"}),
        new("R9", "Execucoes por periodo", "HarnessRun", "harness_runs", "created_at", new[]{"created_at"}, new[]{"profileId","profileVersionId","bindingId","provider","mode","resultClass"}, new[]{"csv","xlsx","pdf","html"}),
        new("R13", "Capturas do bridge", "BridgeCapture", "bridge_captures", "created_at", new[]{"created_at"}, new[]{"mode","hasResponse","duplicateOnly","linkedToRun"}, new[]{"csv","xlsx","pdf","html"}),
        new("R18", "Kanban de cards", "TrainingWorkflowCard", "training_workflow_cards", "created_at", new[]{"created_at","updated_at"}, new[]{"statusId","experimentId","approverId","requesterId","ownerLogin"}, new[]{"csv","xlsx","pdf","html"}),
        new("R22", "Trilha de auditoria", "WorkFlowTransition", "workflow_transitions", "created_at", new[]{"created_at"}, new[]{"entityType","entityId","action","approverId"}, new[]{"csv","xlsx","pdf","html"}),
        new("R29", "Integridade referencial", "User", "users", "created_at", new[]{"created_at"}, new[]{"isAdmin","isActive"}, new[]{"csv","xlsx","pdf","html"}),
        new("R30", "Inventario inativo", "Agent", "agents", "created_at", new[]{"created_at"}, new[]{"isActive","agentMode","model","thinking"}, new[]{"csv","xlsx","pdf","html"}),
    };

    public static ReportDefinition? Find(string id) => All.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));
    public static ReportDefinition? FindByEntity(string entity) => All.FirstOrDefault(d => string.Equals(d.Entity, entity, StringComparison.OrdinalIgnoreCase));
}
