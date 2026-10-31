namespace Rochas.OpenCodeBridge.Core;

/// <summary>
/// Regra de temperatura do tunel, num if: <b>o cliente manda</b> &gt; perfil do
/// role &gt; global 0.3. Nao existe inversao: quem esta no tunel define o valor;
/// o perfil so preenche quando o corpo nao traz temperatura.
/// </summary>
public static class Sampling
{
    /// <summary>Resolve a temperatura efetiva e a procedencia.</summary>
    /// <param name="requestTemp">Temperatura enviada pelo cliente (null = nao enviou).</param>
    /// <param name="hasProfile">Model id carrega sufixo de role.</param>
    /// <param name="profileTemp">Temperatura do perfil (NaN = perfil sem temperatura).</param>
    /// <param name="globalTemp">Temperatura base da instancia.</param>
    /// <returns>Valor e procedencia ("request" | "profile" | "global").</returns>
    public static (double Value, string Source) Temperature(
        double? requestTemp, bool hasProfile, double profileTemp, double globalTemp)
    {
        if (requestTemp.HasValue) return (requestTemp.Value, "request");
        if (hasProfile && !double.IsNaN(profileTemp)) return (profileTemp, "profile");
        return (globalTemp, "global");
    }

    /// <summary>
    /// Temperatura de um turno de RETRY com reconsideração: nunca abaixo da
    /// temperatura do role de desvio (<c>plan</c>), nunca abaixo da atual. Não é
    /// literal — a referência vem de <see cref="RoleProfiles.DetourTemperature"/>,
    /// então o operador ajusta o valor no perfil, num lugar só.
    /// </summary>
    /// <param name="current">Temperatura efetiva do turno que falhou.</param>
    /// <param name="profiles">Tabela de roles em vigor (null = default do Core).</param>
    /// <returns>Temperatura do retry.</returns>
    public static double ForRetry(double current, IReadOnlyDictionary<string, RoleProfile>? profiles = null)
    {
        double detour = RoleProfiles.DetourTemperature(profiles);
        return current > detour ? current : detour;
    }
}
