namespace Rochas.OpenCodeBridge.Core;

/// <summary>Veredito da contencao do loop agente.</summary>
public enum TurnLoopVerdict
{
    /// <summary>Nada a fazer.</summary>
    None,

    /// <summary>Injetar aviso no contexto e repetir o turno (correcao).</summary>
    Nudge,

    /// <summary>Encerrar o turno com nota honesta.</summary>
    Trip
}

/// <summary>Decisao do guard com o texto canonico da intervecao.</summary>
/// <param name="Verdict">Acao a tomar.</param>
/// <param name="Notice">Texto a injetar (vazio quando <see cref="TurnLoopVerdict.None"/>).</param>
/// <param name="Cause">Rotulo do sinal para log/metrica.</param>
public sealed record TurnLoopDecision(TurnLoopVerdict Verdict, string Notice, string Cause);

/// <summary>
/// Contencao do loop agente: sinais de giro (leitura sem producao), chamada
/// identica repetida (ping-pong) e espiral de prosa. Estado por request; a
/// maquina e stateless entre requests, entao o chamador guarda uma instancia
/// durante o stream. Reune o que estava duplicado nos loops chat/Responses.
/// </summary>
/// <remarks>
/// Nao reimplementa <see cref="ChatterGuard"/> nem <see cref="ToolSig"/>: so
/// guarda contadores e compoe os sinais. Os textos estao em <see cref="LoopNudges"/>.
/// </remarks>
public sealed class TurnLoopGuard
{
    /// <summary>Ocorrencias do mesmo sinal que disparam a correcao.</summary>
    public const int RepeatAt = 3;

    /// <summary>Correcoes automaticas antes do encerramento.</summary>
    public const int MaxFixes = 2;

    /// <summary>Turnos de prosa apos execucao antes do encerramento.</summary>
    public const int ProseCap = 2;

    private readonly ChatterGuard _chatter = new();
    private int _fixes;
    private int _idleReadOnly;
    private int _turnStreak;
    private int _callStreak;
    private int _prosaTurns;
    private string _lastTurnSig = "";
    private string _lastCallSig = "";

    /// <summary>Sinal de giro do loop chat: assinatura do turno e leitura sem texto.</summary>
    /// <param name="turnSignature">Assinatura canonica das calls do turno.</param>
    /// <param name="readOnlyNoText">Turno so-leitura e sem texto do modelo.</param>
    /// <returns>Decisao do guard.</returns>
    public TurnLoopDecision ObserveTurn(string turnSignature, bool readOnlyNoText)
    {
        if (turnSignature == _lastTurnSig) _turnStreak++;
        else { _turnStreak = 1; _lastTurnSig = turnSignature; }
        _idleReadOnly = readOnlyNoText ? _idleReadOnly + 1 : 0;

        if (_turnStreak < RepeatAt && _idleReadOnly < RepeatAt)
            return new TurnLoopDecision(TurnLoopVerdict.None, "", "");
        if (_fixes >= MaxFixes)
            return new TurnLoopDecision(TurnLoopVerdict.Trip, LoopNudges.AntiGiroStop, "anti-giro");
        _fixes++;
        // Zera os sinais que dispararam, como no comportamento medido do loop chat.
        _turnStreak = 0;
        _idleReadOnly = 0;
        return new TurnLoopDecision(TurnLoopVerdict.Nudge, LoopNudges.AntiGiro(_fixes), "anti-giro");
    }

    /// <summary>Sinal de chamada identica do loop Responses (ping-pong).</summary>
    /// <param name="name">Nome da tool.</param>
    /// <param name="args">Argumentos crus da chamada.</param>
    /// <returns>Decisao do guard.</returns>
    public TurnLoopDecision ObserveCall(string name, string args)
    {
        string sig = ToolSig.Of(name, args);
        if (sig == _lastCallSig) _callStreak++;
        else { _callStreak = 1; _lastCallSig = sig; }
        if (_callStreak < RepeatAt)
            return new TurnLoopDecision(TurnLoopVerdict.None, "", "");
        if (_fixes >= MaxFixes)
            return new TurnLoopDecision(TurnLoopVerdict.Trip, "", "anti-toolloop");
        _fixes++;
        return new TurnLoopDecision(TurnLoopVerdict.Nudge, LoopNudges.SameCall(name, args), "anti-toolloop");
    }

    /// <summary>Espiral de prosa apos execucao: usa o <see cref="ChatterGuard"/> do Core.</summary>
    /// <param name="turnText">Texto do turno sem tools.</param>
    /// <param name="anyToolRan">Se alguma tool ja foi executada no request.</param>
    /// <returns>Trip quando a prosa deve ser cortada.</returns>
    public TurnLoopDecision ObserveProse(string turnText, bool anyToolRan)
    {
        if (!anyToolRan)
            return new TurnLoopDecision(TurnLoopVerdict.Trip, "", "turno-sem-tool");
        _prosaTurns++;
        if (!_chatter.Note(turnText, 0) && _prosaTurns < ProseCap)
            return new TurnLoopDecision(TurnLoopVerdict.None, "", "");
        return new TurnLoopDecision(TurnLoopVerdict.Trip, "", "prosa");
    }

    /// <summary>Zera o teto de prosa apos uma tool executar com sucesso.</summary>
    public void ResetProse() => _prosaTurns = 0;
}
