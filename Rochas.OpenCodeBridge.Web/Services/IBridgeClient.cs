using System.IO;
using System.Text.Json.Nodes;
using System.Threading;

namespace Rochas.OpenCodeBridge.Web.Services;

// Interface para cliente de bridge (permite swap real/mock via DI).
public interface IBridgeClient
{
    Task<(bool ok, string error)> StreamAsync(string bridgeUrl, string model, double temperature,
        string systemPrompt, JsonArray messages, Stream output, CancellationToken ct,
        bool includeTools = true, JsonArray? tools = null, int maxTokens = 2048);
}