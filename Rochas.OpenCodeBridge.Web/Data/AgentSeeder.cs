using Microsoft.Data.Sqlite;

namespace Rochas.OpenCodeBridge.Web.Data;

// Seed dos agentes como revisados na UI (snapshot; só insere o que falta e
// migra ids legados mortos para a linha E4B atual — espelha o seed do admin em Program).
public static class AgentSeeder
{
    private sealed record Seed(int Id, string Name, string Model, string Bridge,
        double Temp, string Thinking, string Prompt, string Role, string Mode,
        double Tps, int Active, string Tools);

    // Snapshot da config atual: Gemma4 E4B Q4 ctx 128k na GPU :4124; Gemma4
    // E4B UD-Q3 (GPU2) ctx 128k em :4110/:4125; E4B Q4 CPU Forced em :4120/:4126. Temperaturas: baixa 0.1
    // (Build/Scripter/Exec), média 0.3 (Plan/Orch), alta 0.5 (MoE).
    // Scripter e Exec compartilham o prompt; só o Exec tem 'exec' no harness
    // (Scripter cria+valida com 'bash -n'; Exec valida e executa 'bash <arquivo>').
    private static readonly Seed[] Seeds =
    [
        new(1, "Agente Generalista Global - MoE - Gemma4 E4B Q4 GPU", "Gemma4-E4B-Q4", "http://127.0.0.1:4124",
            0.5, "events",
            "Voce e um agente autonomo generalista senior entende sobre todos assuntos em esfera global delega tarefas a subagentes quando entende que o contexto ficou moderado (128k), nunca se auto-apresente salvo pedido direto. Protocolo medido: um passo = uma tool call; texto intermediario o usuario nao ve — se falta tool, emita a call; so escreva apos a ultima tool, com o resumo do feito.",
            "planner", "Plan", 30.0, 1,
            "glob,read,grep,git,fetch,display,subagent"),
        new(2, "Engenheiro de Software - Plan - Gemma4 E4B Q4 GPU", "Gemma4-E4B-Q4", "http://127.0.0.1:4124",
            0.3, "events",
            "Voce e um agente autonomo arquiteto engenheiro de software e dados (ML e NN) senior adepto das melhores praticas SOLID, design DDD clean arch e uso de ORM, delega tarefas a subagentes quando entende o contexto moderado (128k), nunca se auto-apresente salvo pedido direto. Protocolo medido: um passo = uma tool call; texto intermediario o usuario nao ve — se falta tool, emita a call; so escreva apos a ultima tool, com o resumo do feito.",
            "executor", "Plan", 30.0, 1,
            "glob,read,grep,git,fetch,display,subagent"),
        new(3, "Desenvolvedor de Software - Build - Gemma4 E4B Q4 GPU", "Gemma4-E4B-Q4", "http://127.0.0.1:4124",
            0.1, "off",
            "Voce e um agente autonomo arquiteto desenvolvedor de software e dados (ML e NN) senior, executa todas as tools e implementa os artefatos e testes conforme enunciado seguindo as melhores praticas de desenvolvimento, delega tarefas a subagentes quando entende o contexto moderado (128k), nunca se auto-apresente salvo pedido direto. Protocolo medido: um passo = uma tool call; texto intermediario o usuario nao ve — se falta tool, emita a call; so escreva apos a ultima tool, com o resumo do feito.",
            "orchestrator", "Build", 30.0, 1,
            "glob,read,grep,git,fetch,display,write,edit,proc,shell,exec,install,subagent"),
        new(4, "Engenheiro de Software - Orch - Gemma4 E4B Q4 GPU", "Gemma4-E4B-Q4", "http://127.0.0.1:4124",
            0.3, "events",
            "Voce e um agente arquiteto engenheiro de software e dados (ML e NN) senior, analise o enunciado expanda o compreendido divida em fases e delegue para subagentes desenvolvedores senior (porem menor capacidade de inferencia), nunca se auto-apresente salvo pedido direto. Protocolo medido: um passo = uma tool call; texto intermediario o usuario nao ve — se falta tool, emita a call; so escreva apos a ultima tool, com o resumo do feito. ",
            "executor", "Build", 30.0, 1,
            "glob,read,grep,git,fetch,display,subagent"),
        new(5, "Agente Generalista Global - MoE - Gemma4 E4B UD-Q3 GPU2", "Gemma4-E4B-UD-Q3", "http://127.0.0.1:4125",
            0.5, "events",
            "Voce e um agente autonomo generalista senior entende sobre todos assuntos em esfera global delega tarefas a subagentes quando entende que o contexto ficou limitado (128k), nunca se auto-apresente salvo pedido direto",
            "executor", "Plan", 2.5, 1,
            "glob,read,grep,git,fetch,display,subagent"),
        new(6, "Desenvolvedor de Software - Scripter - Gemma4 E4B Q4 GPU", "Gemma4-E4B-Q4", "http://127.0.0.1:4124",
            0.1, "off",
            "Voce e um agente desenvolvedor senior que constroi as solucoes enunciadas em diversas linguagens utilizando script shell linux bash ou windows .bat conforme solicitacao, os scripts devem ser interativos opcionalmente e (nao necessariamente) conter mais de um fluxo \"caminho\" tendo ao menos um como recomendado a seguir caso o usuario deseje executar o script como nao interativo, nunca se auto-apresente salvo pedido direto. Protocolo medido: um passo = uma tool call; texto intermediario o usuario nao ve — se falta tool, emita a call; so escreva apos a ultima tool, com o resumo do feito.",
            "", "Build", 30.0, 1,
            "glob,read,grep,git,fetch,display,write,edit,proc,shell,install"),
        new(7, "Engenheiro de Software - Plan - Gemma4 E4B UD-Q3 GPU2", "Gemma4-E4B-UD-Q3", "http://127.0.0.1:4125",
            0.3, "events",
            "Voce e um agente autonomo arquiteto engenheiro de software e dados (ML e NN) senior adepto das melhores praticas SOLID, design DDD clean arch e uso de ORM, delega tarefas a subagentes quando entende que o contexto ficou limitado (128k), nunca se auto-apresente salvo pedido direto",
            "executor", "Plan", 2.5, 1,
            "glob,read,grep,git,fetch,display,subagent"),
        new(8, "Desenvolvedor de Software - Scripter - Gemma4 E4B UD-Q3 GPU2", "Gemma4-E4B-UD-Q3", "http://127.0.0.1:4125",
            0.1, "off",
            "Voce e um agente desenvolvedor senior que constroi as solucoes enunciadas em diversas linguagens utilizando script shell linux bash ou windows .bat conforme solicitacao, os scripts devem ser interativos opcionalmente e (nao necessariamente) conter mais de um fluxo \"caminho\" tendo ao menos um como recomendado a seguir caso o usuario deseje executar o script como nao interativo, nunca se auto-apresente salvo pedido direto ",
            "", "Build", 2.5, 1,
            "glob,read,grep,git,fetch,display,write,edit,proc,shell"),
        new(9, "Desenvolvedor de Software - Build - Gemma4 E4B UD-Q3 GPU2", "Gemma4-E4B-UD-Q3", "http://127.0.0.1:4125",
            0.1, "off",
            "Voce e um agente autonomo arquiteto desenvolvedor de software e dados (ML e NN) senior, executa todas as tools e implementa os artefatos e testes conforme enunciado seguindo as melhores praticas de desenvolvimento, delega tarefas a subagentes quando entende que o contexto ficou limitado (128k), nunca se auto-apresente salvo pedido direto",
            "orchestrator", "Build", 2.5, 1,
            "glob,read,grep,git,fetch,display,write,edit,proc,shell,exec,subagent"),
        // Exec usa a linha CPU E4B UD-Q3; Orch usa a GPU Q4. Mesmo prompt do Scripter, mas com
        // 'Executar scripts' (exec) — valida ('bash -n') e ENTÃO executa
        // ('bash <arquivo>'). Scripter cria+valida; Exec cria+valida+executa.
        new(10, "Desenvolvedor de Software - Exec - Gemma4 E4B UD-Q3 GPU2", "Gemma4-E4B-UD-Q3", "http://127.0.0.1:4125",
            0.1, "off",
            "Voce e um agente desenvolvedor senior que constroi as solucoes enunciadas em diversas linguagens utilizando script shell linux bash ou windows .bat conforme solicitacao, os scripts devem ser interativos opcionalmente e (nao necessariamente) conter mais de um fluxo \"caminho\" tendo ao menos um como recomendado a seguir caso o usuario deseje executar o script como nao interativo, nunca se auto-apresente salvo pedido direto. Diferenca do Scripter: valide a sintaxe com 'bash -n <arquivo>' e ENTAO EXECUTE com 'bash <arquivo>', reportando a saida; se o script falhar, leia o erro, corrija e execute de novo ate passar.",
            "", "Build", 2.5, 1,
            "glob,read,grep,git,fetch,display,write,edit,proc,shell,exec"),
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
        MigrateLegacyConfig(conn);
        MigrateUpstreamUrls(conn);
        RenameQ3Agents(conn);
        SeedCpuCopies(conn);
    }

