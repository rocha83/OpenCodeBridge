using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Rochas.OpenCodeBridge.Core;

/// <summary>Configuração da guarda de contexto (lida de flags/appsettings pelo host).</summary>
/// <param name="Enabled">Guarda ligada.</param>
/// <param name="Threshold">Fração do ctx que dispara a guarda (ex.: 0.95).</param>
/// <param name="Tokens">Tamanho do contexto TOTAL do modelo servido (0 = inativa).</param>
/// <param name="Slots">Slots paralelos do llama (<c>-np</c>): o ctx total é dividido
/// entre eles e uma request só cabe em um slot. A API do llama não informa o número
/// de slots, então o host declara (<c>--context-slots</c>).</param>
public readonly record struct ContextGuardSettings(bool Enabled, double Threshold, int Tokens, int Slots = 1);

/// <summary>
/// Guarda de contexto: detecta, na resposta do upstream, que o prompt chegou ao
/// limiar do ctx do modelo. Regra do núcleo — cliente só informa os parâmetros.
/// </summary>
public static class ContextGuard
{
    /// <summary>Avalia se o prompt do upstream atingiu o limiar.</summary>
    /// <param name="json">Corpo JSON do upstream (com <c>usage.prompt_tokens</c>).</param>
    /// <param name="settings">Configuração em vigor.</param>
    /// <param name="promptTokens">Tokens de prompt lidos (0 quando ilegível).</param>
    /// <param name="fraction">Fração do ctx consumida.</param>
    /// <returns>True quando dispara (>= limiar).</returns>
    public static bool Tripped(string json, ContextGuardSettings settings, out int promptTokens, out double fraction)
    {
        promptTokens = 0;
        fraction = 0;
        if (!settings.Enabled || settings.Tokens <= 0) return false;
        try { promptTokens = JsonNode.Parse(json)?.AsObject()?["usage"]?["prompt_tokens"]?.GetValue<int>() ?? 0; }
        catch { return false; }
        // O llama divide o ctx TOTAL entre N slots paralelos; uma request só cabe no
        // slot. A API do llama não informa o número de slots, então dividimos pelo
        // configurado (--context-slots). Com o TOTAL o guard nunca dispara antes do
        // 400 seco do slot (medido 2026-10-30: 524288 total, slot 131072, request
        // de 142k barrada pelo llama antes de qualquer compactação).
        int slots = settings.Slots > 0 ? settings.Slots : 1;
        double perSlot = (double)settings.Tokens / slots;
        fraction = promptTokens / perSlot;
        return fraction >= settings.Threshold;
    }

    /// <summary>Fração do ctx por-slot que dispara a compactação (95%).</summary>
    public const double DefaultThreshold = 0.95;

    /// <summary>
    /// Fator chars-&gt;tokens calibrado no proprio llama (<c>/tokenize</c>, 2026-10-31):
    /// prosa 3.83, C# 3.66, C# denso 3.22. O <c>chars/4</c> anterior (calibrado em
    /// prosa) subestimava ate' 1.24x em codigo: o estimado ficava abaixo do teto e a
    /// compactacao virava no-op, deixando a proxima request estourar o slot (medido no
    /// <c>E-mission-algo-cs-hanoi</c>: 140146 tokens reais contra teto de 124518 = 95%
    /// de 131072 por slot). Usa-se 3.2 (o pior caso medido) para errar sempre pra
    /// CIMA — o lado seguro: o guard disparar um pouco antes nunca deixa a request
    /// estourar seco.
    /// </summary>
    private const double CharsPerToken = 3.2;

    /// <summary>Args de tool call maiores que isto viram marcador no corte.</summary>
    private const int ArgsKeepChars = 300;

    /// <summary>Content de tool result maiores que isto truncam no corte agressivo.</summary>
    private const int TrimmedChars = 600;

    /// <summary>Turnos recentes preservados no primeiro passe do corte agressivo.</summary>
    private const int KeepRecentInitial = 8;

    /// <summary>Piso do corte recursivo: sem chegar aqui, preserva-se system + 1a troca.</summary>
    private const int MinKeep = 2;

    /// <summary>
    /// Nudge de economia: texto unico que a bridge anexa quando a sessao estoura o
    /// ctx. Ensinha a FORMA CERTA (edit/read, sem reemitir arquivo inteiro) sem exibir
    /// a forma errada — mesma regra do <c>ToolHelp</c>, num lugar so'.
    /// </summary>
    public const string NudgeText =
        "ATENCAO: o historico desta sessao estourou o contexto do modelo. Aja com economia: " +
        "para mudar um arquivo existente use `edit` (substitui um TRECHO), nunca reemita o arquivo inteiro com `write`; " +
        "para conferir estado use `read` com offset/limit; nao releia arquivos grandes por inteiro nem repita build/log longo. " +
        "O arquivo em disco e a fonte da verdade.";

