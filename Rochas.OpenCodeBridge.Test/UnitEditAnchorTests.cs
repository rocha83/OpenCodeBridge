using Rochas.OpenCodeBridge.Core.Tools;

namespace Rochas.OpenCodeBridge.Test;

/// <summary>
/// `edit` com `oldString` nulo/vazio precisa devolver a ancora REAL do disco.
/// Medido no E-tools-full de 2026-11-06: o modelo mandou `oldString:null` 2x,
/// recebeu "oldString e' obrigatorio" e repetiu igual — sem a linha real ele
/// nao consegue montar a chamada.
/// </summary>
internal static class UnitEditAnchorTests
{
    public static Task RefusalShowsRealAnchor()
    {
        string ws = Directory.CreateTempSubdirectory("edit-anchor-").FullName;
        string file = Path.Combine(ws, "data.txt");
        File.WriteAllText(file, "2026-11-06\n\nARQUIVO EDITADO - Clima: 25.4°C\n");
        var ctx = new ToolContext { WorkspaceRoot = ws, Role = "build", TimeoutSeconds = 60 };
        var edit = new EditToolHandler();

        var refused = edit.Handle("""{"path":"data.txt","oldString":null,"newString":"x"}""", ctx);
        TestContext.Check(!refused.Success, "edit com oldString nulo e recusado");
        TestContext.Check(refused.Error.Contains("2026-11-06", StringComparison.Ordinal),
            "recusa mostra a ancora REAL do disco (err: " + refused.Error + ")");
        TestContext.Check(refused.Error.Contains("write", StringComparison.Ordinal),
            "recusa aponta o `write` quando nao ha ancora");

        var applied = edit.Handle("""{"path":"data.txt","oldString":"2026-11-06","newString":"2026-11-06\nARQUIVO EDITADO - Clima: 25.4°C"}""", ctx);
        TestContext.Check(applied.Success, "append por edit com ancora real aplica (err: " + applied.Error + ")");
        string body = File.ReadAllText(file);
        TestContext.Check(body.Contains("2026-11-06", StringComparison.Ordinal)
            && body.Contains("ARQUIVO EDITADO", StringComparison.Ordinal),
            "a data commitada continua e a linha nova entra (conteudo: " + body.Replace("\n", " | ") + ")");
        return Task.CompletedTask;
    }
}
