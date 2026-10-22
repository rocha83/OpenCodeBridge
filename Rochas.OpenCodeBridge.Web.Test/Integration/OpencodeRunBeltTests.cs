// Belts headless no agente real (opencode run, modo implicito): sem browser, sem
// web UI e sem a bridge "pura" — cada enunciado roda no workspace do belt e o
// resultado e conferido por artefato + log.
// Uso: dotnet ... --opencode-run [--belts tools-full,tools-dev-pipe]
//      [--agent build] [--model openai/gemma4-12b-build] [--workdir /tmp/opencode/belts]
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Rochas.OpenCodeBridge.Web.Test.Integration;

internal static class OpencodeRunBeltTests
{
    /// <summary>Contexto das faixas: como cada uma e aceita (spec/texto/tools/pipe).</summary>
    static readonly Dictionary<string, string> BeltKinds = new(StringComparer.Ordinal)
    {
        ["azul"] = "spec",
        ["verde"] = "spec",
        ["roxa"] = "spec",
        ["marrom"] = "spec",
        ["preta"] = "spec",
        ["preta-alone"] = "texto",
        ["tools-full"] = "tools",
        ["tools-dev-pipe"] = "pipe",
    };

    /// <summary>Topicos minimos da resposta das faixas de texto (comparados sem acento).</summary>
    static readonly Dictionary<string, string[]> SpecTopics = new(StringComparer.Ordinal)
    {
        ["azul"] = new[] { "repositorio|repository", "orm", "consulta|query", "fase|phase" },
        ["verde"] = new[] { "cpf", "cnpj", "ddl", "indice", "constraint" },
        ["roxa"] = new[] { "dapper", "regex", "cep", "telefone" },
        ["marrom"] = new[] { "controller", "dto", "react", "dockerfile", "compose" },
        ["preta"] = new[] { "barramento", "evento", "regressao|regression", "perceptron|mlp|multicamada", "anomalia" },
        ["preta-alone"] = new[] { "barramento", "dapper", "regressao|regression", "perceptron|mlp|multicamada", "anomalia" },
    };

    /// <summary>Icones com que o `opencode run` imprime cada tool nativa.</summary>
    static readonly (string Icon, string Name)[] NativeTools =
    {
        ("\u2190 Edit", "edit"),
        ("\u2190 Write", "write"),
        ("\u2192 Read", "read"),
        ("\u2731 Grep", "grep"),
        ("\u2731 Glob", "glob"),
        ("% WebFetch", "fetch"),
        ("\u25C8", "search"),
    };

    /// <summary>Log da bridge GPU de onde sai o tps por request (--diagnostic 1).</summary>
    const string BridgeLog = "/tmp/bridge-gpu.log";

