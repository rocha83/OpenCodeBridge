namespace Rochas.OpenCodeBridge.Core;

/// <summary>
/// Regra de temperatura do tunel, num if: <b>cliente manda</b> &gt; perfil do role
/// &gt; global. <c>profileWins</c> (--profile-temp-wins) inverte o primeiro par.
/// </summary>
public static class Sampling
{
    /// <summary>Resolve a temperatura efetiva e a procedencia.</summary>
    /// <param name="requestTemp">Temperatura enviada pelo cliente (null = nao enviou).</param>
    /// <param name="hasProfile">Model id carrega sufixo de role.</param>
    /// <param name="profileTemp">Temperatura do perfil (NaN = perfil sem temperatura).</param>
    /// <param name="globalTemp">Temperatura base da instancia.</param>
    /// <param name="profileWins">Perfil sobrepoe o cliente (--profile-temp-wins).</param>
    /// <returns>Valor e procedencia ("request" | "profile" | "profile(override)" | "global").</returns>
    public static (double Value, string Source) Temperature(
        double? requestTemp, bool hasProfile, double profileTemp, double globalTemp, bool profileWins)
    {
        if (requestTemp.HasValue && !(profileWins && hasProfile && !double.IsNaN(profileTemp)))
            return (requestTemp.Value, "request");
        if (hasProfile && !double.IsNaN(profileTemp))
            return (profileTemp, profileWins && requestTemp.HasValue ? "profile(override)" : "profile");
        return (globalTemp, "global");
    }
}
