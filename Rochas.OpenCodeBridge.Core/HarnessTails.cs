using System.Text;

namespace Rochas.OpenCodeBridge.Core;

/// <summary>
/// Caudas de inducao do harness. Os textos abaixo sao fallback do codigo; o
/// deployment ajusta em appsettings (harnessBehaviorTail/harnessToolsTail) ou
/// CLI (--harness-behavior-tail/--harness-tools-tail). O nucleo base
/// (MainHarness) e hardcoded e nao passa por aqui.
/// </summary>
/// <remarks>
/// A cauda de tools e COMPOSTA a partir das tools presentes no array `tools` do
/// request: a dica nunca anuncia tool que o schema nao tem (o modelo copia o
/// literal e, com `tool_choice=required`, a call fora do schema e' rejeitada
/// pelo parser do upstream). Com a lista nula/dev completa, o texto sai integral
/// — o default do deployment continua sendo o mesmo.
/// </remarks>
public static class HarnessTails
{
    /// <summary>
    /// Delta de comportamento da bridge (o nucleo ja mora no MainHarness).
    /// Mantido curto de proposito: so o que e especifico deste canal/workspace.
    /// </summary>
    public const string DefaultBehavior =
        "COMPORTAMENTO (delta da bridge; a base ja cobre postura, caminhos e erros): " +
        "Formato: em conversa use Unicode (setas, <=, pi, somatorio) em vez de LaTeX; use $...$ so em bloco de codigo ou formula, onde o cliente renderiza. " +
        "TOOLS PRIMEIRO: em enunciado que pede execucao, a PRIMEIRA saida e a tool call; ZERO linha de prosa antes do primeiro disparo e nenhum texto entre uma call e a proxima. " +
        "Excecao: no maximo 1 linha entre calls dependentes, so para indicar o proximo passo. " +
        "So escreva ao usuario depois da ultima acao, com evidencia real (trecho de log, saida de comando). " +
        "PERSISTENCIA: cada resultado de tool e insumo da proxima call no MESMO turno; nao espere 'siga' e nao encerre com 'vou fazer', 'iniciando' ou confirmacao. " +
        "PRIORIDADE: se o master mandar explicar antes de comando nao trivial, execute primeiro e mostre a saida; a explicacao, se necessaria, vem depois, em 1 linha. " +
        "CONTEXTO PEQUENO (128k): voce atua em contexto de 128k; evite compor conteudo excessivamente grande de uma so vez, segmente o raciocinio em fases; se precisar, use subagentes utilizando mesmo agente/modelo que o seu.";

    /// <summary>
    /// Variante do delta para pedido SEM tools. A cauda de tools e a linha
    /// "TOOLS PRIMEIRO" empurram o modelo a disparar tool call inexistente
    /// (ele devolve o JSON da chamada como TEXTO e trunca a resposta). Em
    /// pedido de texto/plano a cauda vira so formato.
    /// </summary>
    public const string DefaultBehaviorNoTools =
        "COMPORTAMENTO (delta da bridge; a base ja cobre postura, caminhos e erros): " +
        "Formato: em conversa use Unicode (setas, <=, pi, somatorio) em vez de LaTeX; use $...$ so em bloco de codigo ou formula, onde o cliente renderiza. " +
        "PEDIDO SEM TOOLS: responda em TEXTO com o CODIGO/PRODUTO COMPLETO pedido (no corpo da mensagem). " +
        "NAO entregue 'plano de implementacao', 'plano arquitetural', 'estrutura do projeto', sumario nem indice de passos: o texto deve ser o entregavel final. " +
        "NAO elida codigo: proibido '// ...', '/* ... */', 'resto igual' ou comentario no lugar do corpo — escreva cada metodo/trecho por inteiro. " +
        "Quando o pedido terminar com 'ENTREGUE O ARTEFATO COMPLETO EM TEXTO, SEM TOOL CALLS' (ou 'MOSTRE-ME COMO MODO PLANO EM TEXTO'), " +
        "entregue o ARTEFATO FINAL no corpo; NAO responda com documento de plano, 'plano de implementacao' nem 'aguardando confirmacao'.";

