using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using Microsoft.Extensions.Options;

namespace Rochas.OpenCodeBridge.Web.Services;

// Mock bridge client para testes/demo sem bridge real.
// Emite thinking + content simulados + usage final.
public sealed class MockBridgeClient : IBridgeClient
{
    private readonly MockBridgeOptions _options;
    private readonly Random _rng = Random.Shared;

    public MockBridgeClient(IOptions<MockBridgeOptions> options)
    {
        _options = options.Value;
    }

    public async Task<(bool ok, string error)> StreamAsync(string bridgeUrl, string model, double temperature,
        string systemPrompt, JsonArray messages, Stream output, CancellationToken ct,
        bool includeTools = true, JsonArray? tools = null)
    {
        try
        {
            // Simula latência inicial
            await Task.Delay(_rng.Next(_options.MinInitialDelayMs, _options.MaxInitialDelayMs), ct);

            // Extrai última mensagem do usuário para resposta contextual
            string lastUserMsg = "";
            for (int i = messages.Count - 1; i >= 0; i--)
            {
                var m = messages[i];
                if (m?["role"]?.GetValue<string>() == "user")
                {
                    lastUserMsg = m["content"]?.GetValue<string>() ?? "";
                    break;
                }
            }

            bool thinkingEnabled = _options.EnableThinking;
            int thinkingChunks = _rng.Next(_options.MinThinkingChunks, _options.MaxThinkingChunks + 1);
            int contentChunks = _rng.Next(_options.MinContentChunks, _options.MaxContentChunks + 1);

            // Gera resposta baseada na mensagem do usuário
            string thinkingText = GenerateThinking(lastUserMsg);
            string contentText = GenerateContent(lastUserMsg);

            var writer = new StreamWriter(output, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };

            // Helper para escrever chunk SSE
            async Task WriteChunk(object chunk)
            {
                string json = JsonSerializer.Serialize(chunk);
                await writer.WriteAsync($"data: {json}\n\n");
            }

            // Emite thinking se habilitado
            if (thinkingEnabled)
            {
                for (int i = 0; i < thinkingChunks && !ct.IsCancellationRequested; i++)
                {
                    string piece = thinkingText.Length > 0
                        ? thinkingText.Substring(0, Math.Min(thinkingText.Length, _rng.Next(3, 12)))
                        : " ";
                    thinkingText = thinkingText.Length > piece.Length ? thinkingText[piece.Length..] : "";

                    var chunk = new JsonObject
                    {
                        ["id"] = $"chatcmpl-mock-{Guid.NewGuid():N}",
                        ["object"] = "chat.completion.chunk",
                        ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                        ["model"] = "mock-model",
                        ["choices"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["index"] = 0,
                                ["delta"] = new JsonObject { ["reasoning_content"] = piece },
                                ["logprobs"] = null,
                                ["finish_reason"] = null
                            }
                        }
                    };
                    await WriteChunk(chunk);
                    await Task.Delay(_rng.Next(_options.MinChunkDelayMs, _options.MaxChunkDelayMs), ct);
                }
            }

            // Emite content
            for (int i = 0; i < contentChunks && !ct.IsCancellationRequested; i++)
            {
                string piece = contentText.Length > 0
                    ? contentText.Substring(0, Math.Min(contentText.Length, _rng.Next(5, 20)))
                    : " ";
                contentText = contentText.Length > piece.Length ? contentText[piece.Length..] : "";

                var chunk = new JsonObject
                {
                    ["id"] = $"chatcmpl-mock-{Guid.NewGuid():N}",
                    ["object"] = "chat.completion.chunk",
                    ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    ["model"] = "mock-model",
                    ["choices"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["index"] = 0,
                            ["delta"] = new JsonObject { ["content"] = piece },
                            ["logprobs"] = null,
                            ["finish_reason"] = null
                        }
                    }
                };
                await WriteChunk(chunk);
                await Task.Delay(_rng.Next(_options.MinChunkDelayMs, _options.MaxChunkDelayMs), ct);
            }

            // Final chunk com usage
            int promptTokens = EstimateTokens(string.Join(" ", messages.Select(m => m["content"]?.GetValue<string>() ?? ""))) + 10;
            int completionTokens = _rng.Next(50, 300);

            var finalChunk = new JsonObject
            {
                ["id"] = $"chatcmpl-mock-{Guid.NewGuid():N}",
                ["object"] = "chat.completion.chunk",
                ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ["model"] = "mock-model",
                ["choices"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["index"] = 0,
                        ["delta"] = new JsonObject { },
                        ["logprobs"] = null,
                        ["finish_reason"] = "stop"
                    }
                },
                ["usage"] = new JsonObject
                {
                    ["prompt_tokens"] = promptTokens,
                    ["completion_tokens"] = completionTokens,
                    ["total_tokens"] = promptTokens + completionTokens
                }
            };
            await WriteChunk(finalChunk);

            // [DONE]
            await writer.WriteAsync("data: [DONE]\n\n");

            // Simula erro aleatório
            if (_rng.NextDouble() < _options.ErrorRate)
            {
                return (false, "Mock bridge: erro simulado");
            }

            return (true, "");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private string GenerateThinking(string userMsg)
    {
        var thoughts = new[]
        {
            "Analisando a pergunta do usuário... ",
            "Identificando a intenção principal... ",
            "Verificando conhecimento relevante... ",
            "Estruturando a resposta... ",
            "Verificando consistência... ",
            "Preparando resposta final... "
        };
        return string.Concat(thoughts);
    }

    private string GenerateContent(string userMsg)
    {
        if (string.IsNullOrWhiteSpace(userMsg))
            return "Olá! Como posso ajudar você hoje? ";

        var responses = new[]
        {
            $"Você disse: \"{userMsg}\". Isso é uma resposta mock do modelo. ",
            $"Entendi sua pergunta sobre \"{userMsg}\". Aqui está uma resposta simulada. ",
            $"Processando: {userMsg}. Resposta gerada pelo mock bridge. ",
            $"Mock response para: {userMsg}. ",
            $"```csharp\n// Mock response para: {userMsg}\nConsole.WriteLine(\"Hello from mock!\");\n``` "
        };
        return responses[Random.Shared.Next(responses.Length)];
    }

    private int EstimateTokens(string text)
    {
        if (string.IsNullOrEmpty(text)) return 1;
        return (int)Math.Ceiling(text.Length / 4.0);
    }
}

// Opções de configuração do mock
public sealed class MockBridgeOptions
{
    public bool UseMockBridge { get; set; } = false;
    public bool EnableThinking { get; set; } = true;
    public int MinThinkingChunks { get; set; } = 3;
    public int MaxThinkingChunks { get; set; } = 8;
    public int MinContentChunks { get; set; } = 8;
    public int MaxContentChunks { get; set; } = 18;
    public int MinChunkDelayMs { get; set; } = 50;
    public int MaxChunkDelayMs { get; set; } = 200;
    public int MinInitialDelayMs { get; set; } = 200;
    public int MaxInitialDelayMs { get; set; } = 500;
    public double ErrorRate { get; set; } = 0.05; // 5% de erro simulado
}