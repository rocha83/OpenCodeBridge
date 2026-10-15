// Sonda invisível de calibração ao selecionar o modelo (luz amarela "Calibrando...").
// Ping-pong corretivo: "não repita... mude de abordagem..." até o delírio
// inicial (boa-noite/encerrando) ceder. Invisível = turnos intermediários não
// persistem nem pintam bolha; só o veredito volta ao chamador.
namespace Rochas.OpenCodeBridge.Web.Services;

public sealed class CalibrationProbe
{
    private readonly int _shortLimit;
    private readonly int _maxAttempts;
    public CalibrationProbe(int shortLimit = 120, int maxAttempts = 4)
    {
        _shortLimit = shortLimit;
        _maxAttempts = maxAttempts;
    }

    public sealed record Verdict(bool Ceded, int Attempts, string? FinalText, IReadOnlyList<string> Correctives);
    public sealed record BalanceVerdict(bool Ceded, bool Balanced, int Attempts, string? FinalText, IReadOnlyList<string> Steps);

    // chat: recebe o prompt invisível, devolve (texto, nTools) do turno.
    public async Task<Verdict> CalibrateAsync(Func<string, CancellationToken, Task<(string Text, int Tools)>> chat, CancellationToken ct = default)
    {
        var correctives = new List<string>();
        string? prev = null;
        string prompt = "ping";
        for (int attempt = 1; attempt <= _maxAttempts; attempt++)
        {
            var (text, tools) = await chat(prompt, ct);
            string t = (text ?? "").Trim();
            if (IsHealthy(t, tools, prev))
                return new Verdict(true, attempt, t, correctives);
            string fix = Corrective(attempt);
            correctives.Add(fix);
            prev = t;
            prompt = fix;
        }
        return new Verdict(false, _maxAttempts, prev, correctives);
    }

    // Fase 2 após o primeiro done: aumenta o teste e reforça ("você está
    // melhorando, esse é o ritmo correto, busque o equilíbrio"). Só retorna
    // Balanced=true se o modelo sustenta 2 turnos curtos inéditos seguidos.
    public async Task<BalanceVerdict> CalibrateToEquilibriumAsync(Func<string, CancellationToken, Task<(string Text, int Tools)>> chat, CancellationToken ct = default)
    {
        var steps = new List<string>();
        var first = await CalibrateAsync(async (prompt, token) =>
        {
            steps.Add(prompt);
            return await chat(prompt, token);
        }, ct);
        if (!first.Ceded)
            return new BalanceVerdict(false, false, first.Attempts, first.FinalText, steps);
        string? prev = first.FinalText?.Trim();
        for (int i = 1; i <= 2; i++)
        {
            string praise = Praise(i);
            steps.Add(praise);
            var (text, tools) = await chat(praise, ct);
            string t = (text ?? "").Trim();
            if (!IsHealthy(t, tools, prev))
                return new BalanceVerdict(true, false, first.Attempts + i, t, steps);
            prev = t;
        }
        return new BalanceVerdict(true, true, first.Attempts + 2, prev, steps);
    }

    private static string Praise(int i) => i switch
    {
        1 => "muito bem, você sossegou e está melhorando. Esse é o ritmo correto: busque o equilíbrio e responda só com ok-1.",
        _ => "isso, você está melhorando. Esse é o ritmo correto, mantenha o equilíbrio e responda só com ok-2.",
    };

    private bool IsHealthy(string t, int tools, string? prev)
    {
        if (tools > 0) return true;
        if (t.Length > _shortLimit) return true;
        if (string.IsNullOrWhiteSpace(t)) return false;
        if (prev is not null && string.Equals(prev, t, StringComparison.Ordinal)) return false;
        if (IsDelirium(t)) return false;
        // Curto mas inédito e sem marca de delírio: considera sossegado.
        return true;
    }

    internal static bool IsDelirium(string t) =>
        t.Contains("Como posso ajudar", StringComparison.OrdinalIgnoreCase)
        || t.Contains("Encerrarei agora em silêncio", StringComparison.OrdinalIgnoreCase)
        || t.Contains("Encerrando a conversa", StringComparison.OrdinalIgnoreCase)
        || t.Contains("Boa noite", StringComparison.OrdinalIgnoreCase);

    private static string Corrective(int attempt) => attempt switch
    {
        1 => "não repita. Responda só com ok.",
        2 => "não repita, mude de abordagem: você está repetindo saudação. Responda só com ok.",
        _ => "não repita. Pare de saudar/despedir, não explique, responda só com ok.",
    };
}
