using System.Text.Json.Nodes;

namespace Rochas.OpenCodeBridge.Core.Tools;

// Handler da tool "fetch": GET HTTP somente-leitura (tipo curl).
// Sem escrita, sem execução: devolve o corpo como texto.
public sealed class FetchToolHandler : IToolHandler
{
    public string ToolName => "fetch";

    private const int DefaultMaxChars = 12000;
    private const int MaxChars = 1_000_000;

    private static readonly System.Net.Http.HttpClient Http = new(new System.Net.Http.HttpClientHandler
    {
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 5,
    });

    static FetchToolHandler()
    {
        // Navegacao web se passa por Firefox 133 em Windows 11: evita bloqueio
        // de bot/UA padrao e keep-alive consistente. Fonte unica (Core).
        Http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:133.0) Gecko/20100101 Firefox/133.0");
        Http.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        Http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("pt-BR,pt;q=0.9,en-US;q=0.8,en;q=0.7");
    }

    public ToolResult Handle(string argumentsJson, ToolContext context)
    {
        JsonObject? obj;
        try { obj = JsonNode.Parse(argumentsJson)?.AsObject(); }
        catch (Exception ex) { return ToolResult.Fail($"argumentos inválidos: {ex.Message}"); }
        if (obj is null) return ToolResult.Fail("argumentos inválidos: objeto JSON esperado");

        string url = obj["url"]?.GetValue<string>() ?? "";
        if (string.IsNullOrWhiteSpace(url)) return ToolResult.Fail("url é obrigatória");
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != "http" && uri.Scheme != "https"))
            return ToolResult.Fail("url deve ser http(s) absoluta");
        int maxChars = Math.Min(Math.Max(obj["maxChars"]?.GetValue<int>() ?? DefaultMaxChars, 1), MaxChars);

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(30, context.TimeoutSeconds)));
            using var res = Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cts.Token).GetAwaiter().GetResult();
            if (!res.IsSuccessStatusCode)
                return ToolResult.Fail($"HTTP {(int)res.StatusCode} em '{uri}'");
            string body = res.Content.ReadAsStringAsync(cts.Token).GetAwaiter().GetResult() ?? "";
            if (body.Length > maxChars) body = body[..maxChars];
            return ToolResult.Ok(body);
        }
        catch (OperationCanceledException)
        {
            return ToolResult.Fail("fetch estourou o tempo");
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"falha ao buscar '{url}': {ex.Message.Split('\n')[0]}");
        }
    }
}
