namespace Rochas.OpenCodeBridge.Web.Services;

// Contrato de um handler de tool (uma implementação por tool anunciada).
public interface IToolHandler
{
    // Nome da tool no protocolo (ex.: "shell", "read").
    string ToolName { get; }

    // Executa com os argumentos JSON crus do modelo.
    ToolResult Handle(string argumentsJson, ToolContext context);
}