    /// <summary>Piso/teto do fator calibrado (protege contra 400 ilegivel ou corpo vazio).</summary>
    private const double MinCharsPerToken = 0.5;
    private const double MaxCharsPerToken = 64.0;

    /// <summary>Tamanho da sessao em chars JSON — a mesma base de <see cref="EstimateTokens"/>.</summary>
    /// <param name="messages">Mensagens no formato chat.</param>
    /// <returns>Soma dos chars serializados de cada mensagem.</returns>
    public static int Size(JsonArray messages)
    {
        int chars = 0;
        foreach (var m in messages) chars += m?.ToJsonString().Length ?? 0;
        return chars;
    }

    /// <summary>
    /// Fator chars-&gt;tokens medido na PROPRIA 400 do upstream: o corpo enviado e o
    /// <c>prompt_tokens</c> lido sao do MESMO request, entao a razao medida e' a do
    /// modelo para aquele conteudo — nao depende de constante de prosa.
    /// </summary>
    /// <remarks>
    /// A constante fixa (<see cref="CharsPerToken"/>, media de prosa inglesa) subestima
    /// em conteudo denso: no <c>E-mission-algo-cs-hanoi</c> de 2026-10-31 a sessao de
    /// 189665 chars media 137964 tokens reais (1.37 chars/token). Com 3.2 o guard
    /// estimava 59k e a compactacao virava no-op — o cliente reenviou o mesmo corpo
    /// 23x ate' o step estourar o tempo (FAIL 241s, causa=contexto).
    /// </remarks>
    /// <param name="chars">Chars da sessao/corpo (base de <see cref="EstimateTokens"/>).</param>
    /// <param name="promptTokens">Tokens reais lidos na 400 (0 = sem calibracao).</param>
    /// <returns>Fator a usar na estimativa; <see cref="CharsPerToken"/> quando nao ha calibracao.</returns>
    public static double CalibrateCharsPerToken(int chars, int promptTokens)
        => promptTokens > 0 && chars > 0
            ? Math.Clamp((double)chars / promptTokens, MinCharsPerToken, MaxCharsPerToken)
            : CharsPerToken;

    /// <summary>Estima tokens do historico (calibrado em codigo, ver <see cref="CharsPerToken"/>).</summary>
    private static int EstimateTokens(JsonArray messages) => EstimateTokens(messages, CharsPerToken);

    /// <summary>Estima tokens com o fator informado (o calibrado pela 400, quando ha).</summary>
    private static int EstimateTokens(JsonArray messages, double charsPerToken)
        => (int)Math.Ceiling(Size(messages) / (charsPerToken > 0 ? charsPerToken : CharsPerToken));

    /// <summary>Teto em tokens (0 = inativo): <paramref name="threshold"/> do ctx por-slot.</summary>
    private static int BudgetTokens(int contextTokens, int slots, double threshold)
        => contextTokens <= 0 ? 0 : (int)((double)contextTokens / Math.Max(1, slots) * threshold);

    /// <summary>
    /// Detecta, no corpo do erro do llama (400 <c>exceed_context_size</c>), o numero de
    /// tokens do prompt que estourou o slot. A bridge usa para decidir a escalada de
    /// recuperacao (nudge -&gt; compactar -&gt; 413) em vez de repassar o 400 cru.
    /// </summary>
    /// <param name="body">Corpo do erro vindo do upstream.</param>
    /// <param name="promptTokens">Tokens lidos da mensagem (0 quando nao achou).</param>
    /// <returns>true quando e' um estouro de contexto.</returns>
    public static bool LooksLikeOverflow(string? body, out int promptTokens)
    {
        promptTokens = 0;
        if (string.IsNullOrEmpty(body)) return false;
        if (!body.Contains("exceed_context_size", StringComparison.OrdinalIgnoreCase)
            && !body.Contains("exceeds the available context size", StringComparison.OrdinalIgnoreCase))
            return false;
        // Mensagem do llama: "request (140146 tokens) exceeds the available context size (131072 tokens)".
        Match m = Regex.Match(body, @"request\s*\((\d+)\s*tokens?\)", RegexOptions.IgnoreCase);
        if (m.Success && int.TryParse(m.Groups[1].Value, out int t)) promptTokens = t;
        return true;
    }

