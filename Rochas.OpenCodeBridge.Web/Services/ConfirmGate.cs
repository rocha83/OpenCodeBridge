// Portao de confirmacao: mutacoes pedem aval via botoes Sim/Nao (sem
// mensagem na bolha) ou "pode fazer" digitado. Aprovacao clicada resolve
// o pending e o loop executa na hora; timeout/Nao nega com pedido claro.
// /Chat/Tool (clique manual) nao passa por aqui: clique = aval implicito.
namespace Rochas.OpenCodeBridge.Web.Services;

public static class ConfirmGate
{
    // ---- RISCO: padroes de comando que exigem aval, por agent (opcional).
    // Diferente do NeedsConfirmation fixo (write/edit/push/install), aqui o
    // operador declara o que e risco no SEU ambiente: appsettings
    // RiskToolsAlerts: { "<agenteId>": ["\\brm\\b", "\\bmv\\b", ...] }.
    // Vazio = so vale o NeedsConfirmation fixo (comportamento anterior).
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string,
        System.Collections.Generic.List<System.Text.RegularExpressions.Regex>> RiskToolsAlerts =
        new(System.StringComparer.OrdinalIgnoreCase);

    /// <summary>Carrega os padroes de risco do appsettings (padrao invalido e ignorado).</summary>
    public static void LoadRiskAlerts(Microsoft.Extensions.Configuration.IConfigurationSection? section)
    {
        if (section is null) return;
        foreach (var agent in section.GetChildren())
        {
            var rx = new System.Collections.Generic.List<System.Text.RegularExpressions.Regex>();
            foreach (var item in agent.GetChildren().Select(c => c.Value).OfType<string>())
            {
                if (string.IsNullOrWhiteSpace(item)) continue;
                try
                {
                    rx.Add(new System.Text.RegularExpressions.Regex(item.Trim(),
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase
                        | System.Text.RegularExpressions.RegexOptions.CultureInvariant,
                        TimeSpan.FromMilliseconds(100)));
                }
                catch (System.Text.RegularExpressions.RegexParseException) { /* padrao invalido: ignora */ }
            }
            if (rx.Count > 0) RiskToolsAlerts[agent.Key] = rx;
        }
    }

    /// <summary>A chamada casa com algum padrao de risco declarado p/ o agente?</summary>
    public static bool IsRisk(string? agentId, string name, string args)
    {
        if (string.IsNullOrEmpty(agentId)) return false;
        if (!RiskToolsAlerts.TryGetValue(agentId, out var rx) || rx.Count == 0) return false;
        string hay = (name ?? "") + " " + (args ?? "");
        foreach (var r in rx)
            if (r.IsMatch(hay)) return true;
        return false;
    }

    /// <summary>Rotulo do risco declarado, para a pergunta ao usuario.</summary>
    public static string RiskLabel(string name, string args)
    {
        string cmd = name.Equals("shell", System.StringComparison.OrdinalIgnoreCase)
            || name is "bash" or "sh" or "exec" || name.Equals("proc", System.StringComparison.OrdinalIgnoreCase)
            ? ShellInspector.ExtractCommand(args ?? "") : args ?? "";
        cmd = cmd.Replace('\n', ' ').Trim();
        return cmd.Length > 120 ? cmd[..120] + "..." : cmd;
    }

    // Palavras de aval (explicitas; "sim"/"ok" sozinhos nao valem).
    private static readonly string[] Approvals =
    {
        "pode sim", "pode fazer", "autorizo", "aprovado", "confirmo",
        "pode executar", "vai em frente", "manda ver", "prossiga",
        "pode continuar", "pode commitar", "pode dar push",
    };