    /// <summary>Corre executa os enunciados em `opencode run` e confere cada aceite.</summary>
    /// <param name="only">Chaves das faixas (vazio = todas).</param>
    /// <param name="agent">Agente do opencode.</param>
    /// <param name="model">Modelo do opencode.</param>
    /// <param name="baseDir">Raiz dos workspaces.</param>
    /// <returns>Quantidade de faixas reprovadas.</returns>
    public static async Task<int> RunAsync(string[] only, string agent, string model, string baseDir)
    {
        int failures = 0;
        int skipped = 0;
        var belts = Ui.BeltUiTests.Select(only);
        if (belts.Length == 0)
        {
            Console.WriteLine("[run] FAIL nenhuma faixa selecionada");
            return 1;
        }
        Directory.CreateDirectory(baseDir);

        foreach (var belt in belts)
        {
            string dir = Path.Combine(baseDir, $"{belt.Key}-{DateTime.Now:HHmmss}");
            Directory.CreateDirectory(dir);
            long logOffset = LogOffset();
            var sw = Stopwatch.StartNew();
            Console.WriteLine($"[run] faixa {belt.Key} em {dir}");

            var psi = new ProcessStartInfo("/usr/local/bin/opencode")
            {
                WorkingDirectory = dir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            // `PWD` do processo pai engana o opencode (ele resolve o workspace por
            // essa env, nao pelo cwd real) - sem isto o agente escreve no repo do teste.
            psi.Environment["PWD"] = dir;
            foreach (var a in new[] { "run", "--standalone", "--thinking", "--auto", "--agent", agent, "--model", model, belt.Prompt })
                psi.ArgumentList.Add(a);

            var sb = new StringBuilder();
            using var p = Process.Start(psi) ?? throw new Exception("sem processo opencode");
            p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            bool exited = await Task.Run(() => p.WaitForExit(belt.TimeoutMin * 60 * 1000));
            if (!exited) { try { p.Kill(true); } catch { } }
            sw.Stop();
            string output = sb.ToString();
            string clean = StripAnsi(output);
            string log = Path.Combine(dir, "_opencode.log");
            try { await File.WriteAllTextAsync(log, output); } catch { }

            bool unknownTool = clean.Contains("Unknown tool", StringComparison.OrdinalIgnoreCase);
            var files = Directory.EnumerateFileSystemEntries(dir, "*", SearchOption.AllDirectories)
                .Where(f => !f.Contains("/.git/", StringComparison.Ordinal))
                .ToList();
            string kind = BeltKinds.TryGetValue(belt.Key, out var k) ? k : "artefatos";
            string detail = "";
            bool artifacts = kind switch
            {
                "spec" => ValidateSpec(clean, belt.Key, out detail),
                "texto" => ValidateText(clean, belt.Key, out detail),
                "tools" => ValidateToolsFull(dir, out detail),
                "pipe" => ValidateDevPipe(dir, clean, out detail),
                _ => ValidateFiles(files, out detail),
            };
            var (req, inTok, outTok, elapsed, ring2, ring3, nudge) = LogWindow(logOffset);
            double tps = elapsed > 0 ? outTok / elapsed : 0;

            bool ok = exited && !unknownTool && output.Length > 0 && artifacts;
            Console.WriteLine($"[run] {(ok ? "PASS" : "FAIL")} {belt.Key}: timeout={!exited} out={output.Length} chars " +
                $"kind={kind} ok={artifacts} {detail} tps={tps:F1} tok/s req={req} in={inTok} out_tok={outTok} " +
                $"wall={sw.Elapsed.TotalSeconds:F0}s unknownTool={unknownTool} " +
                $"ring2={ring2} ring3={ring3} nudge={nudge} ({log})");
            RecordMetrics(dir, belt.Key, kind, sw.Elapsed.TotalSeconds, req, inTok, outTok, elapsed, tps, ok,
                ring2, ring3, nudge);
            if (!ok) failures++;
        }

        Console.WriteLine(failures == 0
            ? $"[run] PASS{ (skipped > 0 ? $" (skip={skipped})" : "") }"
            : $"[run] FAIL={failures} skip={skipped}");
        return failures;
    }

    /// <summary>Tamanho atual do log da bridge (marco do inicio da faixa).</summary>
    /// <returns>Bytes ja escritos; 0 quando o arquivo nao existe.</returns>
    static long LogOffset()
    {
        try { return File.Exists(BridgeLog) ? new FileInfo(BridgeLog).Length : 0; } catch { return 0; }
    }

    /// <summary>Soma os eventos `response` e as recusas por anel da janela da faixa.</summary>
    /// <param name="offset">Marco inicial em bytes.</param>
    /// <returns>Requests, tokens, decode, e recusas ring2/ring3/nudge.</returns>
    static (int Requests, long In, long Out, double Elapsed, int Ring2, int Ring3, int Nudge) LogWindow(long offset)
    {
        int req = 0, ring2 = 0, ring3 = 0, nudge = 0;
        long inTok = 0, outTok = 0;
        double elapsed = 0;
        try
        {
            if (!File.Exists(BridgeLog)) return (0, 0, 0, 0, 0, 0, 0);
            using var fs = new FileStream(BridgeLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (offset > fs.Length) offset = 0;              // log truncado por restart
            fs.Seek(offset, SeekOrigin.Begin);
            using var reader = new StreamReader(fs);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                // Recusas por anel (mede o desenho "catalogo cheio, deny no risco"):
                // ring2 = tool barrada por policy/role; ring3 = argv barrado pelo
                // inspetor de shell; nudge = args guiados (chamada refeita).
                if (line.Contains("\"event\":\"tool-deny\"", StringComparison.Ordinal)) ring3++;
                else if (line.Contains("\"event\":\"tool-args-guide\"", StringComparison.Ordinal)) nudge++;
                else if (line.Contains("\"event\":\"tool-blocked\"", StringComparison.Ordinal)
                      || line.Contains("\"event\":\"tool-ask\"", StringComparison.Ordinal)) ring2++;
                if (!line.Contains("\"event\":\"response\"", StringComparison.Ordinal)) continue;
                JsonElement e;
                try { using var doc = JsonDocument.Parse(line); e = doc.RootElement.Clone(); } catch { continue; }
                req++;
                inTok += Tok(e, "input_tokens");
                outTok += Tok(e, "output_tokens");
                if (e.TryGetProperty("elapsed", out var el) && el.ValueKind == JsonValueKind.Number) elapsed += el.GetDouble();
            }
        }
        catch { }
        return (req, inTok, outTok, elapsed, ring2, ring3, nudge);
    }

    /// <summary>Le um contador inteiro do evento (0 quando ausente).</summary>
    /// <param name="e">Evento JSON da bridge.</param>
    /// <param name="name">Nome do campo.</param>
    /// <returns>Valor do campo.</returns>
    static long Tok(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;

    /// <summary>Grava a metrica da faixa em `_metrics.jsonl` (comparacao entre rodadas).</summary>
    static void RecordMetrics(string dir, string belt, string kind, double wall, int req, long inTok, long outTok, double elapsed, double tps, bool ok,
        int ring2 = 0, int ring3 = 0, int nudge = 0)
    {
        try
        {
            string json = JsonSerializer.Serialize(new
            {
                belt,
                kind,
                ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                wall_s = Math.Round(wall, 1),
                requests = req,
                in_tok = inTok,
                out_tok = outTok,
                elapsed = Math.Round(elapsed, 2),
                tps = Math.Round(tps, 1),
                pass = ok,
                ring2_deny = ring2,
                ring3_deny = ring3,
                nudge_args = nudge,
            });
            File.AppendAllText(Path.Combine(dir, "_metrics.jsonl"), json + "\n");
        }
        catch { }
    }

    /// <summary>Remove sequencias ANSI do log (o CLI colore a saida).</summary>
    /// <param name="text">Saida crua.</param>
    /// <returns>Texto sem escapes.</returns>
    static string StripAnsi(string text) => ReAnsi.Replace(text, "");

    /// <summary>Escape ANSI (cores e titulos).</summary>
    static readonly Regex ReAnsi = new(@"\x1b\[[0-9;?]*[A-Za-z]|\x1b\][^\x07]*\x07", RegexOptions.Compiled);

    /// <summary>Minusculas sem acento (comparacao de topicos).</summary>
    /// <remarks>O projeto roda com `InvariantGlobalization=true` e nesse modo
    /// `String.Normalize(FormD)` e no-op (acento nunca sai); por isso o mapa explicito.</remarks>
    /// <param name="text">Texto do log.</param>
    /// <returns>Texto normalizado.</returns>
    static string Norm(string text)
    {
        string clean = StripAnsi(text);
        var sb = new StringBuilder(clean.Length);
        foreach (char raw in clean)
        {
            char c = char.ToLowerInvariant(raw);
            sb.Append(c switch
            {
                'á' or 'à' or 'â' or 'ã' or 'ä' or 'å' => 'a',
                'é' or 'è' or 'ê' or 'ë' => 'e',
                'í' or 'ì' or 'î' or 'ï' => 'i',
                'ó' or 'ò' or 'ô' or 'õ' or 'ö' => 'o',
                'ú' or 'ù' or 'û' or 'ü' => 'u',
                'ç' => 'c',
                'ñ' => 'n',
                _ => c,
            });
        }
        return sb.ToString();
    }

    /// <summary>Topicos do enunciado ausentes na resposta.</summary>
    /// <param name="text">Log ja normalizado.</param>
    /// <param name="key">Chave da faixa.</param>
    /// <returns>Lista dos que faltaram.</returns>
    static string[] MissingTopics(string text, string key) =>
        SpecTopics.TryGetValue(key, out var topics)
            ? topics.Where(t => !t.Split('|').Any(alt => text.Contains(alt, StringComparison.Ordinal))).ToArray()
            : Array.Empty<string>();

    /// <summary>Aceite das faixas de especificacao: resposta textual que cobre o escopo.</summary>
    /// <param name="clean">Saida do opencode sem ANSI.</param>
    /// <param name="key">Chave da faixa.</param>
    /// <param name="detail">Resumo para o log.</param>
    /// <returns>True quando o texto cobre o escopo pedido.</returns>
    static bool ValidateSpec(string clean, string key, out string detail)
    {
        string text = Norm(clean);
        var missing = MissingTopics(text, key);
        int deleg = Count(clean, "subagent", StringComparison.OrdinalIgnoreCase)
                  + Count(clean, "Subagent", StringComparison.Ordinal);
        detail = $"chars={clean.Length} falta={string.Join("|", missing)} deleg={deleg}";
        return clean.Length >= 1500 && missing.Length == 0;
    }

    /// <summary>Aceite da faixa cognitiva: texto puro (zero tool) cobrindo o escopo.</summary>
    /// <param name="clean">Saida do opencode sem ANSI.</param>
    /// <param name="key">Chave da faixa.</param>
    /// <param name="detail">Resumo para o log.</param>
    /// <returns>True quando respondeu em texto sem chamar tool.</returns>
    static bool ValidateText(string clean, string key, out string detail)
    {
        string text = Norm(clean);
        var missing = MissingTopics(text, key);
        int tools = ToolCalls(clean);
        detail = $"chars={clean.Length} tools={tools} falta={string.Join("|", missing)}";
        return clean.Length >= 1500 && missing.Length == 0 && tools == 0;
    }

    /// <summary>Aceite das faixas sem regra propria: pelo menos dois arquivos criados.</summary>
    /// <param name="files">Arquivos do workspace.</param>
    /// <param name="detail">Resumo para o log.</param>
    /// <returns>True quando produziu artefatos.</returns>
    static bool ValidateFiles(List<string> files, out string detail)
    {
        detail = $"files={files.Count}";
        return files.Count > 1;
    }

    /// <summary>Chamadas de tool impressas pelo CLI (linhas de icone).</summary>
    /// <param name="clean">Saida sem ANSI.</param>
    /// <returns>Quantidade de linhas de tool.</returns>
    static int ToolCalls(string clean)
    {
        int n = 0;
        foreach (var raw in clean.Split('\n'))
        {
            string line = raw.TrimStart();
            if (line.Length < 2) continue;
            if (line.StartsWith("$ ", StringComparison.Ordinal)
                || line.StartsWith("% WebFetch", StringComparison.Ordinal)
                || line.StartsWith("\u25C8", StringComparison.Ordinal)
                || line.StartsWith("\u2190 ", StringComparison.Ordinal)
                || line.StartsWith("\u2192 ", StringComparison.Ordinal)
                || line.StartsWith("\u2731 ", StringComparison.Ordinal)
                || line.StartsWith("\u2717 ", StringComparison.Ordinal)) n++;
        }
        return n;
    }

    /// <summary>Ocorrencias de um trecho no texto.</summary>
    /// <param name="text">Texto.</param>
    /// <param name="piece">Trecho procurado.</param>
    /// <param name="cmp">Comparacao.</param>
    /// <returns>Contagem.</returns>
    static int Count(string text, string piece, StringComparison cmp = StringComparison.Ordinal)
    {
        int n = 0, i = 0;
        while ((i = text.IndexOf(piece, i, cmp)) >= 0) { n++; i += piece.Length; }
        return n;
    }

    /// <summary>Historico git real do repo: hash, assunto e arquivos por commit.</summary>
    /// <param name="repo">Pasta do repositorio.</param>
    /// <returns>Commits na ordem do log (vazio quando nao ha repo).</returns>
    static List<(string Hash, string Subject, List<string> Files)> GitLog(string repo)
    {
        var result = new List<(string Hash, string Subject, List<string> Files)>();
        if (repo.Length == 0 || !Directory.Exists(Path.Combine(repo, ".git"))) return result;
        try
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = repo,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var a in new[] { "log", "--pretty=format:%h|%s", "--name-only" }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null) return result;
            string outp = p.StandardOutput.ReadToEnd();
            p.WaitForExit(20000);
            foreach (var rawLine in outp.Split('\n'))
            {
                string line = rawLine.Trim();
                if (line.Length == 0) continue;
                int bar = line.IndexOf('|');
                if (bar > 0 && bar <= 12 && line[..bar].All(Uri.IsHexDigit))
                {
                    result.Add((line[..bar], line[(bar + 1)..], new List<string>()));
                    continue;
                }
                if (result.Count > 0) result[^1].Files.Add(line);
            }
        }
        catch { }
        return result;
    }

