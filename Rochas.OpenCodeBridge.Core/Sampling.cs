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
    /// referência de desvio, nunca abaixo da atual.
    /// </summary>
    /// <param name="current">Temperatura efetiva do turno que falhou.</param>
    /// <param name="profiles">Tabela de roles em vigor (null = default do Core).</param>
    /// <returns>Temperatura do retry.</returns>
    public static double ForRetry(double current, IReadOnlyDictionary<string, RoleProfile>? profiles = null)
        => ForRetry(current, RoleProfiles.DetourTemperature(profiles));

    /// <summary>Mesma regra com a referência de desvio já resolvida pelo chamador.</summary>
    /// <param name="current">Temperatura efetiva do turno que falhou.</param>
    /// <param name="detourReference">Temperatura de desvio (ver <see cref="DetourReference"/>);
    /// NaN = sem referência, o turno mantém a temperatura que já tinha.</param>
    /// <returns>Temperatura do retry.</returns>
    public static double ForRetry(double current, double detourReference)
        => double.IsNaN(detourReference) || current > detourReference ? current : detourReference;

    /// <summary>
    /// Referência do desvio do retry, com a MESMA precedência da temperatura de
    /// um turno: o perfil do role manda quando o model id carrega um role; sem
    /// role vale o valor global do host; sem ele, o perfil de desvio da tabela.
    /// </summary>
    /// <remarks>
    /// O valor global do host é o análogo da temperatura global (a que vale
    /// quando o corpo não traz a sua): preenche a falta de role e nunca
    /// sobrepõe um role resolvido.
    /// </remarks>
    /// <param name="hasRole">O model id carrega um role conhecido?</param>
    /// <param name="profiles">Tabela de roles em vigor (null = default do Core).</param>
    /// <param name="auxTemp">Valor global do host para o desvio (NaN = não configurado).</param>
    /// <returns>Temperatura de referência do desvio.</returns>
    public static double DetourReference(bool hasRole,
        IReadOnlyDictionary<string, RoleProfile>? profiles = null, double auxTemp = double.NaN)
        => !hasRole && !double.IsNaN(auxTemp) ? auxTemp : RoleProfiles.DetourTemperature(profiles);
}
