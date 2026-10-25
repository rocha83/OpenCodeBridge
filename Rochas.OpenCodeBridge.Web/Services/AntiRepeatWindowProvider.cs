using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Rochas.OpenCodeBridge.Core;

namespace Rochas.OpenCodeBridge.Web.Services;

/// <summary>
/// Janela efetiva do anti-repeat no caminho Web. A BRIDGE manda: o valor vem de
/// <c>--anti-repeat-window</c> (appsettings) e aparece em <c>/api/status</c>
/// (<c>anti_repeat_window</c>). O Core só publica o default
/// (<see cref="ContentAntiRepeat.DefaultWindow"/>) para quando o status não
/// responde — o detector nunca fica sem janela.
/// </summary>
public static class AntiRepeatWindowProvider
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private static readonly ConcurrentDictionary<string, (int Window, DateTime At)> Cache = new(StringComparer.Ordinal);
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Resolve a janela para o deployment da bridge informada, com cache curto.
    /// </summary>
    /// <param name="bridgeUrl">URL da bridge do agente (ex.: http://127.0.0.1:4124).</param>
    /// <param name="apiKey">Chave da bridge (Bearer); vazia = sem header.</param>
    /// <param name="ct">Cancelamento.</param>
    /// <returns>Janela em caracteres, dentro de <see cref="ContentAntiRepeat.WindowMin"/>..<see cref="ContentAntiRepeat.WindowMax"/>.</returns>
    public static async Task<int> ResolveAsync(string? bridgeUrl, string apiKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(bridgeUrl)) return ContentAntiRepeat.DefaultWindow;
        if (Cache.TryGetValue(bridgeUrl, out var hit) && DateTime.UtcNow - hit.At < Ttl)
            return hit.Window;

        int window = ContentAntiRepeat.DefaultWindow;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, bridgeUrl.TrimEnd('/') + "/api/status");
            if (!string.IsNullOrEmpty(apiKey))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            using var res = await Http.SendAsync(req, ct);
            if (res.IsSuccessStatusCode)
            {
                var node = JsonNode.Parse(await res.Content.ReadAsStringAsync(ct));
                int w = node?["anti_repeat_window"]?.GetValue<int>() ?? 0;
                if (w >= ContentAntiRepeat.WindowMin && w <= ContentAntiRepeat.WindowMax) window = w;
            }
        }
        catch
        {
            // Bridge fora/status sem o campo: default do Core (mesma janela padrao).
        }
        Cache[bridgeUrl] = (window, DateTime.UtcNow);
        return window;
    }
}