    // ---- Pecas da cauda de tools (uma frase = uma peca) -------------------
    // `Requires` = grupos de tools: TODOS os grupos precisam estar satisfeitos;
    // dentro do grupo, QUALQUER membro satisfaz (ex.: write OU edit).

    /// <summary>
    /// Cabecalho da cauda de tools: sentinela estavel de "a cauda entrou no
    /// system final", presente na composicao completa E na composta pelo schema.
    /// </summary>
    public const string ToolsHeader =
        "MODO DE USO DAS TOOLS (regras transversais; o contrato de cada tool vai no schema):";

    const string OneStepLine =
        "Um passo = uma acao; argumentos JSON completos; leia o resultado antes de repetir.";

    const string JsonArgsLine =
        "JSON DO ARGS: o conteudo de `write`/`edit` e' string JSON — quebra de linha = `\\n` (barra + n, dois caracteres) e aspas = `\\\"`; nunca quebre a linha crua dentro da string (o parser recusa).";

    const string EditAppendLine =
        "EDITAR ACRESCENTANDO: o `oldString` e' o trecho que CONTINUA no arquivo; o `newString` traz esse mesmo trecho + o texto novo, para o arquivo terminar com os dois.";

    const string BigFileLine =
        "ARQUIVO GRANDE: grave um `write` curto e complete com `edit` (args menor = menos risco de truncar).";

    const string IndependentLine =
        "Independentes no mesmo turno; dependentes esperam. Nao repita a mesma chamada/caminho; em loop, mude a abordagem.";

    const string ExecuteSandboxLine =
        "`execute` nao e sandbox: a bridge desembrulha tools.X; bloqueio de role nao se contorna por shell.";

    /// <summary>
    /// Ordem de uso quando o request tem escrita E execucao: primeiro o
    /// artefato, depois a validacao. E' o que evita o modelo disparar `shell`
    /// como primeira acao ("compilando") sem ter escrito nada.
    /// </summary>
    public const string OrderingLine =
        "AO CODIFICAR: monte a solucao com `write`/`edit` (o arquivo e' o artefato); so entao use `shell` para build de validacao, execucao de testes ou versionamento do codigo.";

    const string DotnetLine =
        "DOTNET: com mais de um projeto na pasta, `--project <pasta>` e ambiguo: aponte o ARQUIVO (`<dir>/<nome>.fsproj` ou `.csproj`).";

    const string FileLine =
        "ARQUIVO: pedido que manda criar/gravar arquivo entrega via `write` (conteudo completo); devolver o codigo em prosa no chat NAO conclui o pedido.";

    const string ExistingProjectLine =
        "PROJETO EXISTENTE: edite os arquivos do projeto; `dotnet new` so em pasta vazia (segundo .csproj/.fsproj na mesma pasta quebra `dotnet run --project <pasta>`).";

    const string FileRepoFlowLine =
        "FLUXO ARQUIVO+REPO (exemplo; use os nomes do seu pedido): `mkdir` da pasta → `write` com o path da pasta (`<pasta>/<arquivo>`) → `git init`/`add`/`commit` com o mesmo `path` → `git log` e a saida colada no historico (passo a passo comentado no help da tool git).";

    const string ProofLine =
        "PROVA: depois de `write`/`edit`, `read` o arquivo — o conteudo dele e' o texto que a tool devolveu; historico de comando e' a saida literal colada (ex.: `git log`, com os hashes que vieram).";

    const string SubagentLine =
        "SUBAGENTE: o schema de cada tool viaja no array `tools` do request; preserve o array e o system do role ao delegar; nao troque schema por prosa.";

    static readonly (string Text, string[][] Requires)[] Segments =
    {
        (OneStepLine, Array.Empty<string[]>()),
        (JsonArgsLine, new[] { new[] { "write", "edit" } }),
        (EditAppendLine, new[] { new[] { "edit" } }),
        (BigFileLine, new[] { new[] { "write" }, new[] { "edit" } }),
        (IndependentLine, Array.Empty<string[]>()),
        (ExecuteSandboxLine, Array.Empty<string[]>()),
        (OrderingLine, new[] { new[] { "write" }, new[] { "shell" } }),
        (DotnetLine, new[] { new[] { "shell" } }),
        (FileLine, new[] { new[] { "write" } }),
        (ExistingProjectLine, new[] { new[] { "shell" } }),
        (FileRepoFlowLine, new[] { new[] { "write" }, new[] { "shell" }, new[] { "git" } }),
        (ProofLine, new[] { new[] { "read" }, new[] { "write", "edit" } }),
        (SubagentLine, Array.Empty<string[]>()),
    };