    /// <summary>Aceite tools-full: repo tooltest, 2+ commits e plano.md versionado na criacao e na edicao.</summary>
    /// <param name="dir">Workspace do belt.</param>
    /// <param name="detail">Resumo do historico para o log.</param>
    /// <returns>True quando o historico comprova criacao + edicao.</returns>
    static bool ValidateToolsFull(string dir, out string detail)
    {
        string repo = Path.Combine(dir, "tooltest");
        string plan = Path.Combine(repo, "docs", "plano.md");
        var hist = GitLog(repo);
        int touches = hist.Count(c => c.Files.Any(f => f.EndsWith("docs/plano.md", StringComparison.Ordinal)));
        detail = $"git={hist.Count}cmt plano_em={touches}cmt";
        return File.Exists(plan) && hist.Count >= 2 && touches >= 2;
    }

    /// <summary>Aceite tools-dev-pipe: historico git, AWC preservado, zip e tools nativas exercitadas.</summary>
    /// <param name="dir">Workspace do belt.</param>
    /// <param name="clean">Saida do opencode sem ANSI (para contar as tools usadas).</param>
    /// <param name="detail">Resumo do historico e das tools para o log.</param>
    /// <returns>True quando o historico e os artefatos conferem.</returns>
    static bool ValidateDevPipe(string dir, string clean, out string detail)
    {
        string repo = Directory.EnumerateDirectories(dir, "devpipe-test-*").FirstOrDefault() ?? "";
        var hist = GitLog(repo);
        string txt = repo.Length > 0
            ? Directory.EnumerateFiles(repo, "*.txt", SearchOption.TopDirectoryOnly).FirstOrDefault() ?? ""
            : "";
        string rel = txt.Length > 0 ? Path.GetFileName(txt) : "";
        int touches = rel.Length > 0 ? hist.Count(c => c.Files.Any(f => f.Equals(rel, StringComparison.Ordinal))) : 0;
        bool content = false;
        try { content = txt.Length > 0 && File.ReadAllText(txt).Contains("AWC"); } catch { }
        bool zip = repo.Length > 0 && Directory.EnumerateFiles(repo, "*.zip", SearchOption.TopDirectoryOnly).Any();
        // 12B tende a editar por `sed -i` no shell: o pipe so vale se exercitar as tools nativas.
        var use = NativeTools.ToDictionary(t => t.Name, t => Count(clean, t.Icon));
        int edits = use["edit"] + use["write"];
        int lookup = use["read"] + use["grep"] + use["glob"];
        int fetch = use["fetch"] + use["search"];
        bool sed = clean.Contains("sed -i", StringComparison.Ordinal);
        detail = $"git={hist.Count}cmt txt_em={touches}cmt awc={content} zip={zip} " +
                 $"tools=e{edits}/r{lookup}/f{fetch} sed={sed}";
        return repo.Length > 0 && hist.Count >= 2 && touches >= 2 && content && zip
            && edits >= 2 && lookup >= 1 && fetch >= 1 && !sed;
    }
}
