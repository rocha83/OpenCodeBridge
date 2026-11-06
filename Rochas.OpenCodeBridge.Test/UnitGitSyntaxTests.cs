namespace Rochas.OpenCodeBridge.Test;

/// <summary>
/// Forma estrita do git: flag inventada (`--path`, `-f`, `--no-bare`) e' recusada
/// com a forma canonica, e a receita valida continua passando. Medido no
/// E-tools-full de 2026-11-06.
/// </summary>
internal static class UnitGitSyntaxTests
{
    static string? V(string command)
    {
        string[] argv = (new[] { "git" }).Concat(Split(command)).ToArray();
        return global::Rochas.OpenCodeBridge.Core.Tools.GitCommandSyntax.Validate(argv, 1);
    }

    static string[] Split(string command)
    {
        var parts = new System.Collections.Generic.List<string>();
        var cur = new System.Text.StringBuilder();
        char quote = '\0';
        foreach (char ch in command.Trim())
        {
            if (quote != '\0') { if (ch == quote) quote = '\0'; else cur.Append(ch); }
            else if (ch == '"' || ch == '\'') quote = ch;
            else if (char.IsWhiteSpace(ch)) { if (cur.Length > 0) { parts.Add(cur.ToString()); cur.Clear(); } }
            else cur.Append(ch);
        }
        if (cur.Length > 0) parts.Add(cur.ToString());
        return parts.ToArray();
    }

    public static Task RejectsInventedFlags()
    {
        TestContext.Check(V("add data.txt --path ./") is not null, "recusa --path no add");
        TestContext.Check(V("commit -m \"data de hoje\" data.txt --path ./data.txt") is not null, "recusa --path no commit");
        TestContext.Check(V("log --oneline -1 data.txt --path ./data.txt") is not null, "recusa --path no log");
        TestContext.Check(V("init --no-bare") is not null, "recusa --no-bare no init");
        TestContext.Check(V("config user.name \"Ana\" --local -f") is not null, "recusa -f no config");
        TestContext.Check(V("commit -m \"x\" --allow-empty") is not null, "recusa --allow-empty (commit tem que ser real)");
        TestContext.Check(V("add . --force-include-all -f") is not null, "recusa --force-include-all");
        return Task.CompletedTask;
    }

    public static Task AcceptsCanonicalForms()
    {
        TestContext.Check(V("init") is null, "init puro");
        TestContext.Check(V("add .") is null, "add .");
        TestContext.Check(V("add -A") is null, "add -A");
        TestContext.Check(V("commit -m \"data de hoje\"") is null, "commit -m");
        TestContext.Check(V("config user.email teste@exemplo.local") is null, "config user.email");
        TestContext.Check(V("log --oneline") is null, "log --oneline");
        TestContext.Check(V("log --oneline -1 data.txt") is null, "log -1 pathspec");
        TestContext.Check(V("status --short") is null, "status --short");
        TestContext.Check(V("init .") is null, "init . (legal)");
        TestContext.Check(V("init --quiet") is null, "init --quiet (legal)");
        TestContext.Check(V("config user.email a@b.c --local") is null, "config --local (legal)");
        TestContext.Check(V("add -f .") is null, "add -f (legal)");
        return Task.CompletedTask;
    }
}
