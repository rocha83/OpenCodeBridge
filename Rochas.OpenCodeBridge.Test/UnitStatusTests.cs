using System.Text.Json.Nodes;

namespace Rochas.OpenCodeBridge.Test;

/// <summary>Unit de saúde da bridge (status, métricas, passthrough de modelos).</summary>
internal static class UnitStatusTests
{
    public static async Task Status()
    {
        var s = await TestContext.GetObj(TestContext.Bridge + "/api/status");
        TestContext.Check(s["status"]?.GetValue<string>() == "ok", "status ok");
        TestContext.Check(!string.IsNullOrEmpty(s["model"]?.GetValue<string>()), "model presente");
        TestContext.Check(s["upstream"]?.GetValue<string>() == TestContext.Llama, "upstream == llama");
    }

    public static async Task Metrics()
    {
        var m = await TestContext.GetObj(TestContext.Bridge + "/api/metrics");
        TestContext.Check(m["requests"] is not null, "requests presente");
        TestContext.Check(m["tps_output"] is not null && m["tps_total"] is not null, "tps presentes");
    }

    /// <summary>
    /// Reset dos diagnosticos (POST /api/diag/reset): zera os contadores
    /// cumulativos sem reiniciar a bridge — e' o que permite o painel medir o
    /// step atual. Depois do reset, model/tokens voltam a zero e o HTTP segue.
    /// </summary>
    public static async Task DiagReset()
    {
        using var post = new HttpRequestMessage(HttpMethod.Post,
            TestContext.Bridge + "/api/diag/reset?probe=1&files=1")
        { Content = new StringContent("") };
        using var res = await TestContext.Http.SendAsync(post);
        string body = await res.Content.ReadAsStringAsync();
        TestContext.Check(res.IsSuccessStatusCode, $"reset responde {(int)res.StatusCode}: {body.Trim()}");
        var ack = JsonNode.Parse(body)?.AsObject();
        TestContext.Check(ack?["ok"]?.GetValue<bool>() == true, "reset devolve ok");
        TestContext.Check(ack?["event"]?.GetValue<string>() == "diag-reset", "reset devolve evento diag-reset");
        var m = await TestContext.GetObj(TestContext.Bridge + "/api/metrics");
        long modelReqs = m["model_requests"]?.GetValue<long>() ?? -1;
        long tokens = m["input_tokens"]?.GetValue<long>() ?? -1;
        TestContext.Check(modelReqs == 0, $"model_requests zerado depois do reset, veio {modelReqs}");
        TestContext.Check(tokens == 0, $"input_tokens zerado depois do reset, veio {tokens}");
    }

