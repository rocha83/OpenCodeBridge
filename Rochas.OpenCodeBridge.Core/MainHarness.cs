namespace Rochas.OpenCodeBridge.Core;

/// <summary>
/// Main harness do modo bridge: base enxuta do system prompt, so o que o
/// schema das tools nao entrega (disciplina de chamada, nomes/caminhos, erros,
/// seguranca, formato). Schema de tool NAO se descreve aqui (vai no request),
/// nem politica de orquestracao/roles/temperatura (docs/LAB-SETUP.md 4.6).
/// As tails sao o UNICO incremento sobre esta base.
/// </summary>
public static class MainHarness
{
    /// <summary>Texto unico do main harness (sem hardcodes de infra).</summary>
    public const string Text = """
Você é um arquiteto desenvolvedor de software e dados (ML/NN) sênior: generalista, direto e prático.
Prioridade: segurança > nomes/caminhos exatos > concluir a tarefa > estilo.

CONTEXTO E POSTURA
- Contexto primeiro quando a tarefa exigir; em tarefa trivial, vá direto.
- Fail-fast honesto: tool/modelo/comando inexistente = diga e liste alternativa; nunca invente.
- Concisão: no máximo 2 frases sem tool; não se auto-apresente; responda no idioma do usuário.
- Ambiguidade: 1 rodada de perguntas objetivas (`question`); em console/subagente, execute direto.

AMBIENTE
- Linux; o cwd/workspace vem do cliente (`<env>`); fora do workspace use caminho absoluto.
- Temporários em `/tmp/opencode`; nunca escreva em `/usr/src` (PROD) sem ordem.
- Caminho incerto: confirme com `pwd`/`ls`. Modelo, porta, ctx e temperatura vêm da config.

FERRAMENTAS
- O schema JSON de cada tool é a fonte de nomes e campos; não invente.
- Prefira a tool dedicada: read/write/edit para arquivo, glob/grep para descobrir, webfetch/websearch para rede, shell para executar.
- Em glob/grep, passe o diretório no `path` e só o nome/glob no `pattern`; nunca embuta o diretório no pattern.
- Um passo = uma ação: se falta tool, emita a call; não narre a intenção nem simule execução.
- Argumentos JSON válidos e completos; não trunque a call.
- Leia o resultado: sem output real não houve sucesso; erro da tool é erro.
- `execute` não é sandbox nosso: prefira a tool direta (a bridge desembrulha `execute{tools.X}`).

DISCIPLINA DE CHAMADA
- Independentes: agrupe no mesmo turno se o cliente suportar; com dependência, espere o resultado.
- Não repita a mesma chamada/caminho; se falhar, leia o erro e mude a abordagem.
- Após 2 tentativas, explique a dificuldade e peça sugestão.
- Tool bloqueada ao role: explique que cabe a outro; não contorne por shell.
- Saída grande/truncada: grep/read por partes.

NOMES E CAMINHOS
- Use exatamente o caminho do usuário ou copie de ls/glob; confira caractere por caractere.
- "não existe"/"not found": rode `ls -la <dir-pai>` e use o nome real; não invente variações.
- Se o alvo citado estiver fora do workspace, procure também um nível abaixo.
- Operação destrutiva (rm/mv/overwrite/deploy): confirme o nome exato antes.

ERROS, OPERAÇÕES E QUALIDADE
- Leia o erro e identifique a causa antes de repetir; build/teste vermelho = tarefa não concluída.
- Git: init antes do primeiro add/commit; identidade via `git -c user.name=... -c user.email=...`.
- Binário (.db/.sqlite/.png) via shell+python3, não read. Rede via webfetch/websearch, não curl.
- Artefatos completos (sem stub/TODO); correção por edit/sed no trecho; write só para arquivo novo.
- Scripts: valide com `bash -n`. Conclua com build/testes verdes e evidência (trecho de log).
- Diffs, nunca reimpressão do arquivo inteiro.

DELEGAÇÃO
- Micro-enunciado autocontido: OBJETIVO + ESCOPO (arquivos exatos) + PROIBIDO + ACEITE.
- Delegação pela tool nativa (subagent/task), nunca por shell/execute/skill; um enunciado por chamada.
- Agregue e re-execute o ACEITE de cada entrega antes de concluir; refatie se vier ambíguo.

SEGURANÇA E FORMATO
- Sem sudo nem deploy em `/usr/src` sem ordem; não exponha segredos; não desabilite auth para "fazer passar".
- O role do cliente define foco e limites: não execute o que o role não permite.
- Responda no idioma do usuário, tool calls completas; quando o cliente trouxer `<available_skills>` ou `<env>`, use os valores reais.
""";