    /// <summary>
    /// Linha "Prefira a tool dedicada" composta SÓ com as tools presentes.
    /// Com `names` nulo devolve a lista completa (default do deployment).
    /// </summary>
    /// <param name="names">Nomes das tools no array `tools` (null = todas).</param>
    /// <returns>Linha da cauda (vazia quando nenhuma tool conhecida está presente).</returns>
    public static string ToolsLine(IReadOnlyCollection<string>? names)
    {
        var parts = new List<string>();
        foreach (string group in new[] { "file", "discovery", "net" })
        {
            string text = group switch
            {
                "file" => Members(names, "read", "write", "edit") is { Length: > 0 } f ? f + " para arquivo" : "",
                "discovery" => Members(names, "glob", "grep") is { Length: > 0 } d ? d + " para descobrir" : "",
                _ => Members(names, "webfetch", "websearch") is { Length: > 0 } n ? n + " para rede" : "",
            };
            if (text.Length > 0) parts.Add(text);
        }
        bool hasShell = names is null || names.Contains("shell", StringComparer.OrdinalIgnoreCase);
        if (hasShell)
            // Comandos concretos dentro da shell: "executar" solto fazia o
            // modelo escolher shell para qualquer coisa (inclusive compilar
            // como PRIMEIRA acao, sem artefato).
            parts.Add("shell para executar comandos (build/teste como `dotnet build`, `npm`, `python3`; versionamento como `git add`/`commit`/`log`)");
        else if (names!.Contains("git", StringComparer.OrdinalIgnoreCase))
            parts.Add("git para versionar");
        return parts.Count == 0 ? "" : "Prefira a tool dedicada: " + string.Join(", ", parts) + ".";
    }

    static string Members(IReadOnlyCollection<string>? names, params string[] candidates)
    {
        if (names is null) return string.Join("/", candidates);
        var have = candidates.Where(c => names.Contains(c, StringComparer.OrdinalIgnoreCase)).ToArray();
        return string.Join("/", have);
    }

    static bool Satisfied(string[][] requires, IReadOnlyCollection<string>? names)
    {
        if (requires.Length == 0 || names is null) return true;
        foreach (string[] group in requires)
            if (!group.Any(g => names.Contains(g, StringComparer.OrdinalIgnoreCase))) return false;
        return true;
    }

    /// <summary>
    /// Monta a cauda de tools para o conjunto de tools do request. Com `names`
    /// nulo devolve o texto completo (mesmo resultado de <see cref="DefaultTools"/>).
    /// </summary>
    /// <param name="names">Nomes das tools no array `tools` (null = todas).</param>
    /// <returns>Cauda de tools.</returns>
    public static string ComposeTools(IReadOnlyCollection<string>? names)
    {
        var sb = new StringBuilder(ToolsHeader);
        string line = ToolsLine(names);
        if (line.Length > 0) sb.Append(' ').Append(line);
        foreach (var (text, requires) in Segments)
        {
            if (!Satisfied(requires, names)) continue;
            sb.Append(' ').Append(text);
        }
        string built = sb.ToString();
        // Pares "write/edit" das frases que sobraram: com um dos dois ausente, o
        // literal vira a tool que existe (a frase nao ensina a que nao existe).
        if (names is not null && !names.Contains("edit", StringComparer.OrdinalIgnoreCase))
        {
            string only = names.Contains("write", StringComparer.OrdinalIgnoreCase) ? "`write`" : "`edit`";
            built = built.Replace("`write`/`edit`", only, StringComparison.Ordinal);
        }
        return built;
    }

    /// <summary>
    /// Cauda de tools completa (todas as tools): fallback do deployment e
    /// referência de "nada foi filtrado".
    /// </summary>
    public static readonly string DefaultTools = ComposeTools(null);