    /// <summary>
    /// Reset do painel (?panel=1): o enunciado publicado, o historico e a lista
    /// de steps somem no reset de bateria — sem apagar o enunciado no reset por
    /// step (que roda no <c>step_start</c>).
    /// </summary>
    public static async Task DiagResetPanel()
    {
        string enunciado = TestContext.Bridge + "/api/test/enunciado";
        async Task<JsonNode?> Get(string url)
        {
            using var res = await TestContext.Http.GetAsync(url);
            string raw = await res.Content.ReadAsStringAsync();
            TestContext.Check(res.IsSuccessStatusCode, $"GET {url.Split('/').Last()} -> {(int)res.StatusCode}");
            return JsonNode.Parse(raw);
        }
        async Task<JsonObject> Publish(string step, string prompt)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, enunciado)
            { Content = new StringContent(new JsonObject { ["step"] = step, ["prompt"] = prompt }.ToJsonString(),
                System.Text.Encoding.UTF8, "application/json") };
            using var res = await TestContext.Http.SendAsync(req);
            TestContext.Check(res.IsSuccessStatusCode, $"publica enunciado ({(int)res.StatusCode})");
            return (await Get(enunciado))!.AsObject();
        }

        var snap = await Publish("U-panel-probe", "ENUNCIADO DO PAINEL");
        TestContext.Check(snap["step"]?.GetValue<string>() == "U-panel-probe", "enunciado publicado no painel");
        TestContext.Check((snap["messages"]?.AsArray().Count ?? 0) >= 1, "historico do painel tem a mensagem inicial");

        using (var stepReq = new HttpRequestMessage(HttpMethod.Post, TestContext.Bridge + "/api/test/step")
        {
            Content = new StringContent(new JsonObject
            { ["step"] = "U-panel-probe", ["verdict"] = "PASS", ["mode"] = "bridge", ["secs"] = 1 }.ToJsonString(),
                System.Text.Encoding.UTF8, "application/json")
        })
        {
            using var stepRes = await TestContext.Http.SendAsync(stepReq);
            TestContext.Check(stepRes.IsSuccessStatusCode, $"/api/test/step aceita a linha ({(int)stepRes.StatusCode})");
        }
        var steps = await Get(TestContext.Bridge + "/api/test/steps?war=1");
        // Dentro da bateria o painel JA tem a linha RUN do step corrente (o driver
        // publica step_start), entao o contrato e' ESTRUTURAL: o step publicado
        // aparece na lista e toda linha listada tem `mode` (o filtro war=1).
        // Contar ==1 era verdade so' fora de um run e derrubou o L1 de 2026-10-30.
        var listed = steps!.AsArray();
        TestContext.Check(listed.Any(x => x?["step"]?.GetValue<string>() == "U-panel-probe"),
            $"painel lista o step publicado (linhas: {string.Join(",", listed.Select(x => x?["step"]?.GetValue<string>()))})");
        TestContext.Check(listed.All(x => !string.IsNullOrWhiteSpace(x?["mode"]?.GetValue<string>())),
            "toda linha de war=1 tem mode (filtro do painel)");

        // Reset por step (sem panel): nada do painel pode ser apagado no meio do run.
        using (var keep = new HttpRequestMessage(HttpMethod.Post, TestContext.Bridge + "/api/diag/reset?files=1")
        { Content = new StringContent("") })
        {
            using var keepRes = await TestContext.Http.SendAsync(keep);
            TestContext.Check(keepRes.IsSuccessStatusCode, $"reset por step responde {(int)keepRes.StatusCode}");
        }
        var kept = (await Get(enunciado))!.AsObject();
        TestContext.Check(kept["step"]?.GetValue<string>() == "U-panel-probe",
            "reset sem panel preserva o enunciado (step_start nao apaga)");
        TestContext.Check((kept["messages"]?.AsArray().Count ?? 0) >= 1, "reset sem panel preserva o historico");

        // Reset de bateria (com panel): painel limpo.
        string body;
        using (var clear = new HttpRequestMessage(HttpMethod.Post,
            TestContext.Bridge + "/api/diag/reset?files=1&panel=1") { Content = new StringContent("") })
        {
            using var clearRes = await TestContext.Http.SendAsync(clear);
            body = await clearRes.Content.ReadAsStringAsync();
            TestContext.Check(clearRes.IsSuccessStatusCode, $"reset de bateria responde {(int)clearRes.StatusCode}: {body.Trim()}");
        }
        TestContext.Check(JsonNode.Parse(body)?["panel"]?.GetValue<bool>() == true, "reset devolve panel=true");
        var cleared = (await Get(enunciado))!.AsObject();
        TestContext.Check(cleared["step"]?.GetValue<string>() == "n/d", $"painel zerado no step, veio {cleared["step"]}");
        TestContext.Check((cleared["messages"]?.AsArray().Count ?? -1) == 0, "historico do painel zerado");
        var stepsAfter = await Get(TestContext.Bridge + "/api/test/steps?war=1");
        TestContext.Check(stepsAfter!.AsArray().Count == 0, "lista de steps zerada");

        // Depois do reset o painel volta a aceitar publicacao (proximo step do run).
        var again = await Publish("U-panel-probe-2", "SEGUNDO RUN");
        TestContext.Check(again["step"]?.GetValue<string>() == "U-panel-probe-2", "painel volta a publicar depois do reset");
    }

    public static async Task ModelsPassthrough()
    {
        var models = await TestContext.GetObj(TestContext.Bridge + "/v1/models");
        var first = models["data"]?[0]?["id"]?.GetValue<string>();
        TestContext.Check(first == TestContext.ServedModel, "passthrough lista servido, veio: " + first);
    }
}
