using System.Text.Json.Nodes;

namespace Rochas.OpenCodeBridge.Core;

/// <summary>Perfil de um role de agente: temperatura e modo de thinking.</summary>
/// <param name="Temperature">Temperatura do role.</param>
/// <param name="Thinking">"events" (reasoning exposto) ou "off".</param>
public sealed record RoleProfile(double Temperature, string Thinking);

/// <summary>
/// Dinâmica de uso dos roles (temperatura e thinking por sufixo do model id),
/// no núcleo: <c>-plan</c>, <c>-build</c>, <c>-orch</c>, <c>-moe</c>,
/// <c>-scripter</c>, <c>-exec</c>. Nenhum cliente (API, Web UI, console,
/// testes) define esses valores por conta própria — todos leem daqui.
/// </summary>
public static class RoleProfiles
{
    /// <summary>Role de referência do DESVIO estratégico (retry/reconsideração).</summary>
    /// <remarks>
    /// Um turno de retry precisa de temperatura de desvio: reaproveita a do
    /// role de exploração (<c>plan</c>), que já é a mais aberta do fluxo de
    /// execução. Não existe literal espalhado — quem sobe a temperatura em
    /// retry consulta aqui (ver <see cref="Sampling.ForRetry"/>).
    /// </remarks>
    public const string DetourRole = "plan";

    /// <summary>Tabela default imutável (fonte única dos valores por role).</summary>
    static readonly Dictionary<string, RoleProfile> Defaults = new(StringComparer.OrdinalIgnoreCase)
    {
        ["plan"] = new(0.3, "events"),
        ["build"] = new(0.1, "off"),
        ["orch"] = new(0.3, "events"),
        ["moe"] = new(0.5, "events"),
        ["scripter"] = new(0.1, "off"),
        ["exec"] = new(0.1, "off"),
    };

    /// <summary>Tabela default: plan 0.3/events, build 0.1/off, orch 0.3/events, moe 0.5/events, scripter 0.1/off, exec 0.1/off.</summary>
    /// <returns>Cópia mutável da tabela.</returns>
    public static Dictionary<string, RoleProfile> DefaultCopy() => new(Defaults, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Temperatura de desvio (retry com reconsideração), lida do role
    /// <see cref="DetourRole"/> na tabela em vigor; sem tabela, do default.
    /// </summary>
    /// <param name="profiles">Tabela em vigor (null = default do Core).</param>
    /// <returns>Temperatura de referência para o retry.</returns>
    public static double DetourTemperature(IReadOnlyDictionary<string, RoleProfile>? profiles = null)
    {
        if (profiles is not null && profiles.TryGetValue(DetourRole, out RoleProfile? p) && p is not null)
            return p.Temperature;
        return Defaults[DetourRole].Temperature;
    }

    /// <summary>Resolve o perfil pelo sufixo do model id ("...-build" =&gt; build).</summary>
    /// <param name="modelId">Model id pedido pelo cliente.</param>
    /// <param name="profiles">Tabela em vigor.</param>
    /// <param name="role">Role encontrado (vazio quando não há sufixo).</param>
    /// <param name="profile">Perfil encontrado (null quando não há sufixo).</param>
    /// <returns>True quando o model id carrega um role conhecido.</returns>
    public static bool TryResolve(string? modelId, IReadOnlyDictionary<string, RoleProfile> profiles,
        out string role, out RoleProfile? profile)
    {
        role = "";
        profile = null;
        if (string.IsNullOrEmpty(modelId)) return false;
        foreach (var kv in profiles)
            if (modelId.EndsWith("-" + kv.Key, StringComparison.OrdinalIgnoreCase))
            {
                role = kv.Key;
                profile = kv.Value;
                return true;
            }
        return false;
    }

    /// <summary>Role capitalizado para exibição/log ("...-build" =&gt; "Build").</summary>
    /// <param name="modelId">Model id pedido.</param>
    /// <param name="profiles">Tabela em vigor.</param>
    /// <returns>Role ou string vazia.</returns>
    public static string RoleOf(string? modelId, IReadOnlyDictionary<string, RoleProfile> profiles)
    {
        if (!TryResolve(modelId, profiles, out string role, out _)) return "";
        return char.ToUpperInvariant(role[0]) + role[1..];
    }

    /// <summary>Aplica o bloco <c>profiles</c> da configuração sobre a tabela.</summary>
    /// <param name="profiles">Tabela em vigor (mutada).</param>
    /// <param name="cfg">Objeto <c>profiles</c> do appsettings.</param>
    /// <param name="globalTemp">Temperatura global da instância (fallback).</param>
    /// <param name="globalThinking">Thinking global da instância (fallback).</param>
    /// <returns>Quantidade de entradas aplicadas.</returns>
    public static int Apply(Dictionary<string, RoleProfile> profiles, JsonObject? cfg, double globalTemp, string globalThinking)
    {
        if (cfg is null) return 0;
        int applied = 0;
        foreach (var kv in cfg)
        {
            if (kv.Value is not JsonObject o) continue;
            double t = o["temperature"]?.GetValue<double>() ?? double.NaN;
            string th = o["thinking"]?.GetValue<string>() ?? "";
            profiles.TryGetValue(kv.Key, out RoleProfile? cur);
            if (double.IsNaN(t)) t = cur is { Temperature: > 0 } ? cur.Temperature : globalTemp;
            if (th is not ("events" or "off")) th = cur?.Thinking is { Length: > 0 } ct ? ct : globalThinking;
            profiles[kv.Key] = new(t, th);
            applied++;
        }
        return applied;
    }
}
