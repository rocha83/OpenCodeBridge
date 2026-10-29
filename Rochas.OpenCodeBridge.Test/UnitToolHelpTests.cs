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
        // Receita comentada (exemplo generalizado de SUCESSO): o modelo copia a
        // ordem certa — init, arquivos com a pasta, identidade, add, commit, log —
        // em vez de encadear tudo numa chamada so'.
        TestContext.Check(git.Contains("EXEMPLO DE SUCESSO", StringComparison.Ordinal)
            && git.Contains("\"command\":\"init\"", StringComparison.Ordinal)
            && git.Contains("\"command\":\"add .\"", StringComparison.Ordinal)
            && git.Contains("commit -m", StringComparison.Ordinal)
            && git.Contains("log --oneline", StringComparison.Ordinal),
            "help do git traz o exemplo de sucesso do fluxo completo");
        TestContext.Check(git.Contains("\"path\":\"<pasta>\"", StringComparison.Ordinal),
            "exemplo do git marca os nomes como ficticios e usa o MESMO path");
        TestContext.Check(!git.Contains("\"path\":\"projeto\"", StringComparison.Ordinal),
            "exemplo do git nao fixa nome de pasta que o modelo copiaria ao pe' da letra");
        TestContext.Check(!git.Contains("não encadeie", StringComparison.Ordinal),
            "help do git ensina a forma certa (sem instrucao negativa de encadeamento)");
        TestContext.Check(shell.Contains("cwd de cada chamada", StringComparison.Ordinal),
            "help de shell diz o cwd real de cada chamada");

        // Path das tools de arquivo: RAIZ do workspace, nao o cwd do shell — o
        // modelo gravava `data.txt` na raiz achando que estava na subpasta.
        var paths = global::Rochas.OpenCodeBridge.Core.ToolHelp.Apply(JsonNode.Parse("""
        [
          {"type":"function","function":{"name":"write","description":"w"}},
          {"type":"function","function":{"name":"read","description":"r"}},
          {"type":"function","function":{"name":"edit","description":"e"}}
        ]
        """)!.AsArray());
        foreach (int i in new[] { 0, 1, 2 })
        {
            string d = paths[i]!["function"]!["description"]!.GetValue<string>();
            TestContext.Check(d.Contains("RAIZ do workspace", StringComparison.Ordinal)
                && d.Contains("NAO vale para tools", StringComparison.Ordinal),
                "help de path ensina raiz do workspace (" + paths[i]!["function"]!["name"] + ")");
        }
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