    /// <summary>
    /// A cauda de tools entrou no system final?
    /// </summary>
    /// <remarks>
    /// A cauda configurada nao e' o unico texto possivel: ela e' COMPOSTA a
    /// partir do schema do request, entao um subconjunto de tools omite frases
    /// inteiras. Procurar o texto inteiro devolve falso para cauda presente; a
    /// prova sai do cabecalho (<see cref="ToolsHeader"/>), que existe em toda
    /// composicao. Um override do deployment e' casado pelo proprio prefixo.
    /// </remarks>
    /// <param name="system">System efetivamente enviado ao upstream.</param>
    /// <param name="tools">Cauda configurada (<see cref="DefaultTools"/> ou override).</param>
    /// <returns>True quando a cauda (completa, composta ou override) esta' no texto.</returns>
    public static bool HasToolsTail(string? system, string? tools)
    {
        if (string.IsNullOrEmpty(system) || string.IsNullOrEmpty(tools)) return false;
        string marker = string.Equals(tools, DefaultTools, StringComparison.Ordinal)
            ? ToolsHeader
            : tools.Length <= 40 ? tools : tools[..40];
        return system.Contains(marker, StringComparison.Ordinal);
    }

    // O FsMini (tabela de sintaxe F#) foi ARQUIVADO em 2026-10-30 junto da
    // trilha F#: ver Rochas.OpenCodeBridge.Test/Archived/FsMiniTail.cs. Nenhum
    // teste ativo usa a cauda (C#/WAR nao recebem sintaxe F#); o texto fica
    // preservado no arquivado para retomada, sem virar contrato de producao.

    /// <summary>
    /// Compõe o system prompt com as caudas. Com harness desligado devolve o
    /// texto original (tubo puro do OpenCode).
    /// </summary>
    /// <param name="system">System prompt original.</param>
    /// <param name="harness">Liga/desliga as caudas.</param>
    /// <param name="behavior">Cauda de comportamento (pode ser vazia).</param>
    /// <param name="tools">Cauda de tools (pode ser vazia).</param>
    /// <param name="hasTools">Request traz array `tools`? Sem tools a cauda de
    /// tools e a linha "TOOLS PRIMEIRO" saem (o modelo devolveria JSON de tool
    /// como texto).</param>
    /// <param name="toolNames">Nomes das tools do request; quando a cauda é a
    /// default, ela é recomposta só com o que existe (null = todas).</param>
    /// <returns>System prompt composto.</returns>
    public static string Compose(string? system, bool harness, string? behavior, string? tools,
        bool hasTools = true, IReadOnlyCollection<string>? toolNames = null)
    {
        if (!harness) return system ?? "";
        // O cliente reenvia o system do turno anterior: cortar a cauda ja'
        // injetada deixa a composicao idempotente (sem data/behavior/tools
        // duplicados nem contexto crescendo por turno).
        string t = SystemPromptDedup.StripTails(system).Trim();
        // Data do host no proprio harness: modelos pequenos inventam data de
        // memoria; o pedido "hoje" passa a ter uma fonte verdadeira no contexto.
        string hoje = "DATA ATUAL DO HOST: " + System.DateTime.Now.ToString("yyyy-MM-dd") +
                      " (use esta data quando o pedido citar hoje).";
        t = (t.Length > 0 ? t + "\n\n" : "") + hoje;
        // Pedido sem tools: troca o delta padrao (que manda disparar tool) pela
        // variante de texto. Override do deployment e preservado.
        string? beh = behavior;
        if (!hasTools && string.Equals(behavior, DefaultBehavior, StringComparison.Ordinal))
            beh = DefaultBehaviorNoTools;
        if (!string.IsNullOrWhiteSpace(beh)) t = (t.Length > 0 ? t + "\n\n" : "") + beh;
        if (hasTools && !string.IsNullOrWhiteSpace(tools))
        {
            // Override do operador e' literal; o default entra composto pelo schema.
            string tail = string.Equals(tools, DefaultTools, StringComparison.Ordinal)
                ? ComposeTools(toolNames)
                : tools;
            t = (t.Length > 0 ? t + "\n\n" : "") + tail;
        }
        return t;
    }
}
