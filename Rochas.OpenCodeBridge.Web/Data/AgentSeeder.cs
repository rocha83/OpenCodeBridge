using Microsoft.Data.Sqlite;

namespace Rochas.OpenCodeBridge.Web.Data;

// Seed dos agentes como revisados na UI (snapshot; só insere o que falta,
// nunca sobrescreve edição do usuário — espelha o seed do admin em Program).
public static class AgentSeeder
{
    private sealed record Seed(int Id, string Name, string Model, string Bridge,
        double Temp, string Thinking, string Prompt, string Role, string Mode,
        double Tps, int Active, string Tools);

    // Snapshot da revisão de 13/10 (Q8 na GPU :4124, 4B na CPU :4125).
    private static readonly Seed[] Seeds =
    [
        new(1, "Qwen3.5 9B - Agente Generalista Global - MoE", "qwen3.5-9b", "http://127.0.0.1:4124",
            0.8, "events",
            "Voce e um agente autonomo generalista senior entende sobre todos assuntos em esfera global delega tarefas a subagentes quando entende que o contexto ficou moderado (128k)",
            "planner", "Plan", 0.0, 1,
            "glob,read,grep,git,fetch,display,subagent"),
        new(2, "Qwen3.5 9B GPU - Engenheiro de Software - Plan", "qwen3.5-9b", "http://127.0.0.1:4124",
            0.5, "events",
            "Voce e um agente autonomo arquiteto engenheiro de software e dados (ML e NN) senior adepto das melhores praticas SOLID, design DDD clean arch e uso de ORM, delega tarefas a subagentes quando entende o contexto moderado (128k)",
            "executor", "Plan", 0.0, 1,
            "glob,read,grep,git,fetch,display,subagent"),
        new(3, "Qwen3.5 9B GPU - Desenvolvedor de Software - Build", "qwen3.5-9b", "http://127.0.0.1:4124",
            0.5, "events",
            "Voce e um agente autonomo arquiteto desenvolvedor de software e dados (ML e NN) senior, executa todas as tools e implementa os artefatos e testes conforme enunciado seguindo as melhores praticas de desenvolvimento, delega tarefas a subagentes quando entende o contexto moderado (128k)",
            "orchestrator", "Build", 0.0, 1,
            "glob,read,grep,git,fetch,display,write,edit,shell,subagent"),
        new(4, "Qwen3.5 9B GPU - Engenheiro de Software - Orch", "qwen3.5-4b", "http://127.0.0.1:4125",
            0.5, "events",
            "Voce e um agente arquiteto engenheiro de software e dados (ML e NN) senior, analise o enunciado expanda o compreendido divida em fases e delegue para subagentes desenvolvedores senior (porem menor capacidade de inferencia) ",
            "executor", "Build", 0.0, 1,
            "glob,read,grep,git,fetch,display,subagent"),
        new(5, "Qwen3.5 4B CPU - Agente Generalista Global - MoE", "qwen3.5-9b", "http://127.0.0.1:4124",
            0.8, "events",
            "Voce e um agente autonomo generalista senior entende sobre todos assuntos em esfera global delega tarefas a subagentes quando entende que o contexto ficou limitado (16k)",
            "executor", "Plan", 0.0, 1,
            "glob,read,grep,git,fetch,display,subagent"),
        new(8, "Qwen3.5 4B CPU - Desenvolvedor de Software - Build", "qwen3.5-9b", "http://127.0.0.1:4124",
            0.5, "off",
            "Voce e um agente desenvolvedor senior que implementa codigos utilizando script shell linux bash ou windows .bat conforme solicitacao, os scripts devem ser interativos opcionalmente e (nao necessariamente) conter mais de um fluxo \"caminho\" tendo ao menos um como recomendado a seguir caso o usuario deseje executar o script como nao interativo ",
            "", "Build", 30.7, 1,
            "glob,read,grep,git,fetch,display,write,edit,shell"),
    ];

    public static void SeedIfEmpty(SqliteConnection conn)
    {
        foreach (var s in Seeds)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT OR IGNORE INTO agents
                    (id, name, model, bridge_url, temperature, thinking, system_prompt, role, mode, measured_tps, active, tools)
                VALUES
                    (@id, @name, @model, @bridge, @temp, @thinking, @prompt, @role, @mode, @tps, @active, @tools);
                """;
            cmd.Parameters.AddWithValue("@id", s.Id);
            cmd.Parameters.AddWithValue("@name", s.Name);
            cmd.Parameters.AddWithValue("@model", s.Model);
            cmd.Parameters.AddWithValue("@bridge", s.Bridge);
            cmd.Parameters.AddWithValue("@temp", s.Temp);
            cmd.Parameters.AddWithValue("@thinking", s.Thinking);
            cmd.Parameters.AddWithValue("@prompt", s.Prompt);
            cmd.Parameters.AddWithValue("@role", s.Role);
            cmd.Parameters.AddWithValue("@mode", s.Mode);
            cmd.Parameters.AddWithValue("@tps", s.Tps);
            cmd.Parameters.AddWithValue("@active", s.Active);
            cmd.Parameters.AddWithValue("@tools", s.Tools);
            try { cmd.ExecuteNonQuery(); } catch { /* seed best-effort */ }
        }
    }
}
