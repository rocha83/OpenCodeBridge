// Unit tests do ReadToolHandler: texto, imagem multimodal, PDF, bordas.
// Execucao: dotnet run -c Release --project Rochas.OpenCodeBridge.Web.Test -- --units-only
using System.IO;
using System.Text.Json.Nodes;
using Rochas.OpenCodeBridge.Web.Services;

namespace Rochas.OpenCodeBridge.Web.Test.Unit;

internal static class ReadToolHandlerTests
{
    private static int Failures;
    private static int Total;

    internal static int Run()
    {
        TextRoundtrip();
        MissingFile();
        TraversalDenied();
        InvalidArgs();
        ValidImage();
        UnknownExtensionAsText();
        PdfNotImplemented();
        return Failures;
    }

    private static void Check(bool ok, string name)
    {
        Total++;
        System.Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
        if (!ok) Failures++;
    }

    private static string NewRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), $"read-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static void TextRoundtrip()
    {
        string root = NewRoot();
        try
        {
            File.WriteAllText(Path.Combine(root, "a.txt"), "l1\nl2\nl3");
            var h = new ReadToolHandler();
            var ctx = new ToolContext { WorkspaceRoot = root };
            var r = h.Handle("{\"path\":\"a.txt\"}", ctx);
            Check(r.Success && r.Output == "l1\nl2\nl3", "U-read-text");
            var slice = h.Handle("{\"path\":\"a.txt\",\"offset\":1,\"limit\":1}", ctx);
            Check(slice.Success && slice.Output == "l2", "U-read-slice");
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    private static void MissingFile()
    {
        string root = NewRoot();
        try
        {
            var h = new ReadToolHandler();
            var r = h.Handle("{\"path\":\"nope.txt\"}", new ToolContext { WorkspaceRoot = root });
            Check(!r.Success && r.Error.Contains("não encontrado"), "U-read-missing");
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    private static void TraversalDenied()
    {
        string root = NewRoot();
        try
        {
            var h = new ReadToolHandler();
            var r = h.Handle("{\"path\":\"../fora.txt\"}", new ToolContext { WorkspaceRoot = root });
            Check(!r.Success, "U-read-traversal");
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    private static void InvalidArgs()
    {
        string root = NewRoot();
        try
        {
            var h = new ReadToolHandler();
            var ctx = new ToolContext { WorkspaceRoot = root };
            Check(!h.Handle("nao-json", ctx).Success, "U-read-badjson");
            Check(!h.Handle("{\"path\":\"\"}", ctx).Success, "U-read-nopath");
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    private static void ValidImage()
    {
        string root = NewRoot();
        try
        {
            // PNG 1x1 vermelho (bytes minimos validos).
            byte[] png = Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
            File.WriteAllBytes(Path.Combine(root, "img.png"), png);
            var h = new ReadToolHandler();
            var r = h.Handle("{\"path\":\"img.png\"}", new ToolContext { WorkspaceRoot = root });
            bool ok = r.Success && r.Output.Contains("\"multimodal\":true")
                && r.Output.Contains("image_url") && r.Output.Contains("data:image/png;base64,");
            Check(ok, "U-read-image");
            if (ok)
            {
                var node = JsonNode.Parse(r.Output)?.AsObject();
                string url = node?["content"]?[0]?["image_url"]?["url"]?.GetValue<string>() ?? "";
                Check(url.StartsWith("data:image/png;base64,"), "U-read-image-url");
            }
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    private static void UnknownExtensionAsText()
    {
        string root = NewRoot();
        try
        {
            File.WriteAllText(Path.Combine(root, "d.xyz"), "ola");
            var h = new ReadToolHandler();
            var r = h.Handle("{\"path\":\"d.xyz\"}", new ToolContext { WorkspaceRoot = root });
            Check(r.Success && r.Output == "ola", "U-read-unknownext");
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    private static void PdfNotImplemented()
    {
        string root = NewRoot();
        try
        {
            File.WriteAllBytes(Path.Combine(root, "d.pdf"), new byte[] { 0x25, 0x50, 0x44, 0x46 });
            var h = new ReadToolHandler();
            var r = h.Handle("{\"path\":\"d.pdf\"}", new ToolContext { WorkspaceRoot = root });
            Check(!r.Success && r.Error.Contains("PDF"), "U-read-pdf");
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
}