    /// <summary>
    /// Rotina ÚNICA de compactaçao agressiva da sessao (2a tentativa) — a resposta ao
    /// gatilho que <see cref="Tripped"/> detecta e que o host devolve como 413
    /// <c>context_compact_required</c> quando nem ela cabe. Calcula o teto como
    /// <paramref name="threshold"/> do ctx por-slot (ctx TOTAL / slots paralelos do
    /// llama, <c>-np</c>) e, quando o historico passa do teto, resume os turnos antigos:
    /// args de tool call viram marcador (o arquivo em disco e a fonte; o modelo rele com
    /// <c>read</c>) e resultados antigos sao truncados preservando o inicio (onde ficam
    /// os marcadores de falha que o breaker le). Nao remove mensagens: o pareamento
    /// tool_call/tool_result segue valido.
    ///
    /// Corte RECURSIVO (2026-10-31): um passe so' nao basta quando os turnos recentes
    /// intactos ja passam do teto — o modelo reescreve o arquivo inteiro a cada
    /// <c>write</c>, entao as ultimas mensagens sozinhas chegam a dezenas de k tokens.
    /// Cada volta encolhe a janela preservada (8 -&gt; 4 -&gt; 2) ate' caber ou bater o
    /// piso (system + 1a troca).
    /// </summary>
    /// <param name="messages">Mensagens no formato chat (mutadas).</param>
    /// <param name="contextTokens">Ctx TOTAL servido (ex.: 524288).</param>
    /// <param name="slots">Slots paralelos do llama (ex.: 4).</param>
    /// <param name="threshold">Fração do ctx por-slot que dispara (default 95%).</param>
    /// <returns>true quando compactou.</returns>
    public static bool Compact(JsonArray messages, int contextTokens, int slots, double threshold = DefaultThreshold)
        => Compact(messages, contextTokens, slots, threshold, CharsPerToken);

    /// <summary>
    /// Compacta a sessao com o fator de tokens informado (o calibrado pela 400, quando ha).
    /// Escalada em tres passos: 1) trunca o miolo; 2) REMOVE o miolo quando so' truncar
    /// nao adianta; 3) trunca a cauda, que o passe 1 preserva. O arquivo em disco e a
    /// fonte da verdade — o modelo relê com <c>read</c> depois.
    /// </summary>
    /// <param name="messages">Mensagens no formato chat (mutadas).</param>
    /// <param name="contextTokens">Ctx TOTAL servido (ex.: 524288).</param>
    /// <param name="slots">Slots paralelos do llama (ex.: 4).</param>
    /// <param name="threshold">Fração do ctx por-slot que dispara (default 95%).</param>
    /// <param name="charsPerToken">Fator chars-&gt;tokens (o calibrado pela 400, quando ha).</param>
    /// <returns>true quando a sessao encolheu.</returns>
    public static bool Compact(JsonArray messages, int contextTokens, int slots,
        double threshold, double charsPerToken)
    {
        int budgetTokens = BudgetTokens(contextTokens, slots, threshold);
        if (budgetTokens <= 0 || messages.Count <= MinKeep) return false;
        if (EstimateTokens(messages, charsPerToken) <= budgetTokens) return false;

        bool any = false;
        int keepRecent = KeepRecentInitial;
        while (EstimateTokens(messages, charsPerToken) > budgetTokens && keepRecent > MinKeep)
        {
            any |= CompactPass(messages, keepRecent);
            keepRecent = Math.Max(MinKeep, keepRecent / 2);
        }
        // 2) So' truncar nao adianta quando a massa ja' esta' no teto: medido no hanoi
        //    de 2026-10-31, ~400 mensagens de ~465 chars — capar em 600 tirava 3% e o
        //    request continuava 400. Ai' o miolo sai inteiro (system + 1a troca + cauda
        //    ficam); o arquivo em disco cobre o historico perdido.
        if (EstimateTokens(messages, charsPerToken) > budgetTokens)
            any |= DropMiddle(messages, budgetTokens, charsPerToken);
        // 3) A cauda nunca passou pelo passe 1; se ela e' o que estoura, capa tambem.
        if (EstimateTokens(messages, charsPerToken) > budgetTokens)
            any |= TrimTail(messages);
        return any;
    }

    /// <summary>Teto da recuperacao: o 400 provou que a janela cheia nao cabe no slot.</summary>
    public const double RecoveryThreshold = 0.70;