    // Ruido de caminho/arquivo, removido antes de procurar o aval: evita
    // confundir nome de arquivo ("/tmp/aprovado.txt") com aval ("aprovado").
    private static readonly System.Text.RegularExpressions.Regex PathNoise =
        new(@"[\w.~\-\\]*[\\/][\w.~\-\\]*|\b[\w\-]+\.[\w]+\b",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    // Um regex por aval, com fronteira de palavra (nao pega substring).
    private static readonly System.Text.RegularExpressions.Regex[] ApprovalRes =
        Approvals.Select(a => new System.Text.RegularExpressions.Regex(
            @"(?<!\w)" + System.Text.RegularExpressions.Regex.Escape(a) + @"(?!\w)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase
            | System.Text.RegularExpressions.RegexOptions.Compiled)).ToArray();

    // Aval fixo por (sessao, tool): aprovada uma vez (Sim ou "pode fazer"),
    // a tool segue liberada na sessao sem re-perguntar. "Nao"/timeout nega
    // so a call atual (nao fixa nada).
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> Allowed = new();

    /// <summary>Rótulo amigável da categoria p/ a pergunta ao usuário.</summary>
    public static string Label(string name, string args) => ToolSlot(name) switch
    {
        "write" => "escrever em arquivos",
        "edit" => "editar arquivos",
        "shell" => IsGitPush(ShellInspector.ExtractCommand(args ?? ""))
            ? "enviar código (git push)"
            : ShellInspector.Classify(ShellInspector.ExtractCommand(args ?? "")) == ShellInspector.ShellClass.Install
            ? "instalar pacotes" : "executar comando",
        _ => (name ?? "").Trim(),
    };

    /// <summary>Pergunta ao usuário com o rótulo da categoria.</summary>
    public static string AskFor(string name, string args) =>
        $"O agente está tentando {Label(name, args)}, você permite?";
    /// <summary>Rotulo final da pergunta: o do risco declarado tem precedencia
    /// sobre a categoria generica (diz o QUE sera rodado, nao so a familia).</summary>
    public static string LabelFor(string? agentId, string name, string args)
        => IsRisk(agentId, name, args) ? RiskLabel(name, args) : Label(name, args);

    /// <summary>Slot da tool p/ o aval fixo (familia shell conta como uma).</summary>
    public static string ToolSlot(string name)
    {
        string n = (name ?? "").Trim().ToLowerInvariant();
        return n is "shell" or "bash" or "sh" or "exec" or "proc" ? "shell" : n;
    }

    public static bool IsAllowed(int sessionId, string name) =>
        Allowed.ContainsKey(sessionId + ":" + ToolSlot(name));

    public static void Allow(int sessionId, string name) =>
        Allowed[sessionId + ":" + ToolSlot(name)] = true;

    public static void ClearSession(int sessionId)
    {
        string prefix = sessionId + ":";
        foreach (var k in Allowed.Keys)
            if (k.StartsWith(prefix, StringComparison.Ordinal))
                Allowed.TryRemove(k, out _);
    }

    /// <summary>Call mutante que exige aval? (push/install via shell, write, edit,
    /// e qualquer comando que case com um padrao de risco do agente).</summary>
    public static bool NeedsConfirmation(string name, string args, string? agentId = null)
    {
        if (IsRisk(agentId, name, args)) return true;
        string n = (name ?? "").Trim();
        if (n.Equals("write", StringComparison.OrdinalIgnoreCase)
            || n.Equals("edit", StringComparison.OrdinalIgnoreCase)) return true;
        if (n.Equals("shell", StringComparison.OrdinalIgnoreCase)
            || n.Equals("bash", StringComparison.OrdinalIgnoreCase)
            || n.Equals("sh", StringComparison.OrdinalIgnoreCase)
            || n.Equals("exec", StringComparison.OrdinalIgnoreCase)
            || n.Equals("proc", StringComparison.OrdinalIgnoreCase))
        {
            string cmd = ShellInspector.ExtractCommand(args ?? "");
            if (IsGitPush(cmd)) return true;
            // Instalacao de pacotes pede aval uma vez (sticky por tool na sessao).
            if (ShellInspector.Classify(cmd) == ShellInspector.ShellClass.Install) return true;
            return false;
        }
        return false;
    }

    /// <summary>Detecta git push (fora de aspas: -m "push it" nao conta).</summary>
    public static bool IsGitPush(string command)
    {
        string bare = System.Text.RegularExpressions.Regex.Replace(command ?? "", @"(""[^""]*""|'[^']*')", " ");
        return System.Text.RegularExpressions.Regex.IsMatch(bare, @"\bgit\b[\s\S]*\bpush\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    /// <summary>Ultima mensagem user trouxe aval explicito?</summary>
    public static bool ApprovedBy(string? lastUser)
    {
        if (string.IsNullOrWhiteSpace(lastUser)) return false;
        string clean = PathNoise.Replace(lastUser, " ");
        return ApprovalRes.Any(r => r.IsMatch(clean));
    }

    /// <summary>Ultimo texto user em messages (formato chat).</summary>
    public static string? LastUserText(System.Text.Json.Nodes.JsonArray messages)
    {
        for (int i = messages.Count - 1; i >= 0; i--)
            if (messages[i] is System.Text.Json.Nodes.JsonObject o
                && o["role"]?.GetValue<string>() == "user"
                && o["content"]?.GetValue<string>() is string t && !string.IsNullOrWhiteSpace(t))
                return t;
        return null;
    }

    /// <summary>Ultimo texto user no input (formato Responses).</summary>
    public static string? LastUserInputText(System.Text.Json.Nodes.JsonArray input)
    {
        for (int i = input.Count - 1; i >= 0; i--)
        {
            if (input[i] is not System.Text.Json.Nodes.JsonObject o) continue;
            if (o["type"]?.GetValue<string>() != "message") continue;
            if (o["role"]?.GetValue<string>() != "user") continue;
            if (o["content"] is not System.Text.Json.Nodes.JsonArray parts) continue;
            foreach (var p in parts)
                if (p is System.Text.Json.Nodes.JsonObject po
                    && po["text"] is System.Text.Json.Nodes.JsonValue tv
                    && tv.TryGetValue<string>(out string? t) && !string.IsNullOrWhiteSpace(t))
                    return t;
        }
        return null;
    }

    public const string DenyMessage =
        "Aguardando confirmacao do usuario: esta acao (push/escrita) exige aval explicito. Descreva o que vai fazer e peca 'pode fazer'; sem o aval, nao execute.";

    // Aprovals pendentes por sessao (Sim/Nao da UI resolve sem mensagem).
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, (int Id, TaskCompletionSource<bool> Tcs)> Pending = new();
    private static int _seq;

    public static int NextId() => System.Threading.Interlocked.Increment(ref _seq);

    /// <summary>Registra espera de clique (120s); true = Sim.</summary>
    public static async Task<bool> WaitAsync(int sessionId, int approveId, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Pending[sessionId] = (approveId, tcs);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            using (linked.Token.Register(() => tcs.TrySetResult(false))) { }
            return await tcs.Task.ConfigureAwait(false);
        }
        finally { Pending.TryRemove(sessionId, out _); }
    }

    /// <summary>Responde pending (endpoint /Chat/Approve).</summary>
    public static bool Answer(int sessionId, int approveId, bool approved)
    {
        if (Pending.TryGetValue(sessionId, out var p) && p.Id == approveId)
            return p.Tcs.TrySetResult(approved);
        return false;
    }
}
