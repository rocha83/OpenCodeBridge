namespace Rochas.OpenCodeBridge.Core;

/// <summary>
/// Modo de operação da bridge. O modo — não o cliente — decide quais rotinas do
/// núcleo entram:
/// <list type="bullet">
/// <item><c>bridge</c>: fixes + anti-repeat (conteúdo/thinking e tool-args) +
/// main harness + tails. O cliente fala o protocolo; a bridge aplica a base e
/// as caudas dela.</item>
/// <item><c>opencode</c>: fixes + anti-repeat (conteúdo/thinking e tool-args) +
/// tails aditivas; o master (Code Mode / AGENTS.md) do cliente NÃO é
/// substituído e o main harness fica desligado. As tails só entram com
/// <c>--harness-tails 1</c> e <c>--tail-scope all</c>.</item>
/// </list>
/// </summary>
/// <param name="Name">Nome do modo (o mesmo da CLI).</param>
/// <param name="Fixes">Correções de protocolo/template (sempre ligadas).</param>
/// <param name="AntiRepeat">Observa thinking/conteúdo e tool-args e contém repetição.</param>
/// <param name="MainHarness">Substitui o system do cliente pelo harness do núcleo (só bridge).</param>
/// <param name="Tails">Injeta caudas de comportamento/tools no system (bridge e opencode).</param>
public sealed record BridgeMode(string Name, bool Fixes, bool AntiRepeat, bool MainHarness, bool Tails)
{
    /// <summary>Trilha gerida: harness e tails ligados.</summary>
    public static readonly BridgeMode Bridge = new("bridge", true, true, true, true);

    /// <summary>Trilha opencode: master do cliente, sem troca de system; tails permitidas
    /// (aditivas, o switch real e --harness-tails + --tail-scope).</summary>
    public static readonly BridgeMode Opencode = new("opencode", true, true, false, true);

    /// <summary>Lê o modo da CLI/appsettings.</summary>
    /// <param name="value">"bridge" ou "opencode" (case-insensitive).</param>
    /// <param name="mode">Modo resolvido (Bridge quando inválido).</param>
    /// <returns>False quando o valor não é um modo conhecido.</returns>
    public static bool TryParse(string? value, out BridgeMode mode)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "bridge": mode = Bridge; return true;
            case "opencode": mode = Opencode; return true;
            default: mode = Bridge; return false;
        }
    }
}
