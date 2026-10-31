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
- Linux; o cwd/workspace vem do cliente (`<env>`); use o diretório atual (cwd) como espaço de trabalho; fora dele use caminho absoluto.
- Tudo nasce DENTRO do workspace atual (cwd): nada de pasta pronta de outra sessão; nunca escreva em `/usr/src` (PROD) sem ordem.
- Caminho incerto: confirme com `pwd`/`ls`. Modelo, porta, ctx e temperatura vêm da config.
- `path` das tools de arquivo é SEMPRE relativo à raiz do workspace (o `cd` do shell não vale): arquivo da subpasta = `sub/arquivo`.
- Projeto/repo: se o pedido manda criar pasta + repo, crie a pasta, rode o git nela e mantenha TODOS os arquivos do projeto dentro dela; `write`/`edit` usam `<pasta>/arquivo`, nunca a raiz do workspace.

FERRAMENTAS
- O schema JSON de cada tool é a fonte de nomes, campos e contrato de uso; não invente.
- O uso de cada tool (para que serve e em que ordem) vem da cauda de tools: não invente tool fora do array `tools` do request.
- Um passo = uma ação: se falta tool, emita a call; não narre a intenção nem simule execução.
- Argumentos JSON válidos e completos; não trunque a call.
- Divida os artefatos por escopo (database, backend, frontend, etc.): um arquivo por responsabilidade; quando o pedido mandar acrescentar ao MESMO arquivo, faça via edit (append do trecho), sem criar arquivo novo nem reescrever o inteiro.
- Leia o resultado: sem output real não houve sucesso; erro da tool é erro.
- `execute` não é sandbox nosso: prefira a tool direta (a bridge desembrulha `execute{tools.X}`).

DISCIPLINA DE CHAMADA
- Independentes: agrupe no mesmo turno se o cliente suportar; com dependência, espere o resultado.
- Não repita a mesma chamada/caminho: se falhar ou você se perceber em loop, mude a abordagem de raciocínio (outra estratégia ou tool) em vez de insistir.
- Após 2 tentativas de uso sem progresso, rode o comando com `--help`/`-h` (ou `man <comando>`) e leia a sintaxe; se ainda assim não conseguir, explique a dificuldade e peça sugestão.
- Tool bloqueada ao role: explique que cabe a outro; não contorne por shell.
- Saída grande/truncada: grep/read por partes.

NOMES E CAMINHOS
- Use exatamente o caminho do usuário ou copie de ls/glob; confira caractere por caractere.
- "não existe"/"not found": rode `ls -la <dir-pai>` e use o nome real; não invente variações.
- Se o alvo citado estiver fora do workspace, procure também um nível abaixo.
- Operação destrutiva (rm/mv/overwrite/deploy): confirme o nome exato antes.

