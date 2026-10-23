namespace Rochas.OpenCodeBridge.Core;

/// <summary>
/// Política de tools do caminho Web/SDK. A allowlist do agente é aplicada pela
/// própria Web (ToolDefinitions.Allowed); aqui o padrão é liberar. O host console
/// mantém as regras de risco/role no endpoint /api/permission/check.
/// </summary>
public static class PermissionPolicy
{
    /// <summary>Decide allow/ask/deny para uma chamada.</summary>
    /// <param name="model">Modelo do agente (pode carregar sufixo de role).</param>
    /// <param name="tool">Nome da tool.</param>
    /// <param name="args">Argumentos serializados.</param>
    /// <returns>Ação e motivo.</returns>
    public static (string Action, string Reason) Check(string? model, string tool, string args)
        => ("allow", "");
}