    /// <summary>
    /// Politica UNICA de recuperacao de uma <c>413 context_compact_required</c>: calibra o
    /// fator chars-&gt;tokens com os numeros reais da propria 400 e compacta ate' caber.
    /// </summary>
    /// <remarks>
    /// Quem decide o orcamento e' o Core, nao o consumidor. Medido no
    /// <c>E-mission-algo-cs-hanoi</c> de 2026-10-31: o teste escolhia <c>ctxTokens/2</c>
    /// e reenviava o MESMO corpo 23x, porque o orcamento dele (62259 estimados) ficava
    /// acima do que a estimativa media (59187) — a compactacao era no-op e o step
    /// estourou o tempo com causa=contexto.
    /// </remarks>
    /// <param name="messages">Sessao em formato chat (mutada).</param>
    /// <param name="contextTokens">Ctx TOTAL servido.</param>
    /// <param name="slots">Slots paralelos do llama.</param>
    /// <param name="promptTokens">Tokens reais lidos na 400 (0 = sem calibracao).</param>
    /// <param name="before">Chars da sessao antes da recuperacao.</param>
    /// <param name="after">Chars da sessao depois da recuperacao.</param>
    /// <returns>true quando a sessao encolheu; false quando nao ha o que cortar — o
    /// consumidor deve falhar rapido e NUNCA reenviar o mesmo corpo.</returns>
    public static bool Recover(JsonArray messages, int contextTokens, int slots,
        int promptTokens, out int before, out int after)
    {
        before = Size(messages);
        after = before;
        if (before <= 0) return false;
        double ratio = CalibrateCharsPerToken(before, promptTokens);
        Compact(messages, contextTokens, slots, RecoveryThreshold, ratio);
        after = Size(messages);
        return after < before;
    }

    /// <summary>Le os tokens reais da mensagem de 413 devolvida pela bridge.</summary>
    /// <param name="message">Corpo/mensagem do erro 413.</param>
    /// <returns>Tokens lidos; 0 quando a mensagem nao os traz.</returns>
    public static int PromptTokensFrom(string? message)
    {
        if (string.IsNullOrEmpty(message)) return 0;
        var m = Regex.Match(message, @"(\d+)\s*tokens");
        return m.Success && int.TryParse(m.Groups[1].Value, out int t) ? t : 0;
    }

    /// <summary>Remove mensagens antigas (a partir de <see cref="MinKeep"/>) ate' caber no teto.</summary>
    private static bool DropMiddle(JsonArray messages, int budgetTokens, double charsPerToken)
    {
        bool any = false;
        int keepTail = KeepRecentInitial;
        while (EstimateTokens(messages, charsPerToken) > budgetTokens && keepTail > MinKeep)
        {
            int cut = Math.Max(MinKeep, messages.Count - keepTail);
            if (cut <= MinKeep) break;
            for (int i = cut - 1; i >= MinKeep; i--) messages.RemoveAt(i);
            any = true;
            keepTail = Math.Max(MinKeep, keepTail / 2);
        }
        return any;
    }

    /// <summary>Aplica o teto de conteudo tambem as mensagens recentes (o passe 1 preserva).</summary>
    private static bool TrimTail(JsonArray messages)
    {
        bool touched = false;
        for (int i = MinKeep; i < messages.Count; i++)
            touched |= TrimMessage(messages[i] as JsonObject, TrimmedChars);
        return touched;
    }

    /// <summary>Um passe de corte agressivo: args de tool viram marcador e resultados antigos truncam.</summary>
    private static bool CompactPass(JsonArray messages, int keepRecent)
    {
        int cut = Math.Max(MinKeep, messages.Count - keepRecent);
        bool touched = false;
        for (int i = 2; i < cut; i++)
            if (TrimMessage(messages[i] as JsonObject, TrimmedChars)) touched = true;
        return touched;
    }

    /// <summary>Recorta um args longo (vira marcador) e um content longo (trunca) da mensagem.</summary>
    private static bool TrimMessage(JsonObject? mo, int contentCap)
    {
        if (mo is null) return false;
        bool touched = false;
        if (mo["tool_calls"] is JsonArray tcs)
            foreach (var tc in tcs)
            {
                if (tc?["function"] is not JsonObject fn) continue;
                string args = fn["arguments"] is JsonValue av && av.TryGetValue<string>(out var s) ? s : "";
                if (args.Length <= ArgsKeepChars) continue;
                string path = "";
                try { path = JsonNode.Parse(args)?["path"]?.GetValue<string>() ?? ""; } catch { }
                fn["arguments"] = JsonValue.Create(path.Length > 0
                    ? "{\"path\":\"" + path.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\",\"content\":\"(omitido: o arquivo em disco e a fonte; use read)\"}"
                    : "{\"path\":\"(omitido: o arquivo em disco e a fonte; use read)\"}");
                touched = true;
            }
        if (mo["content"] is JsonValue cv && cv.TryGetValue<string>(out var c) && c.Length > contentCap)
        {
            mo["content"] = JsonValue.Create(c[..contentCap] + "\n...(omitido)");
            touched = true;
        }
        return touched;
    }
}