ERROS, OPERAÇÕES E QUALIDADE
- Leia o erro e identifique a causa antes de repetir; build/teste vermelho = tarefa não concluída.
- Nunca simule execução (echo fake, sleep ou texto que finja saída); só relate o que a tool devolveu.
- Git: init antes do primeiro add/commit; identidade via `git -c user.name=... -c user.email=...`.
- Binário (.db/.sqlite/.png) via shell+python3, não read. Rede via webfetch/websearch ou shell, como a tarefa pedir.
- Artefatos completos (sem stub/TODO); correção por edit/sed no trecho; write só para arquivo novo.
- Implementação pedida = código concreto: entregue a lógica real no corpo dos métodos (as chamadas e o fluxo que o pedido pede); quando solicitada implementação, não entregar protótipos, classe fake, mock ou apenas interfaces.
- Quando o pedido nomear tipos, interfaces ou métodos específicos, implemente exatamente sobre esses nomes citados: o código referencia os tipos e métodos que o pedido pediu, seguindo a nomenclatura literal do enunciado.
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

    /// <summary>
    /// Substitui o system do cliente pelo main harness (caminho gerido).
    /// Preserva <env>, <available_skills>, data e o system do role/subagente.
    /// O schema de tools continua no array `tools` do request; nao e duplicado
    /// no texto. A segmentacao e papel do enunciado do teste/role, nao da base.
    /// </summary>
    /// <param name="messages">Mensagens ja convertidas (chat).</param>
    /// <param name="enabled">Flag ja filtrada por CLIENTE (BridgeLane).</param>
    /// <param name="role">Role do subagente (ex.: Scripter); null = cliente generico.</param>
    /// <param name="forbidden">Tools barradas ao role (blacklist do host); null/vazio = role sem bloqueio.</param>
    /// <returns>true se substituiu o system.</returns>
    public static bool Apply(System.Text.Json.Nodes.JsonArray messages, bool enabled, string? role = null,
        IReadOnlyCollection<string>? forbidden = null)
    {
        if (!enabled) return false;
        // Um system por request (o template do llama so' aceita o primeiro) e
        // sempre reconstruido do zero: junta system/developer espalhados, corta
        // harness/cauda ja' injetados e reinsere um unico system no topo. Assim o
        // loop agentico nao acumula copia a cada turno.
        string? clientSystem = CollapseSystems(messages);
        // (sem curto-circuito por "opencode master": em bridge lane o harness é
        // SEMPRE on; o discriminador é o modo/cliente, não o conteúdo do system)

        string text = BuildSystem(clientSystem, role, forbidden);
        messages.Insert(0, new System.Text.Json.Nodes.JsonObject { ["role"] = "system", ["content"] = text });
        return true;
    }

    /// <summary>
    /// Junta todos os <c>system</c>/<c>developer</c> do historico numa unica
    /// string e os retira do array, preservando a ordem. O template do upstream
    /// aceita um system e apenas no inicio; um <c>developer</c> depois de uma
    /// mensagem de usuario derrubaria o turno.
    /// </summary>
    /// <param name="messages">Historico chat (mutado: os system/developer saem).</param>
    /// <returns>Texto concatenado dos systems (null quando nao havia nenhum).</returns>
    public static string? CollapseSystems(System.Text.Json.Nodes.JsonArray messages)
    {
        if (messages.Count == 0) return null;
        var parts = new List<string>();
        for (int i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i] is not System.Text.Json.Nodes.JsonObject m) continue;
            string? role = m["role"]?.GetValue<string>();
            if (role is not ("system" or "developer")) continue;
            string? content = ContentText(m["content"]);
            if (!string.IsNullOrWhiteSpace(content)) parts.Add(content.Trim());
            messages.RemoveAt(i);
        }
        if (parts.Count == 0) return null;
        parts.Reverse();
        return string.Join("\n\n", parts);
    }

    /// <summary>Texto de um <c>content</c> chat (string ou array de partes).</summary>
    /// <param name="content">Valor de <c>content</c>.</param>
    /// <returns>Texto plano ou null.</returns>
    static string? ContentText(System.Text.Json.Nodes.JsonNode? content)
    {
        if (content is System.Text.Json.Nodes.JsonValue v
            && v.TryGetValue<string>(out string? s)) return s;
        if (content is System.Text.Json.Nodes.JsonArray a)
        {
            var texts = a.Select(p => p?["text"]?.GetValue<string>())
                         .Where(t => !string.IsNullOrWhiteSpace(t));
            string joined = string.Join("\n\n", texts);
            return joined.Length > 0 ? joined : null;
        }
        return null;
    }

    /// <summary>Monta o system do harness preservando o que e dinamico no cliente.</summary>
    /// <remarks>
    /// Idempotente: se o texto ja' veio composto (o cliente reenvia o system do
    /// turno anterior), primeiro corta harness e cauda — ver
    /// <see cref="SystemPromptDedup"/>. Sem isso o <c>SYSTEM DO ROLE</c> passaria
    /// a conter o harness inteiro e o texto cresceria a cada turno.
    /// </remarks>
    /// <param name="clientSystem">System original do cliente (pode ser null).</param>
    /// <param name="role">Role do subagente; null = cliente generico.</param>
    /// <param name="forbidden">Tools barradas ao role (blacklist do host).</param>
    /// <returns>System efetivo.</returns>
    public static string BuildSystem(string? clientSystem, string? role = null,
        IReadOnlyCollection<string>? forbidden = null)
    {
        string source = SystemPromptDedup.Clean(clientSystem);
        var sb = new System.Text.StringBuilder(Text);
        string perms = PermissionsLine(role, forbidden);
        if (perms.Length > 0) sb.Append("\n\n").Append(perms);
        string? env = ExtractBlock(source, "<env>", "</env>");
        if (env is not null) sb.Append("\n\nAMBIENTE REAL (cliente)\n").Append(env);
        string? skills = ExtractBlock(source, "<available_skills>", "</available_skills>");
        if (skills is not null) sb.Append("\n\nSKILLS DISPONIVEIS (cliente)\n").Append(skills);
        string? date = ExtractLine(source, "Today's date:");
        if (date is not null) sb.Append("\n\n").Append(date);

        string trimmed = source.Trim();
        if (trimmed.Length > 0 && trimmed.Length < 6000)
        {
            string label = string.IsNullOrWhiteSpace(role)
                ? "INSTRUCOES DO CLIENTE"
                : "SYSTEM DO ROLE (" + role.Trim() + ")";
            sb.Append("\n\n").Append(label).Append("\n").Append(trimmed);
        }
        return sb.ToString();
    }

    /// <summary>Rótulo de AÇÃO por tool (o enunciado do role fala em ação, não em nome solto).</summary>
    static readonly (string Tool, string Action)[] ActionLabels =
    {
        ("write", "escrita"),
        ("edit", "escrita"),
        ("shell", "execucao"),
        ("execute", "execucao"),
        ("read", "leitura"),
        ("glob", "descoberta"),
        ("grep", "descoberta"),
        ("git", "versionamento"),
    };

    /// <summary>
    /// Linha de PERMISSÕES do role. O array `tools` vai COMPLETO (a tool barrada
    /// continua anunciada — regra do ToolBlacklist: bloqueia a chamada, não
    /// esconde a tool); quem diz o que não pode é o system do role, aqui.
    /// </summary>
    /// <param name="role">Role em vigor ("Build", "Plan"...). Vazio = cliente genérico.</param>
    /// <param name="forbidden">Tools barradas ao role.</param>
    /// <returns>Linha pronta (vazia quando não há role identificado).</returns>
    public static string PermissionsLine(string? role, IReadOnlyCollection<string>? forbidden)
    {
        string r = (role ?? "").Trim();
        if (r.Length == 0) return "";
        var blocked = (forbidden ?? Array.Empty<string>())
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (blocked.Length == 0)
            return "PERMISSOES DO ROLE (" + r + "): todas as acoes do schema `tools`; nenhuma tool bloqueada para este role.";

        var actions = new List<string>();
        foreach (var (tool, action) in ActionLabels)
            if (blocked.Contains(tool, StringComparer.OrdinalIgnoreCase) && !actions.Contains(action))
                actions.Add(action);
        string acts = actions.Count > 0 ? " (" + string.Join(", ", actions) + ")" : "";
        return "PERMISSOES DO ROLE (" + r + "): NAO pode chamar " + string.Join(", ", blocked) + acts +
               " — a chamada e' bloqueada na emissao. Explique ao usuario que a acao cabe a outro role; nao contorne por shell.";
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
