using System.Text.Json.Nodes;
using Rochas.OpenCodeBridge.Core;

namespace Rochas.OpenCodeBridge.Test;

// Unit do help curado por tool: o texto viaja na descricao do schema
// (chat, Responses flat e namespace), preserva a descricao original e e
// idempotente quando o mesmo array passa por mais de uma camada.
internal static class UnitToolHelpTests
{
    public static Task Run()
    {
        var chat = JsonNode.Parse("""
        [
          {"type":"function","function":{"name":"shell","description":"executa","parameters":{"type":"object","properties":{"command":{"type":"string"}}}}},
          {"type":"function","function":{"name":"desconhecida","description":"original"}}
        ]
        """)!.AsArray();

        ToolHelp.Apply(chat);
        string shell = chat[0]!["function"]!["description"]!.GetValue<string>();
        TestContext.Check(shell.StartsWith("executa", StringComparison.Ordinal), "descricao original preservada");
        TestContext.Check(shell.Contains(ToolHelp.Marker, StringComparison.Ordinal), "help marcado no schema chat");
        TestContext.Check(shell.Contains("cd dir && cmd", StringComparison.Ordinal), "help de shell cobre cd");
        // Pacote: a ordem e' o erro medido no E-mission-tools de 2026-10-29 (o
        // modelo passou a origem no lugar do .zip e o zip recusou 4 vezes).
        TestContext.Check(shell.Contains("zip -qr pacote.zip .", StringComparison.Ordinal),
            "help de shell ensina a ordem do pacote (o .zip vem primeiro)");
        TestContext.Check(shell.Contains("Zip file structure invalid", StringComparison.Ordinal),
            "help de shell explica o sintoma do zip em prosa");
        TestContext.Check(chat[1]!["function"]!["description"]!.GetValue<string>() == "original",
            "tool sem help nao e alterada");

        // Git: identidade e' passo do modelo, ensinado na ORDEM (name, email,
        // commit) — o modelo escrevia `user.user.name` e o commit falhava em loop.
        string git = global::Rochas.OpenCodeBridge.Core.ToolHelp.Apply(
            JsonNode.Parse("""
            [{"type":"function","function":{"name":"git","description":"git"}}]
            """)!.AsArray())[0]!["function"]!["description"]!.GetValue<string>();
        TestContext.Check(git.Contains("ANTES do primeiro `commit`", StringComparison.Ordinal),
            "help do git situa a config antes do commit");
        TestContext.Check(git.Contains("config user.name", StringComparison.Ordinal)
            && git.Contains("config user.email", StringComparison.Ordinal),
            "help do git ensina as DUAS chamadas de identidade");
        TestContext.Check(git.IndexOf("config user.name", StringComparison.Ordinal)
            < git.IndexOf("config user.email", StringComparison.Ordinal),
            "help do git ensina name antes de email");
        TestContext.Check(git.Contains("Author identity unknown", StringComparison.Ordinal),
            "help do git explica o sintoma de identidade ausente");
        TestContext.Check(!git.Contains("user.user.name", StringComparison.Ordinal),
            "help do git nao exibe a chave errada (so o certo e' copiavel)");
        TestContext.Check(chat[0]!["function"]!["parameters"]!["properties"]!["command"] is not null,
            "schema nao e alterado");

        string once = shell;
        ToolHelp.Apply(chat);
        TestContext.Check(chat[0]!["function"]!["description"]!.GetValue<string>() == once,
            "segunda aplicacao e idempotente");
        TestContext.Check(once.Split(ToolHelp.Marker).Length - 1 == 1, "marcador aparece uma vez");

        var responses = JsonNode.Parse("""
        [
          {"type":"function","name":"edit","description":"edita"},
          {"type":"namespace","name":"mcp","tools":[{"type":"function","name":"read","description":"le"}]}
        ]
        """)!.AsArray();

        ToolHelp.ApplyTo(responses);
        string edit = responses[0]!["description"]!.GetValue<string>();
        string read = responses[1]!["tools"]![0]!["description"]!.GetValue<string>();
        TestContext.Check(edit.Contains("oldString", StringComparison.Ordinal)
            && edit.Contains(ToolHelp.Marker, StringComparison.Ordinal), "help no shape Responses flat");
        TestContext.Check(read.Contains("offset/limit", StringComparison.Ordinal)
            && read.Contains(ToolHelp.Marker, StringComparison.Ordinal), "help em namespace aninhado");

        var catalog = global::Rochas.OpenCodeBridge.Core.ToolCatalog.Chat();
        string catalogShell = catalog[0]!["function"]!["description"]!.GetValue<string>();
        TestContext.Check(catalogShell.Contains(ToolHelp.Marker, StringComparison.Ordinal),
            "catalogo proprio ja nasce com help");
        TestContext.Check(catalogShell.Contains("um comando", StringComparison.Ordinal),
            "help do catalogo cobre shell");
        return Task.CompletedTask;
    }
}
