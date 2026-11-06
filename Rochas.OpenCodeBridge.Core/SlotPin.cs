using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Rochas.OpenCodeBridge.Core;

/// <summary>
/// Pino de slot do llama-server (<c>-np N</c>): um level por slot. Sem o pino o
/// llama escolhe o slot livre e cada request pode cair num slot diferente; com
/// <c>id_slot</c> fixo o cache de prompt e o KV do run ficam no slot do level em
/// curso (medido: 4 slots de 114688 no <c>:4100</c> com o E4B Q6_K full-VRAM).
/// </summary>
public static class SlotPin
{
    /// <summary>Sem pino: o llama escolhe o slot livre (comportamento historico).</summary>
    public const int None = -1;

    /// <summary>Mesma serializacao do Program (unicode e acentos sem escape).</summary>
    static readonly JsonSerializerOptions SerOpts = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>Aplica <c>id_slot</c> no body ja montado (no-op sem pino).</summary>
    /// <param name="body">Body do /v1/chat/completions.</param>
    /// <param name="slot">Indice do slot (0..N-1) ou <see cref="None"/>.</param>
    /// <returns>O mesmo objeto, para encadear.</returns>
    public static JsonObject Apply(JsonObject body, int slot)
    {
        if (slot >= 0) body["id_slot"] = slot;
        return body;
    }

    /// <summary>Aplica <c>id_slot</c> no body cru do cliente (string).</summary>
    /// <param name="body">JSON do request como veio do cliente.</param>
    /// <param name="slot">Indice do slot (0..N-1) ou <see cref="None"/>.</param>
    /// <returns>O mesmo texto quando sem pino, nao-JSON ou ilegivel.</returns>
    public static string Apply(string body, int slot)
    {
        if (slot < 0) return body;
        try
        {
            if (JsonNode.Parse(body) is not JsonObject o) return body;
            Apply(o, slot);
            return o.ToJsonString(SerOpts);
        }
        catch
        {
            // Body que nao parseia segue intacto: quem decide o erro e' o upstream.
            return body;
        }
    }
}