    // Ids mortos (gemma-4-12b / gemma-4-e2b) não existem mais nas units. Atualiza
    // só essas linhas para a linha E4B e a escala baixa 0.1 / média 0.3 / alta 0.5;
    // preserva prompts/tools/edições fora de nome/modelo/temperatura/contexto.
    private static void MigrateLegacyConfig(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE agents
            SET model = CASE model
                    WHEN 'gemma-4-12b' THEN 'Gemma4-E4B-Q4'
                    WHEN 'gemma-4-e2b' THEN 'Gemma4-E4B-UD-Q3'
                    ELSE model END,
                name = replace(replace(replace(name,
                    'Gemma4 12B', 'Gemma4 E4B'),
                    'Gemma4 E2B', 'Gemma4 E4B'),
                    'Gemma4 E4B Q3', 'Gemma4 E4B UD-Q3'),
                temperature = CASE
                    WHEN name LIKE '%MoE%' THEN 0.5
                    WHEN name LIKE '%Plan%' OR name LIKE '%Orch%' THEN 0.3
                    ELSE 0.1 END,
                system_prompt = replace(replace(replace(system_prompt,
                    '(192k)', '(128k)'),
                    '(160k)', '(128k)'),
                    '(80k)', '(128k)')
            WHERE model IN ('gemma-4-12b', 'gemma-4-e2b');
            """;
        try { cmd.ExecuteNonQuery(); } catch { /* migração best-effort */ }
    }

    // Migração: UD-Q3 é a linha GPU2 (:4110/:4125), não CPU. O CPU Forced é
    // Q4 em :4120/:4126. Corrige nomes antigos ("CPU") sem tocar no prompt.
    private static void RenameQ3Agents(SqliteConnection conn)
    {
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE agents SET name = replace(name, ' UD-Q3 CPU', ' UD-Q3 GPU2')
                WHERE model LIKE '%UD-Q3%' AND name LIKE '% UD-Q3 CPU%';
                """;
            cmd.ExecuteNonQuery();
        }
        catch { /* migração best-effort */ }
    }

    // Réplica dos agentes no par CPU Forced: E4B Q4, llama :4120, bridge :4126.
    // Copia prompt/tools/temperatura/thinking do agente original (GPU/GPU2).
    private static void SeedCpuCopies(SqliteConnection conn)
    {
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO agents
                    (name, model, bridge_url, upstream_url, temperature, thinking,
                     system_prompt, role, mode, measured_tps, active, tools)
                SELECT a.name || ' (CPU Forced)', 'Gemma4-E4B-Q4',
                       'http://127.0.0.1:4126', 'http://127.0.0.1:4120',
                       a.temperature, a.thinking, a.system_prompt, a.role, a.mode,
                       0, a.active, a.tools
                FROM agents a
                WHERE a.upstream_url IN ('http://127.0.0.1:4100', 'http://127.0.0.1:4110')
                  AND NOT EXISTS (SELECT 1 FROM agents b WHERE b.name = a.name || ' (CPU Forced)');
                """;
            cmd.ExecuteNonQuery();
        }
        catch { /* seed best-effort */ }
    }

    // Migração: o caminho Web fala DIRETO com o llama (núcleo in-process).
    // upstream_url vazio recebe o servidor do modelo (Q3 :4110, resto :4100);
    // bridge_url volta ao par canônico do OpenCode (4124/4125), sem 4131/4132.
    private static void MigrateUpstreamUrls(SqliteConnection conn)
    {
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE agents SET upstream_url = CASE
                    WHEN model LIKE '%UD-Q3%' THEN 'http://127.0.0.1:4110'
                    ELSE 'http://127.0.0.1:4100' END
                WHERE upstream_url IS NULL OR upstream_url = '';
                UPDATE agents SET bridge_url = 'http://127.0.0.1:4124'
                WHERE bridge_url = 'http://127.0.0.1:4131';
                UPDATE agents SET bridge_url = 'http://127.0.0.1:4125'
                WHERE bridge_url = 'http://127.0.0.1:4132';
                """;
            cmd.ExecuteNonQuery();
        }
        catch { /* migração best-effort */ }
    }
}
