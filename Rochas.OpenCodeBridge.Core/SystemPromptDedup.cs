namespace Rochas.OpenCodeBridge.Core;

/// <summary>
/// Remove do system prompt o que a propria bridge injetou. No loop agentico o
/// cliente reenvia, a cada turno, o system do turno anterior — inclusive quando
/// ele ja' veio composto pelo harness. Sem este corte o texto cresce por turno
/// (harness dentro de "SYSTEM DO ROLE", cauda sobre cauda) e o modelo passa a
/// ler a mesma regra N vezes, com o contexto encurtando a toa.
/// </summary>
/// <remarks>
/// Duas marcas, uma por superficie de injecao:
/// <list type="bullet">
/// <item><see cref="HarnessMarker"/>: prefixo do <see cref="MainHarness.Text"/>;</item>
/// <item><see cref="TailMarker"/>: inicio da cauda de <see cref="HarnessTails.Compose"/>.</item>
/// </list>
/// As duas rotinas sao idempotentes: aplicar sobre o proprio resultado devolve o
/// mesmo texto.
/// </remarks>
public static class SystemPromptDedup
{
    /// <summary>
    /// Primeira linha do <see cref="MainHarness.Text"/> — sentinela de "harness
    /// ja' injetado". Existe para nao repetir a constante em cada consumidor.
    /// </summary>
    public const string HarnessMarker =
        "Você é um arquiteto desenvolvedor de software e dados (ML/NN) sênior";

    /// <summary>
    /// Primeira linha da cauda de composicao: tudo dela para a frente e' injecao
    /// da bridge (data do host + comportamento + tools), nunca do cliente.
    /// </summary>
    public const string TailMarker = "DATA ATUAL DO HOST: ";

    /// <summary>Rotulos que o <see cref="MainHarness.BuildSystem"/> usa para o system do cliente.</summary>
    static readonly string[] ClientLabels = { "SYSTEM DO ROLE (", "INSTRUCOES DO CLIENTE" };

    /// <summary>O texto ja' carrega o main harness?</summary>
    /// <param name="system">System prompt.</param>
    /// <returns>True quando a sentinela do harness aparece.</returns>
    public static bool HasHarness(string? system)
        => !string.IsNullOrEmpty(system) && system.Contains(HarnessMarker, StringComparison.Ordinal);

    /// <summary>O texto ja' carrega a cauda composta (data do host + behavior + tools)?</summary>
    /// <param name="system">System prompt.</param>
    /// <returns>True quando a sentinela da cauda aparece.</returns>
    public static bool HasTails(string? system)
        => !string.IsNullOrEmpty(system) && system.IndexOf(TailMarker, StringComparison.Ordinal) >= 0;

    /// <summary>
    /// Devolve so a parte do CLIENTE de um system ja' composto pelo
    /// <see cref="MainHarness.BuildSystem"/>. O harness vem antes do rotulo do
    /// cliente ("SYSTEM DO ROLE (...)" / "INSTRUCOES DO CLIENTE"); o que esta
    /// depois do ULTIMO rotulo e' o texto original — o resto e' injecao nossa,
    /// inclusive a permissao do role e os blocos <c>&lt;env&gt;</c>/skills.
    /// </summary>
    /// <param name="system">System prompt recebido.</param>
    /// <returns>Texto do cliente (vazio quando so' havia harness).</returns>
    public static string StripHarness(string? system)
    {
        if (string.IsNullOrEmpty(system)) return "";
        if (!HasHarness(system)) return system;
        int best = -1;
        foreach (string label in ClientLabels)
        {
            int i = system.LastIndexOf(label, StringComparison.Ordinal);
            if (i > best) best = i;
        }
        if (best < 0) return "";                       // harness sem payload do cliente
        int nl = system.IndexOf('\n', best);
        return nl >= 0 ? system[(nl + 1)..] : "";
    }

    /// <summary>
    /// Corta a cauda injetada por <see cref="HarnessTails.Compose"/>. Ela e'
    /// sempre um sufixo (data do host, comportamento, tools), entao tudo a partir
    /// da sentinela sai — o texto do cliente fica intacto.
    /// </summary>
    /// <param name="system">System prompt recebido.</param>
    /// <returns>Texto sem a cauda (o proprio texto quando nao havia cauda).</returns>
    public static string StripTails(string? system)
    {
        if (string.IsNullOrEmpty(system)) return "";
        int i = system.IndexOf(TailMarker, StringComparison.Ordinal);
        return i < 0 ? system : system[..i].TrimEnd();
    }

    /// <summary>Corte completo: harness (prefixo) + cauda (sufixo).</summary>
    /// <param name="system">System prompt recebido.</param>
    /// <returns>Texto original do cliente.</returns>
    public static string Clean(string? system) => StripTails(StripHarness(system));
}
