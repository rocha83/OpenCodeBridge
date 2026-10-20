using Rochas.AI.Manager.Domain.Entities;
using Rochas.AI.Manager.Domain.Experiments;
using Rochas.AI.Manager.Domain.Harness;
using Rochas.AI.Manager.Domain.Models;
using Rochas.AI.Manager.Domain.Profiles;
using Rochas.Data.Specification.Interfaces;
using System.Threading;

namespace Rochas.AI.Manager.Web.Data;

public static class BridgeFeatureSeeder
{
    public static async Task SeedAsync(
        IGenericRepository<ModelArtifact> models,
        IGenericRepository<Agent> agents,
        IGenericRepository<ModelBinding> bindings,
        IGenericRepository<HarnessProfile> profiles,
        IGenericRepository<HarnessProfileVersion> versions,
        IGenericRepository<HarnessAttachment> attachments,
        IGenericRepository<ToolPolicy> policies,
        IGenericRepository<InstructionProfile> instructions,
        IGenericRepository<TrainingPattern> patterns,
        IGenericRepository<Experiment> experiments)
    {
        // ModelArtifact
        var existingModels = await models.QueryRaw("SELECT * FROM model_artifacts", new Dictionary<string, object>());
        if (!existingModels.Any(m => m.Name == "Gemma4 12B Q4_K_M"))
        {
            await models.Add(new ModelArtifact { Name = "Gemma4 12B Q4_K_M", Family = "Gemma4", Variant = "12b", Format = "gguf", IsTrainable = true, Status = "Approved" });
        }
        if (!existingModels.Any(m => m.Name == "Gemma4 E2B Q3_K_M"))
        {
            await models.Add(new ModelArtifact { Name = "Gemma4 E2B Q3_K_M", Family = "Gemma4", Variant = "e2b", Format = "gguf", IsTrainable = false, Status = "Approved" });
        }

        // ModelBinding
        var existingBindings = await bindings.QueryRaw("SELECT * FROM model_bindings", new Dictionary<string, object>());
        if (!existingBindings.Any(b => b.ModelId == "gemma-4-12b"))
        {
            await bindings.Add(new ModelBinding { Name = "Gemma4 12B (bridge GPU)", Provider = "openai", ModelId = "gemma-4-12b", MaxTokens = 32768, ContextWindow = 163840, Thinking = "on", Temperature = 0.5 });
        }
        if (!existingBindings.Any(b => b.ModelId == "gemma-4-e2b"))
        {
            await bindings.Add(new ModelBinding { Name = "Gemma4 E2B (bridge CPU)", Provider = "llama", ModelId = "gemma-4-e2b", MaxTokens = 16384, ContextWindow = 65536, Thinking = "off", Temperature = 0.3 });
        }

        // Agent (6 roles)
        var existingAgents = await agents.QueryRaw("SELECT * FROM agents", new Dictionary<string, object>());
        var agentDefs = new (string Name, string Model, string BridgeUrl, double Temp, string Thinking, string Mode, string Tools)[]
        {
            ("plan", "openai/gemma-4-12b", "http://localhost:4124", 0.5, "on", "Plan", "read,grep,glob,question"),
            ("build", "openai/gemma-4-12b", "http://localhost:4124", 0.5, "on", "Build", "read,write,edit,glob,grep,shell,execute,task"),
            ("orch", "openai/gemma-4-12b", "http://localhost:4124", 0.5, "on", "Orch", "task,read,grep"),
            ("moe", "openai/gemma-4-12b", "http://localhost:4124", 0.8, "on", "MoE", "read,write,edit,glob,grep,shell"),
            ("scripter", "openai/gemma-4-12b", "http://localhost:4124", 0.3, "on", "Scripter", "read,write,edit,glob,grep"),
            ("exec", "llama/gemma-4-e2b", "http://localhost:4125", 0.3, "off", "Exec", "read,write,edit,glob,grep,shell,execute"),
        };
        foreach (var (name, model, url, temp, thinking, mode, tools) in agentDefs)
        {
            if (existingAgents.Any(a => a.Name == name)) continue;
            await agents.Add(new Agent { Name = name, Model = model, BridgeUrl = url, Temperature = temp, Thinking = thinking, Mode = mode, Tools = tools });
        }

        // HarnessProfile
        var existingProfiles = await profiles.QueryRaw("SELECT * FROM harness_profiles", new Dictionary<string, object>());
        int profileId;
        if (existingProfiles.Any(p => p.Name == "OpenCodeBridge Agents"))
        {
            profileId = existingProfiles.First(p => p.Name == "OpenCodeBridge Agents").Id;
        }
        else
        {
            var prof = new HarnessProfile { Name = "OpenCodeBridge Agents", Description = "Harness principal dos 6 agentes (Plan/Build/Orch/MoE/Scripter/Exec)", Status = "Approved" };
            await profiles.Add(prof);
            profileId = prof.Id;
        }

        // Version
        var existingVersions = await versions.QueryRaw("SELECT * FROM harness_profile_versions", new Dictionary<string, object>());
        int versionId;
        if (existingVersions.Any(v => v.ProfileId == profileId && v.Version == 1))
        {
            versionId = existingVersions.First(v => v.ProfileId == profileId && v.Version == 1).Id;
        }
        else
        {
            var ver = new HarnessProfileVersion { ProfileId = profileId, Version = 1, Mode = "ClientNative", ApprovedBy = "seeder", ApprovedAt = DateTime.UtcNow };
            await versions.Add(ver);
            versionId = ver.Id;
        }

        // Attachment (system prompt core completo - harness opencode)
        var existingAtt = await attachments.QueryRaw("SELECT * FROM harness_attachments", new Dictionary<string, object>());
        if (!existingAtt.Any(a => a.VersionId == versionId && a.Kind == "Core"))
        {
            var asm = typeof(BridgeFeatureSeeder).Assembly;
            string ReadRes(string name)
            {
                using var st = asm.GetManifestResourceStream(name);
                if (st is null) return string.Empty;
                using var r = new System.IO.StreamReader(st);
                return r.ReadToEnd();
            }
            var system = ReadRes("Rochas.AI.Manager.Web.Data.harness-system.md");
            if (string.IsNullOrWhiteSpace(system)) system = "Voce e um arquiteto desenvolvedor de software generalista.";
            await attachments.Add(new HarnessAttachment { VersionId = versionId, Kind = "Core", Role = "general", Content = system, CharCount = system.Length });
            var tools = ReadRes("Rochas.AI.Manager.Web.Data.harness-tools.json");
            if (!string.IsNullOrWhiteSpace(tools))
            {
                await attachments.Add(new HarnessAttachment { VersionId = versionId, Kind = "Tools", Role = "general", Content = tools, CharCount = tools.Length });
            }
            var nucleo = ReadRes("Rochas.AI.Manager.Web.Data.harness-nucleo.md");
            if (!string.IsNullOrWhiteSpace(nucleo))
            {
                await attachments.Add(new HarnessAttachment { VersionId = versionId, Kind = "Nucleo", Role = "general", Content = nucleo, CharCount = nucleo.Length });
            }
        }

        // ToolPolicy por role
        var existingPolicies = await policies.QueryRaw("SELECT * FROM tool_policies", new Dictionary<string, object>());
        var policyDefs = new (string Role, string Allow, string Deny, bool RequireCall, int MaxTurns, int StallLimit)[]
        {
            ("build", "read,write,edit,glob,grep,shell,execute,task", "websearch", false, 20, 3),
            ("plan", "read,grep,glob,question", "write,edit,shell", false, 10, 3),
            ("orch", "task,read,grep", "write,edit,shell", true, 15, 3),
            ("moe", "read,write,edit,glob,grep,shell", "websearch", false, 20, 3),
            ("scripter", "read,write,edit,glob,grep", "shell,execute", false, 10, 3),
            ("exec", "read,write,edit,glob,grep,shell,execute", "", false, 10, 3),
        };
        foreach (var (role, allow, deny, req, mt, sl) in policyDefs)
        {
            if (existingPolicies.Any(p => p.Role == role)) continue;
            await policies.Add(new ToolPolicy { Role = role, Allow = allow, Deny = deny, RequireCall = req, MaxTurns = mt, StallLimit = sl });
        }

        // InstructionProfile
        var existingInstr = await instructions.QueryRaw("SELECT * FROM instruction_profiles", new Dictionary<string, object>());
        if (!existingInstr.Any(i => i.Name == "AGENTS.md global"))
        {
            await instructions.Add(new InstructionProfile { Name = "AGENTS.md global", Description = "Instrucoes globais do agente (AGENTS.md)", Version = "1.0", Status = "Approved" });
        }

        // Experiment
        var existingExp = await experiments.QueryRaw("SELECT * FROM experiments", new Dictionary<string, object>());
        if (!existingExp.Any(e => e.Name == "OpenCodeBridge baseline"))
        {
            await experiments.Add(new Experiment { Name = "OpenCodeBridge baseline", Hypothesis = "Motor TorchSharp + harness local", Status = "Planned" });
        }
    }
}