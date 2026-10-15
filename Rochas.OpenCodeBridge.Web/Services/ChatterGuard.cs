// Corta-espiral de prosa curta sem tools (loop "boa noite/encerrando").
// O anti-giro só pega turnos de LEITURA; este pega turnos de CONVERSA:
// prosa curta repetida sem nenhuma tool executar. Uso no loop do
// ChatController: a cada fim de turno, Note(texto, nCalls); true = encerrar
// com nota honesta em vez de chamar o modelo de novo.
namespace Rochas.OpenCodeBridge.Web.Services;

public sealed class ChatterGuard
{
    private readonly int _shortLimit;
    private readonly int _maxShort;
    private int _short;
    private string? _prev;

    public ChatterGuard(int shortLimit = 120, int maxShort = 3)
    {
        _shortLimit = shortLimit;
        _maxShort = maxShort;
    }

    /// <summary>Registra fim de turno; true = espiral, deve encerrar.</summary>
    public bool Note(string? text, int tools)
    {
        string t = (text ?? "").Trim();
        // Tool executou ou texto substancial: conversa saudável, reseta.
        if (tools > 0 || t.Length > _shortLimit) { _short = 0; _prev = null; return false; }
        _short++;
        bool same = _prev is not null && string.Equals(_prev, t, StringComparison.Ordinal);
        _prev = t;
        // 3 curtos seguidos, ou o MESMO texto 2x (eco colado na bolha).
        return _short >= _maxShort || (same && _short >= 2);
    }

    public void Reset() { _short = 0; _prev = null; }

    public const string StopNote =
        "Parece que caímos numa espiral de saudações/despedidas sem executar nada. Me diga a tarefa de forma direta (arquivo + o que fazer) que eu executo.";
}
