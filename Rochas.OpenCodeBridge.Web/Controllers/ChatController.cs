using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Rochas.Data.Specification.Interfaces;
using Rochas.OpenCodeBridge.Web.Models;
using Rochas.OpenCodeBridge.Web.Services;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Rochas.OpenCodeBridge.Web.Controllers;

using System.Collections.Concurrent;

// Chat: tela + proxy de stream p/ bridge (contorna falta de CORS no HttpListener).
[Authorize]
public sealed class ChatController(
    IGenericRepository<Agent> agents,
    IPersistenceRepository<Agent> agentsWrite,
    ISessionService sessionService,
    IToolExecutor toolExecutor,
    IHttpClientFactory http,
    Services.IDiagnosticTelemetry telemetry,
    Microsoft.Extensions.Logging.ILogger<ChatController> log,
    Microsoft.Extensions.Options.IOptions<Services.OrchestrationOptions> orchOptions) : Controller
{
    private int CurrentUserId => int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "0");

    // Streams ativos por sessão p/ Parar via API (além do abort do fetch).
    private static readonly ConcurrentDictionary<int, CancellationTokenSource> ActiveStreams = new();

    public sealed class StopRequest
    {
        public int? SessionId { get; set; }
    }

    [HttpPost("/Chat/Stop")]
    public IActionResult Stop([FromBody] StopRequest req)
    {
        if (req.SessionId.HasValue && ActiveStreams.TryGetValue(req.SessionId.Value, out var cts))
        {
            try { cts.Cancel(); } catch { }
            return Json(new { ok = true });
        }
        return Json(new { ok = false });
    }

    // GET /Chat/Config - flags de UX (aprovacao direta, telemetria p/ o tracer).
    [HttpGet("/Chat/Config")]
    public IActionResult Config() => Json(new { directApprove = orchOptions.Value.DirectApprove, telemetry = telemetry.Enabled });

    // POST /Chat/ClientLog - lote do tracer do front p/ a tabela Serilog (sempre 200).
    public sealed class ClientEvent
    {
        public string? T { get; set; }
        public string? Source { get; set; }
        public string? Level { get; set; }
        public string? Msg { get; set; }
        public string? Data { get; set; }
    }

    public sealed class ClientLogRequest
    {
        public int? SessionId { get; set; }
        public List<ClientEvent>? Events { get; set; }
    }

    [HttpPost("/Chat/ClientLog")]
    [RequestSizeLimit(65536)]
    public async Task<IActionResult> ClientLog([FromBody] ClientLogRequest req)
    {
        try
        {
            int kept = 0;
            if (req?.Events is not null)
            {
                int? sid = null;
                if (req.SessionId.HasValue)
                {
                    var s = await sessionService.GetAsync(req.SessionId.Value, CurrentUserId);
                    if (s?.Id is int v) sid = v;
                }
                foreach (var e in req.Events.Take(50))
                {
                    if (e is null) continue;
                    string msg = e.Msg ?? "";
                    if (msg.Length > 500) msg = msg[..500];
                    string line = $"[ui] {(sid?.ToString() ?? "-")} {e.Source}: {msg}";
                    string lvl = (e.Level ?? "info").ToLowerInvariant();
                    if (lvl == "error") log.LogError(line);
                    else if (lvl == "warn") log.LogWarning(line);
                    else log.LogInformation(line);
                    kept++;
                }
            }
            return Json(new { ok = true, kept });
        }
        catch { return Json(new { ok = false, kept = 0 }); }
    }

    public async Task<IActionResult> Index()
    {
        // Só ativos, filtrado na ORM (bool true entra no WHERE; false é ignorado).
        var list = (await agents.Query(new Agent { Active = true })).Where(a => a.Active)
            .OrderByDescending(a => a.Model).ThenBy(a => a.Name).ToList();
        ViewBag.Agents = list;
        return View();
    }

    // Ping leve na bridge do agente p/ sinalizar conectividade da engine.
    [HttpGet("/Chat/Ping")]
    public async Task<IActionResult> Ping(int agentId)
    {
        var agent = (await agents.Query(new Agent())).FirstOrDefault(a => a.Id == agentId);
        if (agent is null) return Json(new { ok = false });
        try
        {
            var client = http.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(3);
            using var r = await client.GetAsync(agent.BridgeUrl.TrimEnd('/') + "/api/status");
            return Json(new { ok = r.IsSuccessStatusCode });
        }
        catch
        {
            return Json(new { ok = false });
        }
    }

    // ---- Sessions CRUD ----

    // Datas UTC com designador Z (o SQLite devolve Unspecified; sem Z o browser lê como local).
    private static string Utc(System.DateTime dt) =>
        System.DateTime.SpecifyKind(dt, System.DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ssZ");

    [HttpGet("/Chat/Sessions")]
    public async Task<IActionResult> GetSessions()
    {
        try
        {
        var list = await sessionService.GetByUserAsync(CurrentUserId);
        return Json(list.Select(s => new { s.Id, s.AgentId, s.ExecutorAgentId, s.Title, CreatedAt = Utc(s.CreatedAt), UpdatedAt = Utc(s.UpdatedAt) }));
        }
        catch (Exception ex) { try { telemetry.RegisterError(ex); } catch { } return Json(new object[0]); }
    }

    [HttpPost("/Chat/Sessions")]
    public async Task<IActionResult> CreateSession([FromBody] CreateSessionRequest req)
    {
        var agent = (await agents.Query(new Agent())).FirstOrDefault(a => a.Id == req.AgentId);
        if (agent is null || !agent.Active) return BadRequest("Agente inválido");
        int? execId = req.ExecutorAgentId;
        // Sem permissão de delegar, sem executor automático.
        if (!execId.HasValue && ToolDefinitions.Allowed(agent, "subagent"))
        {
            // Padrão (Orch = Plan + Build-Exec): executor Build-Exec, senão o 4B,
            // sempre diferente do orquestrador.
            var all = (await agents.Query(new Agent())).Where(a => a.Active && a.Id != req.AgentId).ToList();
            execId = all.Where(a => (a.Name ?? "").Contains("build-exec", StringComparison.OrdinalIgnoreCase))
                .Select(a => a.Id).FirstOrDefault()
                ?? all.Where(a => (a.Model ?? "").Contains("4b", StringComparison.OrdinalIgnoreCase))
                .Select(a => a.Id).FirstOrDefault();
        }
        if (execId.HasValue)
        {
            if (execId.Value == req.AgentId) return BadRequest("Executor deve diferir do orquestrador");
            var exec = (await agents.Query(new Agent())).FirstOrDefault(a => a.Id == execId.Value);
            if (exec is null || !exec.Active) return BadRequest("Executor inválido");
            // Sem amarra de modo: orquestrador plan + executor build é topologia válida.
        }
        var title = string.IsNullOrWhiteSpace(req.Title) ? "Nova sessão" : req.Title;
        var s = await sessionService.CreateAsync(CurrentUserId, req.AgentId, title, execId);
        return Json(new { s.Id, s.AgentId, s.ExecutorAgentId, s.Title, CreatedAt = Utc(s.CreatedAt) });
    }

    [HttpGet("/Chat/Sessions/{id:int}")]
    public async Task<IActionResult> GetSession(int id)
    {
        var s = await sessionService.GetAsync(id, CurrentUserId);
        if (s is null) return NotFound();
        return Json(new { s.Id, s.AgentId, s.ExecutorAgentId, s.Title, CreatedAt = Utc(s.CreatedAt), UpdatedAt = Utc(s.UpdatedAt) });
    }

    [HttpGet("/Chat/Sessions/{id:int}/Messages")]
    public async Task<IActionResult> GetSessionMessages(int id, int limit = 50)
    {
        try
        {
        var s = await sessionService.GetAsync(id, CurrentUserId);
        if (s is null) return NotFound();
        var msgs = await sessionService.GetMessagesAsync(id, limit);
        return Json(msgs.Select(m => new { m.Id, m.Role, m.Content, m.Thinking, m.PromptTokens, m.CompletionTokens, m.ElapsedMs, m.Tps, Status = Services.SessionService.MessageStatus(m.StatusId, m.Role ?? "", m.Archived), CreatedAt = Utc(m.CreatedAt) }));
        }
        catch (Exception ex) { try { telemetry.RegisterError(ex); } catch { } return Json(new object[0]); }
    }

    // GET /Chat/Sessions/{id}/ToolCalls - trilha de tools p/ avaliação posterior.
    [HttpGet("/Chat/Sessions/{id:int}/ToolCalls")]
    public async Task<IActionResult> GetSessionToolCalls(int id, int limit = 200)
    {
        var s = await sessionService.GetAsync(id, CurrentUserId);
        if (s is null) return NotFound();
        var calls = await sessionService.GetToolCallsAsync(id, limit);
        return Json(calls.Select(c => new { c.Id, c.Agent, c.Name, c.Args, c.Ok, c.Output, c.Ms, c.MessageId, c.Sub, c.CreatedAt }));
    }

    // GET /Chat/Sessions/{id}/Tasks - painel de acompanhamento da decomposição
    // (derivado das mensagens marcadas; sem tabela nova).
    [HttpGet("/Chat/Sessions/{id:int}/Tasks")]
    public async Task<IActionResult> GetSessionTasks(int id)
    {
        try
        {
        var s = await sessionService.GetAsync(id, CurrentUserId);
        if (s is null) return NotFound();
        var msgs = await sessionService.GetMessagesAsync(id, 200);
        var items = msgs.Select(m => (m.Role, m.Content)).ToList();
        var tasks = SessionTaskPanel.Parse(items);
        return Json(new
        {
            tasks = tasks.Select(t => new { index = t.Index, title = t.Title, status = t.Status, etaMin = t.EtaMin, promptSummary = t.PromptSummary, toolCalls = t.ToolCalls }),
            totalEtaMin = Math.Round(tasks.Sum(t => t.EtaMin), 1),
            synthesized = SessionTaskPanel.Synthesized(items, tasks.Count),
        });
        }
        catch (Exception ex) { try { telemetry.RegisterError(ex); } catch { } return Json(new { tasks = new object[0], totalEtaMin = 0.0, synthesized = false }); }
    }

    // GET /Chat/Sessions/{id}/Scripts - .sh gerados na sessão (write +
    // menções em comandos e resultados), p/ o painel lateral.
    [HttpGet("/Chat/Sessions/{id:int}/Scripts")]
    public async Task<IActionResult> GetSessionScripts(int id)
    {
        try
        {
        var s = await sessionService.GetAsync(id, CurrentUserId);
        if (s is null) return NotFound();
        var files = await CollectSessionScriptsAsync(id);
        string root = AppContext.BaseDirectory;
        var items = new List<object>();
        foreach (var f in files)
        {
            // ETA estimada pelo modelo no cabeçalho (`# ETA: ~12 min`); ausente = vazio.
            double? eta = null;
            try
            {
                string full = System.IO.Path.IsPathRooted(f) ? f : System.IO.Path.Combine(root, f);
                if (System.IO.File.Exists(full))
                {
                    using var fs = System.IO.File.OpenRead(full);
                    byte[] buf = new byte[4096];
                    int n = await fs.ReadAsync(buf, 0, buf.Length);
                    eta = Services.ScriptEta.ParseMinutes(System.Text.Encoding.UTF8.GetString(buf, 0, n));
                }
            }
            catch { }
            items.Add(new { path = f, etaMin = eta });
        }
        var zip = await sessionService.GetArtifactAsync(id, "scripts.zip");
        // Zip vazio (0 entradas, legado do scraper antigo) não é oferecido.
        object? zipInfo = null;
        if (zip is not null && !string.IsNullOrEmpty(zip.Value.B64))
        {
            try
            {
                using var ms = new MemoryStream(Convert.FromBase64String(zip.Value.B64));
                using var za = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Read);
                if (za.Entries.Count > 0)
                    zipInfo = new { name = "scripts.zip", size = fmtSize(zip.Value.B64.Length * 3 / 4) };
            }
            catch { }
        }
        return Json(new { scripts = items, zip = zipInfo });
        }
        catch (Exception ex) { try { telemetry.RegisterError(ex); } catch { } return Json(new { scripts = new object[0], zip = (object?)null }); }
    }

    // GET /Chat/Sessions/{id}/ScriptsZip - baixa o zip gravado ao fim do turno.
    [HttpGet("/Chat/Sessions/{id:int}/ScriptsZip")]
    public async Task<IActionResult> GetSessionScriptsZip(int id)
    {
        var s = await sessionService.GetAsync(id, CurrentUserId);
        if (s is null) return NotFound();
        var zip = await sessionService.GetArtifactAsync(id, "scripts.zip");
        if (zip is null || string.IsNullOrEmpty(zip.Value.B64)) return NotFound();
        byte[] bytes;
        try { bytes = Convert.FromBase64String(zip.Value.B64); }
        catch { return BadRequest("artefato ilegível"); }
        try
        {
            using var za = new System.IO.Compression.ZipArchive(new MemoryStream(bytes, false), System.IO.Compression.ZipArchiveMode.Read);
            if (za.Entries.Count == 0) return NotFound();
        }
        catch { return BadRequest("artefato ilegível"); }
        // [nome-da-sessao]-scripts-[data-hora].zip
        string slug = new string((s.Title ?? "sessao").ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray());
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        slug = slug.Trim('-');
        if (string.IsNullOrEmpty(slug)) slug = "sessao";
        if (slug.Length > 40) slug = slug[..40];
        string fname = $"{slug}-scripts-{DateTime.Now:yyyyMMdd-HHmmss}.zip";
        return File(bytes, string.IsNullOrEmpty(zip.Value.Mime) ? "application/zip" : zip.Value.Mime, fname);
    }

    // ---- Arquivos Alterados (painel + modal com edição e diff) ----

    private static string WorkspaceRoot => AppContext.BaseDirectory;

    private static string? ToolPath(System.Text.Json.Nodes.JsonNode? args)
    {
        try
        {
            var f = args?["path"]?.GetValue<string>()?.Trim().Trim('"', '\'');
            return string.IsNullOrWhiteSpace(f) ? null : f;
        }
        catch { return null; }
    }

    // GET /Chat/Sessions/{id}/ChangedFiles - todo arquivo tocado por write/edit
    // ok na sessão (mais recente primeiro), p/ o painel lateral.
    [HttpGet("/Chat/Sessions/{id:int}/ChangedFiles")]
    public async Task<IActionResult> GetChangedFiles(int id)
    {
        try
        {
        var s = await sessionService.GetAsync(id, CurrentUserId);
        if (s is null) return NotFound();
        var calls = await sessionService.GetToolCallsAsync(id, 500);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<object>();
        foreach (var c in calls.OrderByDescending(t => t.Id ?? 0))
        {
            if (!c.Ok) continue;
            if (!string.Equals(c.Name, "write", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(c.Name, "edit", StringComparison.OrdinalIgnoreCase)) continue;
            string? f;
            try { f = ToolPath(System.Text.Json.Nodes.JsonNode.Parse(c.Args ?? "")?.AsObject()); }
            catch { continue; }
            if (string.IsNullOrWhiteSpace(f) || !seen.Add(f)) continue;
            bool exists = false; long size = 0; string? modified = null;
            if (Services.WorkspaceGuard.TryResolve(WorkspaceRoot, f, out string full, out _))
            {
                try
                {
                    var fi = new System.IO.FileInfo(full);
                    exists = fi.Exists;
                    if (exists) { size = fi.Length; modified = fi.LastWriteTimeUtc.ToString("yyyy-MM-ddTHH:mm:ssZ"); }
                }
                catch { }
            }
            string kind = (c.Output ?? "").Contains("criado") ? "novo" : "editado";
            list.Add(new { path = f, exists, size, modified, kind });
        }
        return Json(list);
        }
        catch (Exception ex) { try { telemetry.RegisterError(ex); } catch { } return Json(new object[0]); }
    }

    // GET /Chat/Sessions/{id}/File?path=... - conteúdo p/ o modal (teto 1MB, sem binário).
    [HttpGet("/Chat/Sessions/{id:int}/File")]
    public async Task<IActionResult> GetSessionFile(int id, [FromQuery] string path)
    {
        var s = await sessionService.GetAsync(id, CurrentUserId);
        if (s is null) return NotFound();
        if (!Services.WorkspaceGuard.TryResolve(WorkspaceRoot, path ?? "", out string full, out string why))
            return BadRequest(why);
        System.IO.FileInfo fi;
        try { fi = new System.IO.FileInfo(full); } catch { return BadRequest("caminho inválido"); }
        if (!fi.Exists) return NotFound();
        if (fi.Length > 1_000_000) return StatusCode(413, "arquivo grande demais p/ o modal (teto 1MB)");
        byte[] bytes;
        try { bytes = await System.IO.File.ReadAllBytesAsync(full); }
        catch { return BadRequest("falha ao ler"); }
        if (bytes.Contains((byte)0)) return StatusCode(415, "binário não suportado no modal");
        return Json(new { path, content = System.Text.Encoding.UTF8.GetString(bytes), size = bytes.Length, modified = fi.LastWriteTimeUtc.ToString("yyyy-MM-ddTHH:mm:ssZ") });
    }

    public sealed class SaveFileRequest
    {
        public string Path { get; set; } = "";
        public string Content { get; set; } = "";
    }

    // POST /Chat/Sessions/{id}/File - salva edição do modal (guard de workspace, teto 1MB).
    [HttpPost("/Chat/Sessions/{id:int}/File")]
    public async Task<IActionResult> SaveSessionFile(int id, [FromBody] SaveFileRequest req)
    {
        var s = await sessionService.GetAsync(id, CurrentUserId);
        if (s is null) return NotFound();
        if (!Services.WorkspaceGuard.TryResolve(WorkspaceRoot, req?.Path ?? "", out string full, out string why))
            return BadRequest(why);
        string content = req?.Content ?? "";
        if (content.Length > 1_000_000) return StatusCode(413, "conteúdo grande demais (teto 1MB)");
        try
        {
            string? dir = System.IO.Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);
            await System.IO.File.WriteAllTextAsync(full, content);
        }
        catch (Exception ex) { return BadRequest($"falha ao salvar: {ex.Message.Split('\n')[0]}"); }
        if (s.Id.HasValue)
        {
            await sessionService.LogToolAsync(s.Id.Value, "modal", "write",
                new System.Text.Json.Nodes.JsonObject { ["path"] = req!.Path }.ToJsonString(),
                true, $"atualizado via modal {content.Length} chars em '{req.Path}'", 0);
            await sessionService.TouchAsync(s.Id.Value);
        }
        return Json(new { ok = true });
    }

    // GET /Chat/Sessions/{id}/FileDiff?path=... - git diff do arquivo (lado a lado no front).
    [HttpGet("/Chat/Sessions/{id:int}/FileDiff")]
    public async Task<IActionResult> GetSessionFileDiff(int id, [FromQuery] string path)
    {
        var s = await sessionService.GetAsync(id, CurrentUserId);
        if (s is null) return NotFound();
        if (!Services.WorkspaceGuard.TryResolve(WorkspaceRoot, path ?? "", out string full, out string why))
            return BadRequest(why);
        if (!System.IO.File.Exists(full)) return NotFound();
        string dir = System.IO.Path.GetDirectoryName(full) ?? WorkspaceRoot;
        string top;
        try
        {
            var rp = Services.ProcessRunner.Run("git", new[] { "rev-parse", "--show-toplevel" }, dir, 15);
            if (!rp.Success) return Json(new { diff = (string?)null, message = "fora de repo git: diff indisponível" });
            top = (rp.Output ?? "").Trim();
        }
        catch (Exception ex) { return Json(new { diff = (string?)null, message = $"git indisponível: {ex.Message.Split('\n')[0]}" }); }
        string rel = System.IO.Path.GetRelativePath(top, full);
        string diff;
        try
        {
            var d = Services.ProcessRunner.Run("git", new[] { "diff", "--no-color", "--", rel }, top, 15);
            diff = d.Success ? (d.Output ?? "") : "";
            if (string.IsNullOrWhiteSpace(diff))
            {
                // Untracked: --no-index sai 1 quando HÁ diferenças (é o normal
                // aqui) e o Fail esvazia Output — o diff vem em "exit 1: ...".
                var u = Services.ProcessRunner.Run("git", new[] { "diff", "--no-color", "--no-index", "/dev/null", full }, top, 15);
                string uout = u.Output ?? "";
                if (string.IsNullOrWhiteSpace(uout) && (u.Error ?? "").StartsWith("exit 1", StringComparison.Ordinal))
                    uout = u.Error!.Substring(u.Error.IndexOf(": ", StringComparison.Ordinal) + 2).Replace("\n[stderr]\n", "\n");
                diff = uout;
            }
        }
        catch (Exception ex) { return Json(new { diff = (string?)null, message = $"falha no git diff: {ex.Message.Split('\n')[0]}" }); }
        if (diff.Length > 200_000) diff = diff[..200_000] + "\n[...cortado: diff > 200KB]";
        if (string.IsNullOrWhiteSpace(diff)) return Json(new { diff = (string?)null, message = "sem diferenças no git" });
        return Json(new { diff });
    }

    private static string fmtSize(long bytes) =>
        bytes >= 1048576 ? $"{bytes / 1048576.0:0.#} MB" : bytes >= 1024 ? $"{bytes / 1024.0:0.#} KB" : $"{bytes} B";

    private async Task<List<string>> CollectSessionScriptsAsync(int sessionId)
    {
        var calls = await sessionService.GetToolCallsAsync(sessionId, 500);
        var files = new List<string>();
        // Só .sh CRIADO na sessão (write ok com "criado"): write com falha,
        // overwrite de preexistente ("atualizado"), menções em outputs (ls/grep)
        // ou comandos (bash -n) não entram — aquilo listava .sh da raiz do
        // projeto como se fossem da sessão.
        foreach (var c in calls)
        {
            try
            {
                if (!c.Ok) continue;
                if (!string.Equals(c.Name, "write", StringComparison.OrdinalIgnoreCase)) continue;
                if (!(c.Output ?? "").Contains("criado")) continue;
                var args = System.Text.Json.Nodes.JsonNode.Parse(c.Args ?? "")?.AsObject();
                string? f = args?["path"]?.GetValue<string>()?.Trim().Trim('"', '\'');
                if (!string.IsNullOrWhiteSpace(f) && f.EndsWith(".sh", StringComparison.OrdinalIgnoreCase)
                    && !files.Contains(f)) files.Add(f);
            }
            catch { }
        }
        return files;
    }

    // F5/abort/Parar no meio do stream: persiste o parcial acumulado p/ a
    // sessão não sumir do banco. Bolha marcada [interrompido]; órfãs ligadas
    // a ela. Best-effort, nunca quebra o pipeline.
    private async Task SavePartialAsync(Session? session, System.Text.StringBuilder content, System.Text.StringBuilder thinking, List<int> unlinkedToolIds, System.Diagnostics.Stopwatch sw)
    {
        try
        {
            if (session?.Id is not int sid) return;
            string text = StripThinkTags(content.ToString(), thinking).Trim();
            if (text.Length == 0 && thinking.Length == 0) return;
            long ms = sw.ElapsedMilliseconds;
            int msgId = await sessionService.AddMessageAsync(sid, "assistant", text + "\n\n[interrompido]", thinking.ToString(), null, null, ms, null);
            await sessionService.TouchAsync(sid);
            List<int> orphans;
            lock (unlinkedToolIds) orphans = unlinkedToolIds.ToList();
            if (orphans.Count > 0) await sessionService.LinkToolsByIdsAsync(sid, orphans, msgId);
            await ZipSessionScriptsAsync(sid);
        }
        catch { }
    }

    // Ao fim do turno com scripts: zipa os .sh do workspace e grava em base64
    // (download no quadro da direita). Best-effort, nunca quebra o pipeline.
    private async Task ZipSessionScriptsAsync(int sessionId)
    {
        try
        {
            var files = await CollectSessionScriptsAsync(sessionId);
            if (files.Count == 0) return;
            string root = AppContext.BaseDirectory;
            using var ms = new MemoryStream();
            int entries = 0;
            using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, true))
            {
                foreach (var f in files)
                {
                    string name = System.IO.Path.GetFileName(f);
                    string full = System.IO.Path.IsPathRooted(f) ? f : System.IO.Path.Combine(root, f);
                    if (!System.IO.File.Exists(full)) continue;
                    var entry = zip.CreateEntry(name, System.IO.Compression.CompressionLevel.Optimal);
                    using var es = entry.Open();
                    using var fs = System.IO.File.OpenRead(full);
                    await fs.CopyToAsync(es);
                    entries++;
                }
            }
            if (entries == 0) return;
            await sessionService.SaveArtifactAsync(sessionId, "scripts.zip", "application/zip",
                Convert.ToBase64String(ms.ToArray()));
        }
        catch { /* best-effort */ }
    }

    [HttpDelete("/Chat/Sessions/{id:int}")]
    public async Task<IActionResult> DeleteSession(int id)
    {
        await sessionService.DeleteAsync(id, CurrentUserId);
        return Ok();
    }

    // GET /Chat/Sessions/{id}/Context - tamanho do contexto: real (prompt_tokens
    // do último turno, medido pelo modelo) com fallback p/ estimativa chars/4.
    [HttpGet("/Chat/Sessions/{id:int}/Context")]
    public async Task<IActionResult> GetSessionContext(int id)
    {
        try
        {
        var s = await sessionService.GetAsync(id, CurrentUserId);
        if (s is null) return NotFound();
        var msgs = await sessionService.GetMessagesAsync(id, int.MaxValue);
        var live = msgs.Where(m => m.Archived == 0).ToList();
        int tokens = ContextWindow.EstimateTotalTokens(live);
        var measured = live.LastOrDefault(m => (m.PromptTokens ?? 0) > 0);
        int real = measured?.PromptTokens ?? 0;
        // Uso real + delta estimado depois da última medição (o usage é do
        // turno passado; o que entrou depois soma por chars/4).
        int newer = 0;
        if (measured is not null)
            newer = ContextWindow.EstimateTotalTokens(live.SkipWhile(m => m.Id != measured.Id).Skip(1).ToList());
        var ag = (await agents.Query(new Agent())).FirstOrDefault(a => a.Id == s.AgentId);
        int budget = ag is null ? ContextWindow.MaxInputTokens : ContextWindow.ResolveMaxInput(ag.Model, ag.BridgeUrl);
        return Json(new { tokens, real, newer, budget });
        }
        catch (Exception ex) { try { telemetry.RegisterError(ex); } catch { } return Json(new { tokens = 0, real = 0, newer = 0, budget = 0 }); }
    }

    // POST /Chat/Sessions/{id}/Compact — resume o antigo, mantém a cauda.
    [HttpPost("/Chat/Sessions/{id:int}/Compact")]
    public async Task<IActionResult> CompactSession(int id)
    {
        var s = await sessionService.GetAsync(id, CurrentUserId);
        if (s is null) return NotFound();
        var agent = (await agents.Query(new Agent())).FirstOrDefault(a => a.Id == s.AgentId);
        if (agent is null || !agent.Active) return NotFound();
        int removed = await CompactSessionAsync(s, agent, HttpContext.RequestAborted);
        return Json(new { compacted = removed > 0, removed });
    }

    // Compactação: resumo via bridge (fatiado) + troca no banco. O resumo
    // volta como mensagem do agente (visível no histórico e no contexto).
    private async Task<int> CompactSessionAsync(Session session, Agent agent, CancellationToken ct, int keepLast = 8)
    {
        return await sessionService.CompactAsync(session.Id!.Value, async headText =>
        {
            var parts = new List<string>();
            for (int i = 0; i < headText.Length && parts.Count < 5; i += 20000)
                parts.Add(headText.Substring(i, Math.Min(20000, headText.Length - i)));
            var sums = new List<string>();
            foreach (string part in parts)
            {
                var body = new JsonObject
                {
                    ["model"] = agent.Model,
                    ["temperature"] = 0.4,
                    ["stream"] = false,
                    ["messages"] = new JsonArray
                    {
                        new JsonObject { ["role"] = "system", ["content"] = "Resuma fielmente a conversa abaixo em até 20 linhas: decisões, arquivos citados, resultados de tools e pendências. Sem enrolação." },
                        new JsonObject { ["role"] = "user", ["content"] = part },
                    },
                };
                using var req = new HttpRequestMessage(HttpMethod.Post, agent.BridgeUrl.TrimEnd('/') + "/v1/chat/completions")
                {
                    Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
                };
                using var sumClient = http.CreateClient();
                using var res = await sumClient.SendAsync(req, ct);
                if (!res.IsSuccessStatusCode) return null;
                try
                {
                    var json = JsonNode.Parse(await res.Content.ReadAsStringAsync(ct))?.AsObject();
                    string? text = json?["choices"]?[0]?["message"]?["content"]?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(text)) sums.Add(text!.Trim());
                }
                catch { return null; }
            }
            return sums.Count == 0 ? null : string.Join("\n", sums);
        }, keepLast);
    }

    [HttpPut("/Chat/Sessions/{id:int}/Title")]
    public async Task<IActionResult> UpdateSessionTitle(int id, [FromBody] UpdateTitleRequest req)
    {
        var s = await sessionService.GetAsync(id, CurrentUserId);
        if (s is null) return NotFound();
        await sessionService.UpdateTitleAsync(id, req.Title);
        return Ok();
    }

    [HttpPut("/Chat/Sessions/{id:int}/Agent")]
    public async Task<IActionResult> UpdateSessionAgent(int id, [FromBody] UpdateAgentRequest req)
    {
        var s = await sessionService.GetAsync(id, CurrentUserId);
        if (s is null) return NotFound();
        var agent = (await agents.Query(new Agent())).FirstOrDefault(a => a.Id == req.AgentId);
        if (agent is null || !agent.Active) return BadRequest("Agente inválido");
        await sessionService.UpdateAgentAsync(id, req.AgentId);
        return Ok();
    }

    [HttpPut("/Chat/Sessions/{id:int}/Executor")]
    public async Task<IActionResult> UpdateSessionExecutor(int id, [FromBody] UpdateExecutorRequest req)
    {
        var s = await sessionService.GetAsync(id, CurrentUserId);
        if (s is null) return NotFound();
        if (req.ExecutorAgentId.HasValue)
        {
            if (req.ExecutorAgentId.Value == s.AgentId) return BadRequest("Executor deve diferir do orquestrador");
            var exec = (await agents.Query(new Agent())).FirstOrDefault(a => a.Id == req.ExecutorAgentId.Value);
            if (exec is null || !exec.Active) return BadRequest("Executor inválido");
            // Sem amarra de modo: orquestrador plan + executor build é topologia válida.
        }
        await sessionService.UpdateExecutorAsync(id, req.ExecutorAgentId);
        return Ok();
    }

    // POST /Chat/Executors/{id}/Probe - mede tok/s do agente e grava em measured_tps.
    [HttpPost("/Chat/Executors/{id:int}/Probe")]
    public async Task<IActionResult> ProbeExecutor(int id, [FromServices] IBridgeClient bridge, CancellationToken ct)
    {
        var agent = (await agents.Query(new Agent())).FirstOrDefault(a => a.Id == id);
        if (agent is null || !agent.Active) return NotFound();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = await BridgeHelper.ChatAsync(bridge, agent.BridgeUrl, agent.Model, agent.Temperature,
            "Responda somente: ok",
            new JsonArray { new JsonObject { ["role"] = "user", ["content"] = "Responda somente: ok" } }, ct);
        sw.Stop();
        int total = (r.PromptTokens ?? 0) + (r.CompletionTokens ?? 0);
        if (total <= 0) total = Math.Max(1, r.Content.Length / 4);
        if (!r.Ok || sw.Elapsed.TotalSeconds <= 0)
            return BadRequest("Sonda falhou: " + r.Error);
        double tps = total / sw.Elapsed.TotalSeconds;
        agent.MeasuredTps = Math.Round(tps, 1);
        await agentsWrite.Update(agent, new Agent { Id = agent.Id });
        return Json(new { tps = agent.MeasuredTps });
    }

    public sealed class OrchestrateRequest
    {
        public int SessionId { get; set; }
        public string Text { get; set; } = "";
    }

    public sealed class OrchestrateApprovedRequest
    {
        public int SessionId { get; set; }
        public List<ApprovedTask> Tasks { get; set; } = new();
        public bool Synthesize { get; set; } = true;
    }

    public sealed class SynthesizeRequest
    {
        public int SessionId { get; set; }
    }

    public sealed class ApprovedTask
    {
        public string Title { get; set; } = "";
        public string Prompt { get; set; } = "";
        public double EtaMin { get; set; }
        public bool NeedsTools { get; set; }
    }

    public sealed class CreateSessionRequest
    {
        public int AgentId { get; set; }
        public int? ExecutorAgentId { get; set; }
        public string Title { get; set; } = "";
    }

    public sealed class UpdateTitleRequest
    {
        public string Title { get; set; } = "";
    }

    public sealed class UpdateAgentRequest
    {
        public int AgentId { get; set; }
    }

    public sealed class UpdateExecutorRequest
    {
        public int? ExecutorAgentId { get; set; }
    }

    // ---- Stream com sessão + tool calling ----

    public sealed class StreamRequest
    {
        public int? SessionId { get; set; }
        public int AgentId { get; set; }
        public List<ChatMsg> Messages { get; set; } = new();
    }

    public sealed class ChatMsg
    {
        public string Role { get; set; } = "user";
        public string Content { get; set; } = "";
    }

    [HttpPost("/Chat/Stream")]
    public async Task Stream([FromBody] StreamRequest req, CancellationToken ct)
    {
        Agent? agent;
        Session? session = null;
        List<SessionMessage> history = new();

        // Retomar: mensagens em branco não entram (nem no contexto, nem no banco).
        req.Messages = req.Messages.Where(m => !string.IsNullOrWhiteSpace(m.Content)).ToList();

        if (req.SessionId.HasValue)
        {
            session = await sessionService.GetAsync(req.SessionId.Value, CurrentUserId);
            if (session is null) { Response.StatusCode = 404; return; }
            agent = (await agents.Query(new Agent())).FirstOrDefault(a => a.Id == session.AgentId);
            if (agent is null || !agent.Active) { Response.StatusCode = 404; return; }
            history = await sessionService.GetMessagesAsync(req.SessionId.Value);
        }
        else
        {
            agent = await agents.Get(new Agent { Id = req.AgentId });
            if (agent is null || !agent.Active) { Response.StatusCode = 404; return; }
        }

        // Monta mensagens para a bridge
        var messages = new JsonArray();
        // NÃO adiciona system prompt aqui — o BridgeClient já faz isso via parâmetro

        // Contexto com janela de tokens (subagent gravado como histórico vira user p/ bridge).
        var context = ContextWindow.BuildContext(history, ContextWindow.ResolveMaxInput(agent.Model, agent.BridgeUrl));
        foreach (var m in context)
            messages.Add(new JsonObject { ["role"] = m.Role == "subagent" ? "assistant" : m.Role, ["content"] = m.Content });

        // Mensagens novas do request (se sem sessão) ou última mensagem do usuário
        if (!req.SessionId.HasValue)
        {
            foreach (var m in req.Messages.TakeLast(20))
                messages.Add(new JsonObject { ["role"] = m.Role is "assistant" or "system" ? m.Role : "user", ["content"] = m.Content ?? "" });
        }
        else if (req.Messages.Count > 0)
        {
            var last = req.Messages.Last();
            messages.Add(new JsonObject { ["role"] = last.Role is "assistant" or "system" ? last.Role : "user", ["content"] = last.Content ?? "" });
        }

        Response.ContentType = "text/event-stream; charset=utf-8";
        Response.Headers.CacheControl = "no-cache";

        // Persiste mensagem do usuário se há sessão
        if (session is not null && req.Messages.Count > 0 && session.Id.HasValue)
        {
            var lastUser = req.Messages.Last(m => m.Role == "user");
            await sessionService.AddMessageAsync(session.Id.Value, "user", lastUser.Content, "", null, null);
        }

        // Stream com tool calling loop (cancelável via /Chat/Stop + abort do fetch).
        if (session is not null && session.Id.HasValue)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            ActiveStreams[session.Id.Value] = linked;
            try { await StreamWithTools(agent, messages, session, linked.Token); }
            catch (OperationCanceledException) { /* Parar via API ou abort do fetch */ }
            catch (Exception ex)
            {
                // Qualquer falha no meio do loop: nunca prende a UI sem DONE.
                try
                {
                    if (!Response.HasStarted) Response.StatusCode = 502;
                    else
                    {
                        var ee = new JsonObject { ["error"] = "falha no stream: " + ex.Message };
                        await Response.Body.WriteAsync(Encoding.UTF8.GetBytes("data: " + ee.ToJsonString() + "\n\n"), linked.Token);
                        await Response.Body.FlushAsync(linked.Token);
                        await Response.Body.WriteAsync(Encoding.UTF8.GetBytes("data: [DONE]\n\n"), linked.Token);
                        await Response.Body.FlushAsync(linked.Token);
                    }
                }
                catch { }
            }
            finally { ActiveStreams.TryRemove(session.Id.Value, out _); }
        }
        else
        {
            try { await StreamWithTools(agent, messages, session, ct); }
            catch (OperationCanceledException) { }
            catch (Exception)
            {
                try { if (!Response.HasStarted) Response.StatusCode = 502; } catch { }
            }
        }
    }

    private async Task StreamWithTools(Agent agent, JsonArray messages, Session? session, CancellationToken ct)
    {
        // Cronômetro do stream inteiro: duração da bolha final do agente.
        var streamSw = System.Diagnostics.Stopwatch.StartNew();
        // Bypass padrão (flag Orchestration:UseResponsesBypass): turno único via
        // /v1/responses com passthrough da bridge — SEM ToolExecutor, só o
        // sanitize leve (system-first + drop de reasoning) do lado da bridge.
        if (orchOptions.Value.UseResponsesBypass)
        {
            await StreamResponsesBypass(agent, messages, session, ct);
            return;
        }
        var outputStream = Response.Body;
        var client = http.CreateClient();
        client.Timeout = Timeout.InfiniteTimeSpan;

        var assistantContent = new System.Text.StringBuilder();
        var assistantThinking = new System.Text.StringBuilder();
        int? promptTokens = null;
        int? completionTokens = null;
        // Fechamento silencioso: turno final vazio após tools ganha nota de conclusão.
        bool anyToolRan = false;
        bool anySubRan = false;
        // Anti-giro: turnos só-leitura sem texto nem delegate disparam
        // enunciado corretivo (máx 2); sem progresso, aborta honesto.
        int idleTurns = 0, giroFixes = 0, repeatSig = 0;
        string lastToolSig = "";
        bool giroAbort = false;
        // Vínculo tool->bolha: ids de tools gravadas sem mensagem dona (turno sem
        // texto ou internas de sub cujo card falhou) vão p/ a mensagem final.
        var unlinkedToolIds = new List<int>();
        // Último sub visto no sink de internas (execução serial: é o dono atual).
        string? lastSub = null;
        // Compactação automática: 1 retry após resumir (contexto cheio).
        bool compactRetried = false;
        // Cutucadas DeepSeek em prosa (máx 2, contador próprio p/ não
        // interferir no anti-giro).
        int dsqNudges = 0;
        // Gate de escopo do subagent (máx 2 recusas; depois delega do jeito
        // que veio p/ não travar o run).
        int subScopeRejects = 0;
        // Enunciado original (primeira user do contexto): base do gate.
        string originalTask = "";
        foreach (var m in messages)
            if (m?["role"]?.GetValue<string>() == "user"
                && m?["content"]?.GetValue<string>() is string u && u.Length > originalTask.Length)
                originalTask = u;

        // Helper to build fresh messages array from values (avoids JsonNode parent issues)
        JsonArray BuildMessages(JsonArray src)
        {
            var dst = new JsonArray();
            foreach (var node in src)
            {
                if (node is JsonObject obj)
                {
                    var clone = new JsonObject();
                    foreach (var kv in obj)
                    {
                        var val = kv.Value;
                        if (val is JsonArray arr)
                        {
                            // Rebuild array from values
                            var newArr = new JsonArray();
                            foreach (var item in arr)
                            {
                                if (item is JsonObject itemObj)
                                {
                                    var itemClone = new JsonObject();
                                    foreach (var ikv in itemObj)
                                        itemClone[ikv.Key] = ikv.Value?.DeepClone();
                                    newArr.Add(itemClone);
                                }
                                else
                                {
                                    newArr.Add(item?.DeepClone());
                                }
                            }
                            clone[kv.Key] = newArr;
                        }
                        else
                        {
                            clone[kv.Key] = val?.DeepClone();
                        }
                    }
                    dst.Add(clone);
                }
            }
            return dst;
        }

        // Executor da sessão: chamadas subagent rodam no modelo/bridge dele.
        Agent? execAgent = null;
        if (session?.ExecutorAgentId is int execId)
        {
            try { execAgent = (await agents.Query(new Agent())).FirstOrDefault(a => a.Id == execId && a.Active); } catch { }
        }
        try
        {
        while (!ct.IsCancellationRequested)
        {
            // Build executa todas as tools e NÃO delega subagente; plan delega e não executa escrita.
            bool plan = string.Equals(agent.Mode, "plan", StringComparison.OrdinalIgnoreCase);
            var tools = new JsonArray();
            foreach (var t in ToolDefinitions.GetTools())
            {
                if ((t as JsonObject)?["function"]?["name"]?.GetValue<string>() is not string tn) continue;
                if (!ToolDefinitions.Allowed(agent, tn)) continue;
                if (t is not null) tools.Add(t.DeepClone());
            }
            // DeepSeek em prosa ignora tools com "auto": o 1º turno força chamada;
            // com subagent anunciado, força nominal (foco: delegar); depois volta
            // a "auto" p/ poder concluir em texto (sem isto, loop infinito).
            System.Text.Json.Nodes.JsonNode toolChoice = System.Text.Json.Nodes.JsonValue.Create("auto");
            if (DeepSeekToolCoercion.IsDeepSeek(agent.Model) && !anyToolRan && tools.Count > 0)
                toolChoice = DeepSeekToolCoercion.NamesOf(tools).Contains("subagent")
                    ? new JsonObject { ["type"] = "function", ["function"] = new JsonObject { ["name"] = "subagent" } }
                    : System.Text.Json.Nodes.JsonValue.Create("required");
            var body = new JsonObject
            {
                ["model"] = agent.Model,
                ["messages"] = BuildMessages(messages),
                ["temperature"] = agent.Temperature,
                ["stream"] = true,
                // Trava anti-eco degenerado ([shell] ok x500): repete menos.
                ["repetition_penalty"] = 1.15,
                ["tools"] = tools,
                ["tool_choice"] = toolChoice,
                // Sem isto o vLLM não devolve usage no stream e o donut
                // trabalhava no chute (chars/4).
                ["stream_options"] = new JsonObject { ["include_usage"] = true },
                ["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = agent.Thinking != "off" },
            };

            // Repasse ao vivo (sem buffer total): ResponseHeadersRead ou o PostAsync
            // espera o corpo inteiro antes de entregar qualquer linha.
            using var bridgeReq = new HttpRequestMessage(HttpMethod.Post, agent.BridgeUrl.TrimEnd('/') + "/v1/chat/completions")
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
            };
            using var res = await client.SendAsync(bridgeReq, HttpCompletionOption.ResponseHeadersRead, ct);

            if (!res.IsSuccessStatusCode)
            {
                if (!Response.HasStarted) { Response.StatusCode = 502; return; }
                // Contexto cheio: compacta e tenta o turno de novo (1 vez).
                string errBody = "";
                try { errBody = await res.Content.ReadAsStringAsync(ct); } catch { }
                if (!compactRetried && (errBody.Contains("maximum context", StringComparison.OrdinalIgnoreCase) || errBody.Contains("exceed", StringComparison.OrdinalIgnoreCase))
                    && session?.Id is int sidCtx)
                {
                    compactRetried = true;
                    int removed = 0;
                    try { removed = await CompactSessionAsync(session, agent, ct); } catch { }
                    if (removed > 0)
                    {
                        try
                        {
                            var fresh = await sessionService.GetMessagesAsync(sidCtx, int.MaxValue);
                            var keep = ContextWindow.BuildContext(fresh, ContextWindow.ResolveMaxInput(agent.Model, agent.BridgeUrl));
                            messages.Clear();
                            foreach (var m in keep)
                                messages.Add(new JsonObject { ["role"] = m.Role == "subagent" ? "assistant" : m.Role, ["content"] = m.Content });
                            var note = new JsonObject { ["error"] = $"contexto cheio: histórico compactado ({removed} msgs resumidas), tentando de novo." };
                            await outputStream.WriteAsync(Encoding.UTF8.GetBytes("data: " + note.ToJsonString() + "\n\n"), ct);
                            await outputStream.FlushAsync(ct);
                        }
                        catch { }
                        continue;
                    }
                }
                // Falha com stream aberto: avisa a UI e cai no epílogo (persiste + DONE).
                try
                {
                    var be = new JsonObject { ["error"] = $"bridge {(int)res.StatusCode}" };
                    await outputStream.WriteAsync(Encoding.UTF8.GetBytes("data: " + be.ToJsonString() + "\n\n"), ct);
                    await outputStream.FlushAsync(ct);
                }
                catch { }
                break;
            }

            // Repasse ao vivo: cada linha da bridge vai ao cliente asim que chega
            // (sem CopyToAsync — buffer total matava o efeito "digitando").
            using var stream = await res.Content.ReadAsStreamAsync(ct);
            using var reader = new System.IO.StreamReader(stream);
            string? line;
            // Accumulate tool calls by index (arguments split across chunks)
            var toolCallAccum = new Dictionary<int, (string Id, string Name, StringBuilder Args)>();
            string? finishReason = null;

            while ((line = await reader.ReadLineAsync()) != null)
            {
                // Segura o [DONE] da bridge: o DONE do cliente sai após persistir.
                bool isDone = line.StartsWith("data: ") && line[6..].Trim() == "[DONE]";
                if (!isDone)
                {
                    var lineBytes = System.Text.Encoding.UTF8.GetBytes(line + "\n");
                    await outputStream.WriteAsync(lineBytes, ct);
                    await outputStream.FlushAsync(ct);
                }

                // Parsa SSE para capturar assistant content/thinking/tool_calls/usage
                if (line.StartsWith("data: "))
                {
                    var data = line[6..].Trim();
                    if (data == "[DONE]") continue;
                    try
                    {
                        var chunk = JsonNode.Parse(data);
                        var choices = chunk?["choices"]?.AsArray();
                        if (choices?.Count > 0)
                        {
                            var delta = choices[0]?["delta"];
                            if (delta?["reasoning_content"]?.GetValue<string>() is string rc)
                                assistantThinking.Append(rc);
                            if (delta?["content"]?.GetValue<string>() is string cc)
                                assistantContent.Append(cc);

                            // Captura tool_calls
                            if (delta?["tool_calls"] is JsonArray tcArr)
                            {
                                foreach (var tc in tcArr)
                                {
                                    var tcObj = tc?.AsObject();
                                    if (tcObj is null) continue;
                                    var index = tcObj["index"]?.GetValue<int>() ?? 0;
                                    var id = tcObj["id"]?.GetValue<string>() ?? "";
                                    var fn = tcObj["function"]?.AsObject();
                                    if (fn is null) continue;
                                    var name = fn["name"]?.GetValue<string>() ?? "";
                                    var argsChunk = fn["arguments"]?.GetValue<string>() ?? "";
                                    
                                    if (!toolCallAccum.TryGetValue(index, out var existing))
                                    {
                                        existing = (id, name, new StringBuilder());
                                        toolCallAccum[index] = existing;
                                    }
                                    if (id.Length > 0) existing.Id = id;
                                    if (name.Length > 0) existing.Name = name;
                                    existing.Args.Append(argsChunk);
                                }
                            }

                            finishReason = choices[0]?["finish_reason"]?.GetValue<string>();
                        }
                        if (chunk?["usage"] is JsonObject usage)
                        {
                            promptTokens = usage["prompt_tokens"]?.GetValue<int>();
                            completionTokens = usage["completion_tokens"]?.GetValue<int>();
                        }
                    }
                    catch { /* ignora parse errors */ }
                }
            }

            // DeepSeek com choice forçado devolve calls com finish "stop":
            // normaliza p/ o fluxo normal (só deepseek; Qwen intacto).
            if (DeepSeekToolCoercion.IsDeepSeek(agent.Model) && finishReason != "tool_calls"
                && toolCallAccum.Values.Any(t => !string.IsNullOrEmpty(t.Id) && !string.IsNullOrEmpty(t.Name)))
                finishReason = "tool_calls";

            // Caminho DeepSeek (padrão oficial em texto): sem tool_calls
            // estruturadas, o modelo escreve a chamada no texto
            // (<tool_name>/hermes/fence/tokens). Coage para o fluxo normal de
            // execução, sem mudar prompts.
            if ((toolCallAccum.Count == 0 || finishReason != "tool_calls")
                && tools.Count > 0
                && DeepSeekToolCoercion.IsDeepSeek(agent.Model))
            {
                var coerced = DeepSeekToolCoercion.Coerce(agent.Model,
                    assistantContent.ToString(), out string dsqCleaned,
                    DeepSeekToolCoercion.NamesOf(tools));
                int dsqIdx = toolCallAccum.Count;
                foreach (var c in coerced)
                    toolCallAccum[dsqIdx++] = (c.Id, c.Name, new StringBuilder(c.Args));
                if (coerced.Count > 0)
                {
                    finishReason = "tool_calls";
                    assistantContent.Clear();
                    assistantContent.Append(dsqCleaned);
                }
            }

            // Sem tools anunciadas: sem tool_calls; sem coerção — só texto, termina.
            if (toolCallAccum.Count == 0 || finishReason != "tool_calls")
            {
                // DeepSeek em prosa (vê as tools mas discursa): cutucada mecânica
                // com o formato exato (máx 2, sem tocar prompts — linha anti-giro).
                if (DeepSeekToolCoercion.IsDeepSeek(agent.Model) && tools.Count > 0
                    && dsqNudges < 2 && assistantContent.Length > 0)
                {
                    dsqNudges++;
                    string prose = StripThinkTags(assistantContent.ToString(), assistantThinking);
                    if (!string.IsNullOrWhiteSpace(prose))
                        messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = prose });
                    messages.Add(new JsonObject { ["role"] = "user", ["content"] =
                        $"[DeepSeek-tools {dsqNudges}/2] Você respondeu em prosa sem chamar tools. " +
                        "As tools estão no seu contexto (bloco # Tools). Para agir, emita a chamada " +
                        "no formato exato, sem prosa junto. Para delegar uma fase: " +
                        "<｜tool▁calls▁begin｜><｜tool▁call▁begin｜>function<｜tool▁sep｜>subagent\n" +
                        "```json\n{\"prompt\": \"enunciado da fase\"}\n```<｜tool▁call▁end｜>" +
                        "<｜tool▁calls▁end｜>" });
                    assistantContent.Clear();
                    assistantThinking.Clear();
                    continue;
                }
                break;
            }

            // Converte accumulated tool calls para lista
            var toolCalls = toolCallAccum.Values
                .Where(t => !string.IsNullOrEmpty(t.Id) && !string.IsNullOrEmpty(t.Name))
                .Select(t => (Id: t.Id, Name: t.Name, Args: t.Args.ToString()))
                .ToList();

            // Deduplica calls idênticas no mesmo turno (o modelo repete
            // glob/read): executa 1x. Sem isto, o mesmo subagente pintava 2 cards.
            var seenCall = new HashSet<string>();
            toolCalls = toolCalls.Where(t => seenCall.Add(t.Name + ":" + t.Args)).ToList();

            // Anti-giro: 3 turnos só-leitura sem texto nem delegate, ou a mesma
            // chamada 3x seguidas. Corrige com enunciado (2x) ou aborta honesto.
            // As leituras do turno são descartadas (nunca executaram).
            bool hasSubCall = toolCalls.Any(t => t.Name == "subagent");
            bool readOnlyCall = toolCalls.Count > 0 && toolCalls.All(t =>
                t.Name is "read" or "grep" or "glob" or "fetch" or "git" or "shell");
            string turnTxt0 = StripThinkTags(assistantContent.ToString(), assistantThinking).Trim();
            if (!hasSubCall && readOnlyCall && turnTxt0.Length == 0) idleTurns++; else idleTurns = 0;
            string sig = string.Join(";", toolCalls.Select(t => t.Name + ":" + t.Args));
            if (sig == lastToolSig) repeatSig++; else { repeatSig = 1; lastToolSig = sig; }
            if (idleTurns >= 3 || repeatSig >= 3)
            {
                if (giroFixes < 2)
                {
                    giroFixes++;
                    messages.Add(new JsonObject { ["role"] = "user",
                        ["content"] = $"[Anti-giro {giroFixes}/2] Voce esta ha varios turnos so navegando (leitura) sem produzir texto nem delegar. PARE de chamar tools de leitura: resuma o que ja achou em 3 linhas e delegue a proxima fase via subagent tool." });
                    assistantContent.Clear();
                    assistantThinking.Clear();
                    idleTurns = 0;
                    repeatSig = 0;
                    continue;
                }
                giroAbort = true;
                break;
            }

            // Ao contexto volta SÓ texto (user/assistant/subagente): outputs de
            // tools NÃO entram no loop (persistidos em tool_calls + histórico).
            // Sem tool_calls no contexto (sem elas, a API exige as respostas).
            string turnContent = StripThinkTags(assistantContent.ToString(), assistantThinking);
            if (!string.IsNullOrWhiteSpace(turnContent))
                messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = turnContent });

            // Persiste o texto do turno ao fechar (o que a bolha já exibiu),
            // sem esperar o fim do loop com as tools. Turno só-com-espaço e sem
            // thinking não grava (evita bolhas vazias no reload).
            // <think> vazado no content: move p/ thinking (não suja bolha/contexto).
            string turnText = StripThinkTags(assistantContent.ToString(), assistantThinking).Trim();
            int turnMsgId = 0;
            if (session?.Id is int sidTurn && (turnText.Length > 0 || assistantThinking.Length > 0))
            {
                try
                {
                    turnMsgId = await sessionService.AddMessageAsync(sidTurn, "assistant",
                        turnText, assistantThinking.ToString(), null, null);
                    await sessionService.TouchAsync(sidTurn);
                }
                catch { /* histórico best-effort */ }
            }

            // Executa cada tool e adiciona resultado às mensagens.
            // Progresso vai ao cliente em tempo real para a UI indicar execução.
            foreach (var (id, name, args) in toolCalls)
            {
                anyToolRan = true;
                if (name == "subagent") anySubRan = true;
                // Call vazia (sem prompt): nem anuncia (evita label fantasma).
                if (name == "subagent" && string.IsNullOrWhiteSpace(SubPrompt(args)))
                {
                    if (session?.Id is int sidEmpty)
                        await sessionService.LogToolAsync(sidEmpty, agent.Name, "subagent", args, false, "recusado: prompt vazio", 0);
                    messages.Add(new JsonObject { ["role"] = "user",
                        ["content"] = "[subagent] recusado: prompt vazio. Reenvie a fase com objetivo, arquivos e o que fazer." });
                    continue;
                }
                await WriteProgressAsync(outputStream, "tool_start", name, null, ct, "", 0, "", args);
                // Card imediato no "digitando": o sub real adota ao no primeiro chunk.
                if (name == "subagent") await WriteProgressAsync(outputStream, "sub_start", name, null, ct);
                // Gate de escopo: subagent recebe UMA fase, nunca o enunciado
                // integral (máx 2 recusas; depois aceita p/ não travar).
                if (name == "subagent" && subScopeRejects < 2)
                {
                    string subPrompt = "";
                    try
                    {
                        subPrompt = JsonNode.Parse(args)?.AsObject()?["prompt"]?.GetValue<string>() ?? "";
                    }
                    catch { /* args ilegível: deixa passar p/ o handler falhar */ }
                    if (SubagentScopeGate.IsFullTaskPass(originalTask, subPrompt))
                    {
                        subScopeRejects++;
                        string rej = SubagentScopeGate.RejectMessage(subScopeRejects);
                        if (session?.Id is int sidRej)
                            await sessionService.LogToolAsync(sidRej, agent.Name, "subagent", args, false, rej, 0, null, turnMsgId > 0 ? turnMsgId : null);
                        await WriteProgressAsync(outputStream, "tool_done", name, false, ct, rej, 0, "", args);
                        // Corretivo volta ao contexto como user (fala com o modelo).
                        messages.Add(new JsonObject { ["role"] = "user", ["content"] = rej });
                        continue;
                    }
                }
                // Trunfo: o modelo vê plan (anunciado), o executor roda build (pode tudo).
                var sw = System.Diagnostics.Stopwatch.StartNew();
                // Progresso + chunks ao vivo das tools internas do sub: só aqui.
                toolExecutor.ProgressAsync = name == "subagent"
                    ? async p => await WriteProgressAsync(outputStream,
                        p.Ok is null ? "tool_start" : "tool_done", p.Name, p.Ok, ct, p.Summary ?? "", 0, p.Sub ?? "")
                    : null;
                toolExecutor.SubChunkAsync = name == "subagent"
                    ? async (sub, kind, text) => await WriteSubChunkAsync(outputStream, sub, kind, text, ct)
                    : null;
                // Trilha das internas do sub (com o sub dono; a mensagem dona
                // liga depois, no save do card). Fora de sessão: sem persistir.
                lastSub = null;
                toolExecutor.LogInnerAsync = name == "subagent" && session?.Id is int sidInner
                    ? async log =>
                    {
                        int toolId = await sessionService.LogToolAsync(sidInner, agent.Name,
                            log.Name, log.Args, log.Ok, log.Output, log.Ms, log.Sub);
                        if (!string.IsNullOrEmpty(log.Sub)) lastSub = log.Sub;
                        lock (unlinkedToolIds) unlinkedToolIds.Add(toolId);
                    }
                    : null;
                var result = toolExecutor.Execute(name, args, 300, null, name == "subagent" && execAgent is not null ? execAgent : agent, name == "subagent" ? agent : null); // contexto no executor, permissão no orquestrador
                toolExecutor.ProgressAsync = null;
                toolExecutor.SubChunkAsync = null;
                toolExecutor.LogInnerAsync = null;
                sw.Stop();
                if (session?.Id is int sidLog)
                {
                    int outerId = await sessionService.LogToolAsync(sidLog, agent.Name, name, args,
                        result.Success, result.Success ? result.Output : result.Error ?? "", sw.ElapsedMilliseconds,
                        null, turnMsgId > 0 ? turnMsgId : null);
                    if (turnMsgId <= 0) lock (unlinkedToolIds) unlinkedToolIds.Add(outerId);
                }
                // Retorno do subagente entra na árvore da sessão (bolha Sub Agente no reload).
                string? subText = null;
                if (session?.Id is int sidSub && name == "subagent" && result.Success)
                {
                    try
                    {
                        string sbody = result.Output ?? "", sthink = "";
                        int si = sbody.IndexOf("<SUBTHINK>", StringComparison.Ordinal);
                        int ei = sbody.IndexOf("</SUBTHINK>", StringComparison.Ordinal);
                        if (si >= 0 && ei > si)
                        {
                            sthink = sbody.Substring(si + "<SUBTHINK>".Length, ei - si - "<SUBTHINK>".Length).Trim();
                            sbody = (sbody[..si] + sbody[(ei + "</SUBTHINK>".Length)..]).Trim();
                        }
                        if (sbody.Length > 0 || sthink.Length > 0)
                        {
                            long subMs = sw.ElapsedMilliseconds;
                            int subMsgId = await sessionService.AddMessageAsync(sidSub, "subagent", sbody, sthink, null, null,
                                subMs, TpsEstimate(sbody + "\n" + sthink, subMs));
                            await sessionService.TouchAsync(sidSub);
                            // Card dono das internas: liga as tools do sub a ele.
                            if (lastSub is not null)
                            {
                                await sessionService.LinkSubToolsAsync(sidSub, lastSub, subMsgId);
                                lastSub = null;
                            }
                        }
                        if (sbody.Length > 0) subText = sbody;
                    }
                    catch { /* histórico best-effort: nunca quebra o pipeline */ }
                }
                string output = result.Output ?? "";
                string error = result.Error ?? "";
                string contentFull = result.Success ? output : $"Error: {error}";
                await WriteProgressAsync(outputStream, "tool_done", name, result.Success, ct, contentFull, sw.ElapsedMilliseconds, "", args);
                // Outputs NÃO voltam ao contexto; só o texto do subagente (como assistant).
                if (subText is not null)
                {
                    // Resumo curto p/ contexto: o texto integral (com as linhas
                    // [write]/[shell] ok) fazia o modelo recitar o eco em loop.
                    messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = "[subagente] " + subText });
                }
            }

            // Reseta para próxima iteração
            assistantContent.Clear();
            assistantThinking.Clear();
        }
        }
        catch (OperationCanceledException)
        {
            // F5/abort/Parar no meio do turno: persiste o parcial acumulado
            // p/ a sessão não sumir do banco (bolha marcada [interrompido]).
            await SavePartialAsync(session, assistantContent, assistantThinking, unlinkedToolIds, streamSw);
            return;
        }

        // Persiste resposta final do assistant se há sessão (texto real ou thinking).
        // Giro sem saída após 2 correções: nota honesta no lugar do silêncio.
        if (giroAbort)
        {
            assistantContent.Clear();
            assistantContent.Append("Interrompido por giro em leitura: o agente navegou sem produzir texto nem delegar após 2 correções automáticas.");
            assistantThinking.Clear();
        }
        string finalText = StripThinkTags(assistantContent.ToString(), assistantThinking).Trim();
        // Se content vazio mas thinking tem resposta, usa thinking (limpando tags) como content.
        if (string.IsNullOrWhiteSpace(finalText) && assistantThinking.Length > 0)
        {
            finalText = StripThinkTags(assistantThinking.ToString(), new System.Text.StringBuilder()).Trim();
            assistantThinking.Clear();
        }
        bool silentClose = false;
        if (finalText.Length == 0 && assistantThinking.Length == 0 && (anySubRan || anyToolRan))
        {
            // Retorno silencioso após execução: fecha a bolha do agente e grava no sqlite.
            finalText = anySubRan
                ? "As tarefas delegadas aos subagentes foram concluídas."
                : "As ferramentas solicitadas foram executadas, verifique.";
            silentClose = true;
        }
        if (session is not null && session.Id.HasValue && (finalText.Length > 0 || assistantThinking.Length > 0))
        {
            long streamMs = streamSw.ElapsedMilliseconds;
            int finalMsgId = await sessionService.AddMessageAsync(session.Id.Value, "assistant", finalText, assistantThinking.ToString(), promptTokens, completionTokens,
                streamMs, TpsOf(completionTokens, streamMs));
            await sessionService.TouchAsync(session.Id.Value);
            // Órfãs (turno sem texto / card que falhou) fecham na bolha final.
            if (unlinkedToolIds.Count > 0)
            {
                List<int> orphans;
                lock (unlinkedToolIds) orphans = unlinkedToolIds.ToList();
                await sessionService.LinkToolsByIdsAsync(session.Id.Value, orphans, finalMsgId);
            }
            await ZipSessionScriptsAsync(session.Id.Value);
        }

        // DONE próprio após persistir: elimina race do cliente ler histórico cedo.
        // Sempre tenta entregar (cliente abortado só ignora).
        try
        {
            if (silentClose || giroAbort)
            {
                // Fechamento silencioso também digita na bolha ao vivo.
                var fb = new JsonObject
                {
                    ["choices"] = new JsonArray { new JsonObject { ["delta"] = new JsonObject { ["content"] = finalText } } },
                };
                await outputStream.WriteAsync(Encoding.UTF8.GetBytes("data: " + fb.ToJsonString() + "\n\n"), ct);
                await outputStream.FlushAsync(ct);
            }
            var doneBytes = Encoding.UTF8.GetBytes("data: [DONE]\n\n");
            await outputStream.WriteAsync(doneBytes, ct);
            await outputStream.FlushAsync(ct);
        }
        catch { }
    }

    // Bypass: turno único contra {bridge}/v1/responses (passthrough da bridge),
    // Bypass com harness nativo de tools: anuncia as tools em formato Responses,
    // traduz o SSE Responses -> chat SSE ao vivo (a UI já entende choices[].delta),
    // executa function_call via ToolExecutor e reenvia function_call_output
    // (o loop agêntico mora aqui; a bridge só transporta). Persiste e fecha [DONE].
    private async Task StreamResponsesBypass(Agent agent, JsonArray messages, Session? session, CancellationToken ct)
    {
        // Cronômetro do stream inteiro: duração da bolha final do agente.
        var streamSw = System.Diagnostics.Stopwatch.StartNew();
        var outputStream = Response.Body;
        var client = http.CreateClient();
        client.Timeout = Timeout.InfiniteTimeSpan;

        var assistantContent = new System.Text.StringBuilder();
        var assistantThinking = new System.Text.StringBuilder();
        int? promptTokens = null, completionTokens = null;

        // Temperatura e thinking vêm do cadastro do agente (sem override no código).
        double temp = agent.Temperature;
        // Harness do cadastro (CSV); subagent segue o Mode.
        var chatTools = new JsonArray();
        foreach (var t in ToolDefinitions.GetTools())
        {
            if ((t as JsonObject)?["function"]?["name"]?.GetValue<string>() is not string tn) continue;
            if (!ToolDefinitions.Allowed(agent, tn)) continue;
            if (t is not null) chatTools.Add(t.DeepClone());
        }
        var respTools = Services.ResponsesBypass.ToResponsesTools(chatTools);
        var input = Services.ResponsesBypass.ToInputItems(messages);
        // Vínculo tool->bolha + gate de escopo (paridade com o loop chat).
        var unlinkedToolIds = new List<int>();
        string? lastSub = null;
        int subScopeRejects = 0;
        // Bolha viva do orquestrador: INSERT no 1º turno com texto, UPDATE nos
        // seguintes (crash-safe); subagentes seguem em linhas próprias.
        int liveMsgId = 0;
        // Retry único após compactar (contexto cheio), paridade com o loop chat.
        bool compactRetried = false;
        string originalTask = "";
        foreach (var m in messages)
            if (m?["role"]?.GetValue<string>() == "user"
                && m?["content"]?.GetValue<string>() is string u && u.Length > originalTask.Length)
                originalTask = u;

        // Executor da sessão: chamadas subagent rodam no modelo/bridge dele.
        Agent? execAgent = null;
        if (session?.ExecutorAgentId is int execId)
        {
            try { execAgent = (await agents.Query(new Agent())).FirstOrDefault(a => a.Id == execId && a.Active); } catch { }
        }
        try
        {
        for (int turn = 0; turn < 5 && !ct.IsCancellationRequested; turn++)
        {
            var body = new JsonObject
            {
                ["model"] = agent.Model,
                ["input"] = input.DeepClone(),
                ["stream"] = true,
                ["temperature"] = temp,
            };
            string instructions = agent.SystemPrompt ?? "";
            if (!string.IsNullOrWhiteSpace(instructions)) body["instructions"] = instructions;
            if (respTools.Count > 0) body["tools"] = respTools.DeepClone();

            using var bridgeReq = new HttpRequestMessage(HttpMethod.Post, agent.BridgeUrl.TrimEnd('/') + "/v1/responses")
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
            };
            using var res = await client.SendAsync(bridgeReq, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!res.IsSuccessStatusCode)
            {
                if (!Response.HasStarted) { Response.StatusCode = 502; return; }
                // Contexto cheio (llama: exceed_context_size / exceeds available
                // context; outros: maximum context): compacta e tenta 1 vez.
                string errBody = "";
                try { errBody = await res.Content.ReadAsStringAsync(ct); } catch { }
                if (!compactRetried && session is not null && session.Id.HasValue
                    && (errBody.Contains("maximum context", StringComparison.OrdinalIgnoreCase)
                        || errBody.Contains("exceed", StringComparison.OrdinalIgnoreCase)))
                {
                    compactRetried = true;
                    int removed = 0;
                    try { removed = await CompactSessionAsync(session, agent, ct); } catch { }
                    if (removed > 0)
                    {
                        try
                        {
                            var fresh = await sessionService.GetMessagesAsync(session.Id.Value, int.MaxValue);
                            var keep = ContextWindow.BuildContext(fresh, ContextWindow.ResolveMaxInput(agent.Model, agent.BridgeUrl));
                            var jm = new JsonArray();
                            foreach (var m in keep)
                                jm.Add(new JsonObject { ["role"] = m.Role == "subagent" ? "assistant" : m.Role, ["content"] = m.Content });
                            input = Services.ResponsesBypass.ToInputItems(jm);
                            var note = new JsonObject { ["error"] = $"contexto cheio: histórico compactado ({removed} msgs resumidas), tentando de novo." };
                            await outputStream.WriteAsync(Encoding.UTF8.GetBytes("data: " + note.ToJsonString() + "\n\n"), ct);
                            await outputStream.FlushAsync(ct);
                        }
                        catch { }
                        continue;
                    }
                }
                try
                {
                    var be = new JsonObject { ["error"] = $"bridge {(int)res.StatusCode}" };
                    await outputStream.WriteAsync(Encoding.UTF8.GetBytes("data: " + be.ToJsonString() + "\n\n"), ct);
                    await outputStream.FlushAsync(ct);
                }
                catch { }
                return;
            }

            var accum = new Services.ResponsesBypass.CallAccum();
            using var stream = await res.Content.ReadAsStreamAsync(ct);
            using var reader = new System.IO.StreamReader(stream);
            string? line;
            bool done = false;
            while (!done && (line = await reader.ReadLineAsync()) != null)
            {
                if (ct.IsCancellationRequested) break;
                if (!line.StartsWith("data: ")) continue;
                string data = line[6..].Trim();
                var t = Services.ResponsesBypass.TranslateData(data, assistantContent, assistantThinking,
                    ref promptTokens, ref completionTokens, accum);
                if (t.Error is not null)
                {
                    try
                    {
                        var be = new JsonObject { ["error"] = t.Error };
                        await outputStream.WriteAsync(Encoding.UTF8.GetBytes("data: " + be.ToJsonString() + "\n\n"), ct);
                        await outputStream.FlushAsync(ct);
                    }
                    catch { }
                    done = true;
                    break;
                }
                if (t.ChatData is not null && t.ChatData != "[DONE]")
                {
                    try
                    {
                        await outputStream.WriteAsync(Encoding.UTF8.GetBytes("data: " + t.ChatData + "\n\n"), ct);
                        await outputStream.FlushAsync(ct);
                    }
                    catch { break; }
                }
                done = t.Completed;
            }

            if (accum.Calls.Count == 0) break;
            // Executa cada call e devolve outputs no próximo turno (a API exige
            // function_call_output p/ toda call; sem isso o modelo trava).
            foreach (var (id, name, args) in accum.Calls)
            {
                // Call vazia (sem prompt): nem anuncia (evita label fantasma).
                if (name == "subagent" && string.IsNullOrWhiteSpace(SubPrompt(args)))
                {
                    if (session?.Id is int sidEmpty)
                        await sessionService.LogToolAsync(sidEmpty, agent.Name, "subagent", args, false, "recusado: prompt vazio", 0);
                    input.Add(new JsonObject
                    {
                        ["type"] = "message", ["role"] = "user",
                        ["content"] = new JsonArray(new JsonObject { ["type"] = "input_text",
                            ["text"] = "[subagent] recusado: prompt vazio. Reenvie a fase com objetivo, arquivos e o que fazer." }),
                    });
                    continue;
                }
                await WriteProgressAsync(outputStream, "tool_start", name, null, ct, "", 0, "", args);
                // Card imediato no "digitando": o sub real adota ao no primeiro chunk.
                if (name == "subagent") await WriteProgressAsync(outputStream, "sub_start", name, null, ct);
                // Gate de escopo: subagent recebe UMA fase (máx 2 recusas).
                if (name == "subagent" && subScopeRejects < 2)
                {
                    string subPrompt = "";
                    try { subPrompt = JsonNode.Parse(args)?.AsObject()?["prompt"]?.GetValue<string>() ?? ""; }
                    catch { /* args ilegível: deixa passar */ }
                    if (SubagentScopeGate.IsFullTaskPass(originalTask, subPrompt))
                    {
                        subScopeRejects++;
                        string rej = SubagentScopeGate.RejectMessage(subScopeRejects);
                        if (session?.Id is int sidRej)
                            await sessionService.LogToolAsync(sidRej, agent.Name, "subagent", args, false, rej, 0);
                        await WriteProgressAsync(outputStream, "tool_done", name, false, ct, rej, 0, "", args);
                        input.Add(new JsonObject
                        {
                            ["type"] = "message", ["role"] = "user",
                            ["content"] = new JsonArray(new JsonObject { ["type"] = "input_text", ["text"] = rej }),
                        });
                        continue;
                    }
                }
                var sw = System.Diagnostics.Stopwatch.StartNew();
                toolExecutor.ProgressAsync = name == "subagent"
                    ? async p => await WriteProgressAsync(outputStream,
                        p.Ok is null ? "tool_start" : "tool_done", p.Name, p.Ok, ct, p.Summary ?? "", 0, p.Sub ?? "")
                    : null;
                toolExecutor.SubChunkAsync = name == "subagent"
                    ? async (sub, kind, text) => await WriteSubChunkAsync(outputStream, sub, kind, text, ct)
                    : null;
                lastSub = null;
                toolExecutor.LogInnerAsync = name == "subagent" && session?.Id is int sidInner
                    ? async log =>
                    {
                        int toolId = await sessionService.LogToolAsync(sidInner, agent.Name,
                            log.Name, log.Args, log.Ok, log.Output, log.Ms, log.Sub);
                        if (!string.IsNullOrEmpty(log.Sub)) lastSub = log.Sub;
                        lock (unlinkedToolIds) unlinkedToolIds.Add(toolId);
                    }
                    : null;
                var result = toolExecutor.Execute(name, args, 300, null, name == "subagent" && execAgent is not null ? execAgent : agent, name == "subagent" ? agent : null); // contexto no executor, permissão no orquestrador
                toolExecutor.ProgressAsync = null;
                toolExecutor.SubChunkAsync = null;
                toolExecutor.LogInnerAsync = null;
                sw.Stop();
                if (session?.Id is int sidLog)
                {
                    int outerId = await sessionService.LogToolAsync(sidLog, agent.Name, name, args,
                        result.Success, result.Success ? result.Output : result.Error ?? "", sw.ElapsedMilliseconds);
                    lock (unlinkedToolIds) unlinkedToolIds.Add(outerId);
                }
                string? subText = null;
                if (session?.Id is int sidSub && name == "subagent" && result.Success)
                {
                    try
                    {
                        string sbody = result.Output ?? "", sthink = "";
                        int si = sbody.IndexOf("<SUBTHINK>", StringComparison.Ordinal);
                        int ei = sbody.IndexOf("</SUBTHINK>", StringComparison.Ordinal);
                        if (si >= 0 && ei > si)
                        {
                            sthink = sbody.Substring(si + "<SUBTHINK>".Length, ei - si - "<SUBTHINK>".Length).Trim();
                            sbody = (sbody[..si] + sbody[(ei + "</SUBTHINK>".Length)..]).Trim();
                        }
                        if (sbody.Length > 0 || sthink.Length > 0)
                        {
                            long subMs = sw.ElapsedMilliseconds;
                            int subMsgId = await sessionService.AddMessageAsync(sidSub, "subagent", sbody, sthink, null, null,
                                subMs, TpsEstimate(sbody + "\n" + sthink, subMs));
                            await sessionService.TouchAsync(sidSub);
                            // Card dono das internas: liga as tools do sub a ele.
                            if (lastSub is not null)
                            {
                                await sessionService.LinkSubToolsAsync(sidSub, lastSub, subMsgId);
                                lastSub = null;
                            }
                        }
                        if (sbody.Length > 0) subText = sbody;
                    }
                    catch { /* histórico best-effort */ }
                }
                string contentFull = result.Success ? (result.Output ?? "") : $"Error: {result.Error}";
                await WriteProgressAsync(outputStream, "tool_done", name, result.Success, ct, contentFull, sw.ElapsedMilliseconds, "", args);
                input.Add(new JsonObject
                {
                    ["type"] = "function_call", ["id"] = id, ["call_id"] = id,
                    ["name"] = name, ["arguments"] = args,
                });
                input.Add(new JsonObject
                {
                    ["type"] = "function_call_output", ["call_id"] = id, ["output"] = contentFull,
                });
                if (subText is not null)
                {
                    var sm = new JsonObject
                    {
                        ["type"] = "message", ["role"] = "assistant",
                        ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = "[subagente] " + subText }),
                    };
                    input.Add(sm);
                }
                // Bolha viva: persiste o texto do orquestrador ao fim de cada
                // turno (INSERT uma vez, UPDATE depois) — crash-safe, sem bolhas
                // duplicadas no reload. Subagentes têm linhas próprias.
                try
                {
                    string progText = StripThinkTags(assistantContent.ToString(), assistantThinking).Trim();
                    if (session?.Id is int sidProg && (progText.Length > 0 || assistantThinking.Length > 0))
                    {
                        if (liveMsgId <= 0)
                        {
                            liveMsgId = await sessionService.AddMessageAsync(sidProg, "assistant",
                                progText, assistantThinking.ToString(), null, null);
                            await sessionService.TouchAsync(sidProg);
                        }
                        else await sessionService.UpdateMessageAsync(liveMsgId, progText, assistantThinking.ToString());
                    }
                }
                catch { /* histórico best-effort */ }
            }
        }
        }
        catch (OperationCanceledException)
        {
            // F5/abort/Parar no meio do turno: persiste o parcial acumulado
            // p/ a sessão não sumir do banco (bolha marcada [interrompido]).
            await SavePartialAsync(session, assistantContent, assistantThinking, unlinkedToolIds, streamSw);
            return;
        }

        string finalText = StripThinkTags(assistantContent.ToString(), assistantThinking).Trim();
        if (session is not null && session.Id.HasValue && (finalText.Length > 0 || assistantThinking.Length > 0))
        {
            long streamMs = streamSw.ElapsedMilliseconds;
            // Bolha viva vira final: UPDATE com texto + usage (sem duplicar no reload).
            int finalMsgId;
            if (liveMsgId > 0)
            {
                await sessionService.UpdateMessageAsync(liveMsgId, finalText, assistantThinking.ToString(), promptTokens, completionTokens,
                    streamMs, TpsOf(completionTokens, streamMs));
                finalMsgId = liveMsgId;
            }
            else
            {
                finalMsgId = await sessionService.AddMessageAsync(session.Id.Value, "assistant", finalText, assistantThinking.ToString(), promptTokens, completionTokens,
                    streamMs, TpsOf(completionTokens, streamMs));
            }
            await sessionService.TouchAsync(session.Id.Value);
            // Órfãs (card que falhou) fecham na bolha final.
            if (unlinkedToolIds.Count > 0)
            {
                List<int> orphans;
                lock (unlinkedToolIds) orphans = unlinkedToolIds.ToList();
                await sessionService.LinkToolsByIdsAsync(session.Id.Value, orphans, finalMsgId);
            }
            await ZipSessionScriptsAsync(session.Id.Value);
        }
        try
        {
            var doneBytes = Encoding.UTF8.GetBytes("data: [DONE]\n\n");
            await outputStream.WriteAsync(doneBytes, ct);
            await outputStream.FlushAsync(ct);
        }
        catch { }
    }

    // <think> vazado no content (modelo/hermes): extrai p/ thinking e limpa o texto.

    // Prompt do subagente nos args (vazio = call fantasma, sem label).
    private static string SubPrompt(string argsJson)
    {
        try { return JsonNode.Parse(argsJson ?? "")?.AsObject()?["prompt"]?.GetValue<string>() ?? ""; }
        catch { return ""; }
    }

    // Throughput da bolha (t/s): tokens de saída / segundos de execução.
    private static double? TpsOf(int? completionTokens, long elapsedMs)
    {
        if (completionTokens is null || completionTokens <= 0 || elapsedMs <= 0) return null;
        return Math.Round(completionTokens.Value / (elapsedMs / 1000.0), 1);
    }

    // Estimativa p/ bolha de subagente (sem usage): chars/4 sobre os segundos.
    private static double? TpsEstimate(string text, long elapsedMs)
    {
        if (string.IsNullOrWhiteSpace(text) || elapsedMs <= 0) return null;
        return Math.Round((text.Trim().Length / 4.0) / (elapsedMs / 1000.0), 1);
    }

    // <think> vazado no content (modelo/hermes): extrai p/ thinking e limpa o texto.
    // Idem <thinking> (padrão DeepSeek): vai para o dropdown Raciocínio.
    private static string StripThinkTags(string content, System.Text.StringBuilder thinking)
    {
        if (string.IsNullOrEmpty(content)) return content;
        string s = content;
        if (s.Contains("<thinking>", StringComparison.Ordinal))
        {
            s = System.Text.RegularExpressions.Regex.Replace(s, "<thinking>([\\s\\S]*?)</thinking>",
                m => { thinking.Append(m.Groups[1].Value); return ""; });
            int oi = s.IndexOf("<thinking>", StringComparison.Ordinal);
            if (oi >= 0) { thinking.Append(s[(oi + 10)..]); s = s[..oi]; }
        }
        if (s.Contains("<think>", StringComparison.Ordinal))
        {
            s = System.Text.RegularExpressions.Regex.Replace(s, "<think>([\\s\\S]*?)</think>",
                m => { thinking.Append(m.Groups[1].Value); return ""; });
            int oi = s.IndexOf("<think>", StringComparison.Ordinal);
            if (oi >= 0) { thinking.Append(s[(oi + 7)..]); s = s[..oi]; }
        }
        return s.Replace("</think>", "", StringComparison.Ordinal);
    }

    // Evento de progresso ao cliente (a UI mostra "Executando [tool]..." / details com retorno).
    private static async Task WriteProgressAsync(System.IO.Stream output, string stage, string name, bool? ok, CancellationToken ct, string result = "", long ms = 0, string sub = "", string args = "")
    {
        var evt = new JsonObject { ["progress"] = stage, ["name"] = name };
        if (!string.IsNullOrEmpty(sub)) evt["sub"] = sub;
        if (ok.HasValue) evt["ok"] = ok.Value;
        if (ms > 0) evt["ms"] = ms;
        if (!string.IsNullOrEmpty(result)) evt["result"] = result;
        if (!string.IsNullOrEmpty(args)) evt["args"] = args.Length > 4000 ? args[..4000] : args;
        var bytes = Encoding.UTF8.GetBytes("data: " + evt.ToJsonString() + "\n\n");
        await output.WriteAsync(bytes, ct);
        await output.FlushAsync(ct);
    }

    // Chunk ao vivo do subagente (thinking/content) p/ card próprio na UI.
    private static readonly ConcurrentDictionary<string, int> SubChunkCounts = new();
    private static async Task WriteSubChunkAsync(System.IO.Stream output, string sub, string kind, string text, CancellationToken ct)
    {
        int n = SubChunkCounts.AddOrUpdate(sub + "/" + kind, 1, (_, v) => v + 1);
        if (n == 1 || n % 50 == 0)
            try { await System.IO.File.AppendAllTextAsync("/tmp/opencode/subchunks.log", $"{System.DateTime.Now:HH:mm:ss} {sub} {kind} n={n} len={text.Length}\n", ct); } catch { }
        var evt = new JsonObject { ["progress"] = "sub_chunk", ["sub"] = sub, ["kind"] = kind, ["text"] = text };
        var bytes = Encoding.UTF8.GetBytes("data: " + evt.ToJsonString() + "\n\n");
        await output.WriteAsync(bytes, ct);
        await output.FlushAsync(ct);
    }

    // POST /Chat/Tool - Executa tool no servidor
    [HttpPost("/Chat/Tool")]
    public async Task<IActionResult> ExecuteTool([FromBody] ToolRequest request)
    {
        var result = toolExecutor.Execute(request.Name, request.Arguments);
        return Json(new { success = result.Success, output = result.Output, error = result.Error });
    }

    public sealed class ToolRequest
    {
        public string Name { get; set; } = "";
        public string Arguments { get; set; } = "";
    }
}