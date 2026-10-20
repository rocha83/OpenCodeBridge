using System.Text.Json.Nodes;

namespace Rochas.AI.Manager.Application.Tools;

// Handler da tool "read": lê arquivo de texto, imagem ou PDF do workspace.
// Imagens e PDFs são retornados no formato multimodal OpenAI (base64 data URL).
public sealed class ReadToolHandler : IToolHandler
{
    public string ToolName => "read";

    private const int DefaultLimit = 2000;
    private const int MaxLines = 20000;
    private const int MaxChars = 1_000_000;
    private const long MaxImageBytes = 20_000_000; // 20MB teto p/ imagens

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".tiff", ".tif"
    };

    private static readonly Dictionary<string, string> MimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".png"] = "image/png",
        [".gif"] = "image/gif",
        [".bmp"] = "image/bmp",
        [".webp"] = "image/webp",
        [".tiff"] = "image/tiff",
        [".tif"] = "image/tiff",
        [".pdf"] = "application/pdf"
    };

    public ToolResult Handle(string argumentsJson, ToolContext context)
    {
        JsonObject? obj;
        try { obj = JsonNode.Parse(argumentsJson)?.AsObject(); }
        catch (Exception ex) { return ToolResult.Fail($"argumentos inválidos: {ex.Message}"); }
        if (obj is null) return ToolResult.Fail("argumentos inválidos: objeto JSON esperado");

        string path = obj["path"]?.GetValue<string>() ?? "";
        if (string.IsNullOrWhiteSpace(path)) return ToolResult.Fail("path é obrigatório");
        int offset = ClampInt(obj["offset"]?.GetValue<int>() ?? 0, 0, int.MaxValue);
        int limit = ClampInt(obj["limit"]?.GetValue<int>() ?? DefaultLimit, 1, MaxLines);

        if (!WorkspaceGuard.TryResolve(context.WorkspaceRoot, path, out string full, out string why))
            return ToolResult.Fail(why);
        if (!File.Exists(full)) return ToolResult.Fail($"arquivo não encontrado: '{path}'");

        try
        {
            var info = new FileInfo(full);
            if (info.Length > MaxChars && !IsImage(full)) return ToolResult.Fail($"arquivo grande demais ({info.Length} bytes, teto {MaxChars})");

            string ext = Path.GetExtension(full).ToLowerInvariant();
            
            if (IsImage(full))
            {
                return ReadImage(full);
            }
            else if (ext == ".pdf")
            {
                return ReadPdf(full);
            }
            else
            {
                // Texto: comportamento original
                if (info.Length > MaxChars) return ToolResult.Fail($"arquivo grande demais ({info.Length} bytes, teto {MaxChars})");
                string[] lines = File.ReadAllLines(full);
                string[] slice = lines.Skip(offset).Take(limit).ToArray();
                return ToolResult.Ok(string.Join('\n', slice));
            }
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"falha ao ler '{path}': {ex.Message}");
        }
    }

    private static bool IsImage(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ImageExtensions.Contains(ext);
    }

    private ToolResult ReadImage(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Length > MaxImageBytes)
                return ToolResult.Fail($"imagem grande demais ({info.Length} bytes, teto {MaxImageBytes})");

            byte[] bytes = File.ReadAllBytes(path);
            string base64 = Convert.ToBase64String(bytes);
            string mime = MimeTypes.TryGetValue(Path.GetExtension(path).ToLowerInvariant(), out string? mimeType) ? mimeType ?? "application/octet-stream" : "application/octet-stream";
            string dataUrl = $"data:{mime};base64,{base64}";

            // Formato multimodal OpenAI
            var content = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "image_url",
                    ["image_url"] = new JsonObject
                    {
                        ["url"] = dataUrl,
                        ["detail"] = "high"
                    }
                }
            };

            // Retorna JSON string indicando multimodal
            return ToolResult.Ok($"{{\"multimodal\":true,\"content\":{content.ToJsonString()}}}");
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"falha ao ler imagem '{path}': {ex.Message}");
        }
    }

    private ToolResult ReadPdf(string path)
    {
        // PDF: tentativa básica de extrair texto; para OCR real precisaria de biblioteca externa
        try
        {
            // Tentativa simples: ler como binário e tentar extrair texto simples
            // Para OCR real seria necessário PDFium, PdfPig, etc.
            return ToolResult.Fail("PDF: suporte a OCR não implementado. Converta para imagem ou use ferramenta externa.");
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"falha ao ler PDF '{path}': {ex.Message}");
        }
    }

    private static int ClampInt(int value, int min, int max) => Math.Min(Math.Max(value, min), max);
}
