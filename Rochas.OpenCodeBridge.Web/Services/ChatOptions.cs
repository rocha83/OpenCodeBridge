namespace Rochas.OpenCodeBridge.Web.Services;

// Flags do chat da Web. Não há orquestração separada: o role Orch é um agente
// normal e o loop agêntico vive no ChatController/bridge.
public sealed class ChatOptions
{
    public bool DirectApprove { get; set; } = true;
    public int MaxTurns { get; set; } = 0;
    public bool UseResponsesBypass { get; set; } = true;
}
