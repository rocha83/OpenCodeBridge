using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Rochas.AI.Manager.Application.Services;

public sealed record PermissionResult(string Action, string? Reason, string? Role, string? Tool, string? Notice);

public sealed class PendingRisk(string tool, string args, string role)
{
    public string Tool { get; } = tool;
    public string Args { get; } = args;
    public string Role { get; } = role;
    public DateTime CreatedAt { get; } = DateTime.UtcNow;
}

public sealed class PermissionService
{
    private readonly ConcurrentDictionary<string, PendingRisk> _pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _blacklist = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<Regex>> _riskAlerts = new(StringComparer.OrdinalIgnoreCase);

    public PermissionService(IDictionary<string, string[]>? blacklist = null, IDictionary<string, string[]>? riskAlerts = null)
    {
        if (blacklist is not null)
            foreach (var kv in blacklist)
                _blacklist[kv.Key] = new HashSet<string>(kv.Value, StringComparer.OrdinalIgnoreCase);
        if (riskAlerts is not null)
            foreach (var kv in riskAlerts)
            {
                var list = new List<Regex>();
                foreach (var pattern in kv.Value)
                    try { list.Add(new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled)); } catch { }
                if (list.Count > 0) _riskAlerts[kv.Key] = list;
            }
    }

    public static string RoleOfModel(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return string.Empty;
        var parts = modelId.Split('-', StringSplitOptions.RemoveEmptyEntries);
        // last token often role suffix (plan/build/orch/moe/scripter/exec)
        return parts.Length > 0 ? parts[^1] : string.Empty;
    }

    public PermissionResult PermissionOf(string? modelId, string tool, string? args)
    {
        var role = RoleOfModel(modelId);
        var normArgs = args ?? string.Empty;
        // blacklist: tool proibida para o role => ask (papel)
        if (_blacklist.TryGetValue(role, out var banned) && banned.Contains(tool))
            return new PermissionResult("ask", "papel", role, tool, $"Tool '{tool}' fora do papel {role}.");

        // risk alerts: regex sobre nome+args
        if (_riskAlerts.TryGetValue(role, out var patterns))
        {
            var haystack = $"{tool} {normArgs}";
            foreach (var rx in patterns)
                if (rx.IsMatch(haystack))
                    return new PermissionResult("ask", "risco", role, tool, $"Comando de risco detectado: {tool}.");
        }

        return new PermissionResult("allow", null, role, tool, null);
    }

    public void RegisterPending(string callId, string tool, string args, string role)
        => _pending[callId] = new PendingRisk(tool, args, role);

    public bool TryRemovePending(string callId, out PendingRisk? risk)
        => _pending.TryRemove(callId, out risk);

    public IEnumerable<string> ScanAnswers(JsonElement input)
    {
        var notices = new List<string>();
        if (input.ValueKind != JsonValueKind.Array) return notices;
        foreach (var item in input.EnumerateArray())
        {
            if (!item.TryGetProperty("call_id", out var cid) || !item.TryGetProperty("output", out var outp)) continue;
            var callId = cid.GetString();
            if (string.IsNullOrWhiteSpace(callId)) continue;
            if (!_pending.TryRemove(callId, out var risk) || risk is null) continue;

            var answer = outp.GetString() ?? string.Empty;
            var approved = answer.StartsWith("sim", StringComparison.OrdinalIgnoreCase)
                        || answer.StartsWith("s", StringComparison.OrdinalIgnoreCase)
                        || answer.Contains("autorizar", StringComparison.OrdinalIgnoreCase);
            if (approved)
                notices.Add($"risk-approved: {risk.Tool} ({risk.Args})");
            else
                notices.Add($"risk-refused: {risk.Tool} ({risk.Args})");
        }
        return notices;
    }
}