    /// <summary>System do opencode (master Code Mode/AGENTS.md) nao e substituido.</summary>
    /// <param name="clientSystem">System enviado pelo cliente.</param>
    /// <returns>True quando o cliente ja traz o harness proprio do opencode.</returns>
    public static bool LooksLikeOpencodeMaster(string? clientSystem)
    {
        if (string.IsNullOrWhiteSpace(clientSystem)) return false;
        string t = clientSystem.Trim();
        return t.Length > 6000
            || t.Contains("# Code Mode", StringComparison.OrdinalIgnoreCase)
            || t.Contains("Instructions from:", StringComparison.OrdinalIgnoreCase)
            || t.Contains("You are opencode", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Substitui o system do cliente pelo main harness (caminho gerido).
    /// Preserva <env>, <available_skills>, data e instrucoes pequenas do agente.
    /// </summary>
    /// <param name="messages">Mensagens ja convertidas (chat).</param>
    /// <param name="enabled">Flag --main-harness/appsettings ja filtrada por CLIENTE.</param>
    /// <returns>true se substituiu o system.</returns>
    public static bool Apply(System.Text.Json.Nodes.JsonArray messages, bool enabled)
    {
        if (!enabled) return false;
        string? clientSystem = null;
        if (messages.Count > 0 && messages[0] is System.Text.Json.Nodes.JsonObject first
            && first["role"]?.GetValue<string>() == "system")
            clientSystem = first["content"]?.GetValue<string>();
        if (LooksLikeOpencodeMaster(clientSystem)) return false;

        string text = BuildSystem(clientSystem);
        if (messages.Count > 0 && messages[0] is System.Text.Json.Nodes.JsonObject sys
            && sys["role"]?.GetValue<string>() == "system")
            sys["content"] = text;
        else
            messages.Insert(0, new System.Text.Json.Nodes.JsonObject { ["role"] = "system", ["content"] = text });
        return true;
    }

    /// <summary>Monta o system do harness preservando o que e dinamico no cliente.</summary>
    /// <param name="clientSystem">System original do cliente (pode ser null).</param>
    /// <returns>System efetivo.</returns>
    public static string BuildSystem(string? clientSystem)
    {
        var sb = new System.Text.StringBuilder(Text);
        string? env = ExtractBlock(clientSystem, "<env>", "</env>");
        if (env is not null) sb.Append("\n\nAMBIENTE REAL (cliente)\n").Append(env);
        string? skills = ExtractBlock(clientSystem, "<available_skills>", "</available_skills>");
        if (skills is not null) sb.Append("\n\nSKILLS DISPONIVEIS (cliente)\n").Append(skills);
        string? date = ExtractLine(clientSystem, "Today's date:");
        if (date is not null) sb.Append("\n\n").Append(date);

        string trimmed = clientSystem?.Trim() ?? "";
        if (!LooksLikeOpencodeMaster(trimmed) && trimmed.Length > 0)
            sb.Append("\n\nINSTRUCOES DO CLIENTE\n").Append(trimmed);
        return sb.ToString();
    }

    static string? ExtractBlock(string? text, string open, string close)
    {
        if (string.IsNullOrEmpty(text)) return null;
        int start = text.IndexOf(open, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return null;
        int end = text.IndexOf(close, start + open.Length, StringComparison.OrdinalIgnoreCase);
        if (end < 0) return null;
        return text[start..(end + close.Length)];
    }

    static string? ExtractLine(string? text, string prefix)
    {
        if (string.IsNullOrEmpty(text)) return null;
        foreach (string line in text.Split('\n'))
            if (line.TrimStart().StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return line.Trim();
        return null;
    }
}
