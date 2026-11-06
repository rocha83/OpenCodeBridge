// Corta-espiral de prosa curta sem tools (loop "boa noite/encerrando").
// O anti-giro só pega turnos de LEITURA; este pega turnos de CONVERSA:
// prosa curta repetida sem nenhuma tool executar. Mora no Core (fonte única)
// e a Web UI apenas consome: Note(texto, nCalls); true = encerrar com nota
// honesta em vez de chamar o modelo de novo.
namespace Rochas.OpenCodeBridge.Core;

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

    /// <summary>
    /// Nota de encerramento quando a prosa curta repetiu DEPOIS de tools/subs rodarem:
    /// o texto de <see cref="StopNote"/> ("sem executar nada") mentia nesse caso —
    /// medido na Web UI em 2026-10-30 (sessões 27/28): o subagente gravou
    /// <c>Program.cs</c> e a bolha final dizia que nada tinha sido executado.
    /// </summary>
    public const string StopNoteAfterWork =
        "O modelo repetiu prosa curta sem avançar depois de executar as ferramentas acima (os cards ficam na sessão). Reenvie o próximo passo com arquivo e o que fazer.";

    /// <summary>
    /// Nota honesta conforme o turno executou algo: sem execução é espiral de conversa;
    /// com execução é declaração repetida sobre trabalho já feito.
    /// </summary>
    /// <param name="anyToolRan">True quando alguma tool/subagente rodou no request.</param>
    /// <returns>Texto da bolha final.</returns>
    public static string StopNoteFor(bool anyToolRan) => anyToolRan ? StopNoteAfterWork : StopNote;
}
