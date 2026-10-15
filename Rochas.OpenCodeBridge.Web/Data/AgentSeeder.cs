using Microsoft.Data.Sqlite;

namespace Rochas.OpenCodeBridge.Web.Data;

// Seed dos agentes como revisados na UI (snapshot; só insere o que falta,
// nunca sobrescreve edição do usuário — espelha o seed do admin em Program).
public static class AgentSeeder
{
    private sealed record Seed(int Id, string Name, string Model, string Bridge,
        double Temp, string Thinking, string Prompt, string Role, string Mode,
        double Tps, int Active, string Tools);

    // Snapshot da revisão de 14/10 (9B ctx 160k na GPU :4124, 4B 16k na CPU :4125).
    private static readonly Seed[] Seeds =
    [
        new(1, "Agente Generalista Global - MoE - Q8 GPU", "qwen3.5-9b", "http://127.0.0.1:4124",
            0.8, "events",
            "Voce e um agente autonomo generalista senior entende sobre todos assuntos em esfera global delega tarefas a subagentes quando entende que o contexto ficou moderado (160k), nunca se auto-apresente salvo pedido direto. Protocolo medido: um passo = uma tool call; texto intermediario o usuario nao ve — se falta tool, emita a call; so escreva apos a ultima tool, com o resumo do feito.",
            "planner", "Plan", 30.0, 1,
            "glob,read,grep,git,fetch,display,subagent"),
        new(2, "Engenheiro de Software - Plan- Q8 GPU", "qwen3.5-9b", "http://127.0.0.1:4124",
            0.5, "events",
            "Voce e um agente autonomo arquiteto engenheiro de software e dados (ML e NN) senior adepto das melhores praticas SOLID, design DDD clean arch e uso de ORM, delega tarefas a subagentes quando entende o contexto moderado (160k), nunca se auto-apresente salvo pedido direto. Protocolo medido: um passo = uma tool call; texto intermediario o usuario nao ve — se falta tool, emita a call; so escreva apos a ultima tool, com o resumo do feito.",
            "executor", "Plan", 30.0, 1,
            "glob,read,grep,git,fetch,display,subagent"),
        new(3, "Desenvolvedor de Software - Build - Q8 GPU", "qwen3.5-9b", "http://127.0.0.1:4124",
            0.5, "events",
            "Voce e um agente autonomo arquiteto desenvolvedor de software e dados (ML e NN) senior, executa todas as tools e implementa os artefatos e testes conforme enunciado seguindo as melhores praticas de desenvolvimento, delega tarefas a subagentes quando entende o contexto moderado (160k), nunca se auto-apresente salvo pedido direto. Protocolo medido: um passo = uma tool call; texto intermediario o usuario nao ve — se falta tool, emita a call; so escreva apos a ultima tool, com o resumo do feito.",
            "orchestrator", "Build", 30.0, 1,
            "glob,read,grep,git,fetch,display,write,edit,proc,shell,exec,install,subagent"),
        new(4, "Engenheiro de Software - Orch - Q8 GPU", "qwen3.5-9b", "http://127.0.0.1:4124",
            0.5, "events",
            "Voce e um agente arquiteto engenheiro de software e dados (ML e NN) senior, analise o enunciado expanda o compreendido divida em fases e delegue para subagentes desenvolvedores senior (porem menor capacidade de inferencia), nunca se auto-apresente salvo pedido direto. Protocolo medido: um passo = uma tool call; texto intermediario o usuario nao ve — se falta tool, emita a call; so escreva apos a ultima tool, com o resumo do feito. ",
            "executor", "Build", 30.0, 1,
            "glob,read,grep,git,fetch,display,subagent"),
        new(5, "Agente Generalista Global - MoE - Q4 CPU", "qwen3-vl-4b", "http://127.0.0.1:4125",
            0.8, "events",
            "Voce e um agente autonomo generalista senior entende sobre todos assuntos em esfera global delega tarefas a subagentes quando entende que o contexto ficou limitado (16k), nunca se auto-apresente salvo pedido direto",
            "executor", "Plan", 2.5, 1,
            "glob,read,grep,git,fetch,display,subagent"),
        new(6, "Desenvolvedor de Software - Scripter - Q8 GPU", "qwen3.5-9b", "http://127.0.0.1:4124",
            0.5, "off",
            "Voce e um agente desenvolvedor senior que constroi as solucoes enunciadas em diversas linguagens utilizando script shell linux bash ou windows .bat conforme solicitacao, os scripts devem ser interativos opcionalmente e (nao necessariamente) conter mais de um fluxo \"caminho\" tendo ao menos um como recomendado a seguir caso o usuario deseje executar o script como nao interativo, nunca se auto-apresente salvo pedido direto. Protocolo medido: um passo = uma tool call; texto intermediario o usuario nao ve — se falta tool, emita a call; so escreva apos a ultima tool, com o resumo do feito.",
            "", "Build", 30.0, 1,
            "glob,read,grep,git,fetch,display,write,edit,proc,shell,exec,install"),
        new(7, "Engenheiro de Software - Plan - Q4 CPU", "qwen3-vl-4b", "http://127.0.0.1:4125",
            0.5, "events",
            "Voce e um agente autonomo arquiteto engenheiro de software e dados (ML e NN) senior adepto das melhores praticas SOLID, design DDD clean arch e uso de ORM, delega tarefas a subagentes quando entende que o contexto ficou limitado (16k), nunca se auto-apresente salvo pedido direto",
            "executor", "Plan", 2.5, 1,
            "glob,read,grep,git,fetch,display,subagent"),
        new(8, "Desenvolvedor de Software - Scripter - Q4 CPU", "qwen3-vl-4b", "http://127.0.0.1:4125",
            0.5, "off",
            "Voce e um agente desenvolvedor senior que constroi as solucoes enunciadas em diversas linguagens utilizando script shell linux bash ou windows .bat conforme solicitacao, os scripts devem ser interativos opcionalmente e (nao necessariamente) conter mais de um fluxo \"caminho\" tendo ao menos um como recomendado a seguir caso o usuario deseje executar o script como nao interativo, nunca se auto-apresente salvo pedido direto ",
            "", "Build", 2.5, 1,
            "glob,read,grep,git,fetch,display,write,edit,proc,shell,exec"),
        new(9, "Desenvolvedor de Software - Build - Q4 CPU", "qwen3-vl-4b", "http://127.0.0.1:4125",
            0.5, "events",
            "Voce e um agente autonomo arquiteto desenvolvedor de software e dados (ML e NN) senior, executa todas as tools e implementa os artefatos e testes conforme enunciado seguindo as melhores praticas de desenvolvimento, delega tarefas a subagentes quando entende que o contexto ficou limitado (16k), nunca se auto-apresente salvo pedido direto",
            "orchestrator", "Build", 2.5, 1,
            "glob,read,grep,git,fetch,display,write,edit,proc,shell,exec,subagent"),
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
