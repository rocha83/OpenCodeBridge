using System.Text;

namespace Rochas.OpenCodeBridge.Core;

/// <summary>
/// Classe-mae do escopo de CONTEUDO: contem o algoritmo de repeticao de texto
/// (normalizacao, similaridade de blocos e cauda) e a decisao <see cref="IsRepeat"/>.
/// </summary>
/// <remarks>
/// A separacao e por RESPONSABILIDADE: texto repetido (thinking, content, args de
/// tool em stream) passa por este escopo; ciclo de tool passa por
/// <see cref="ToolsAntiRepeat"/>. Os helpers abaixo sao finos: so declaram ONDE a
/// cauda e medida e herdam o trabalho daqui.
/// </remarks>
public abstract class ContentAntiRepeat
{
    /// <summary>Menor janela aceita: abaixo disso a comparacao vira ruido.</summary>
    public const int MinWindow = 32;

    /// <summary>Similaridade minima entre blocos consecutivos (loop curto com variacao).</summary>
    public const double BlockSimilarity = 0.85;

    /// <summary>
    /// Janela padrao do anti-repeat de conteudo, em caracteres normalizados.
    /// E a MESMA da bridge (`Program.AntiRepeatWindow`, faixa 64..2048, ajustavel
    /// por `--anti-repeat-window`/`antiRepeatWindow`): a bridge manda, o Core
    /// espelha o default e a Web consome daqui — nunca um literal proprio.
    /// </summary>
    public const int DefaultWindow = 256;

    /// <summary>Faixa aceita para a janela no deployment (`--anti-repeat-window`).</summary>
    public const int WindowMin = 64;

    /// <summary>Teto da janela no deployment (`--anti-repeat-window`).</summary>
    public const int WindowMax = 2048;

    /// <summary>
    /// Aviso de contencao FORTE, injetado no retry do caminho Web (ChatController):
    /// pede para encerrar em silencio — a Web ja mostrou texto demais.
    /// </summary>
    public const string Notice =
        "Aqui e a ponte de contencao OpenCodeBridge: pare de enviar o mesmo raciocinio, " +
        "provavelmente voce entrou em repeticao invalida; quando informada a repeticao, " +
        "nao responda mais nada, apenas encerre em silencio, obrigado.";

    /// <summary>
    /// Aviso BRANDO do retry da bridge (`--anti-repeat-notice`): avisa a possivel
    /// repeticao e deixa o modelo reformular ou continuar se for intencional.
    /// Fonte unica: o host so sobrescreve o texto, nunca redefine o default.
    /// </summary>
    public const string SoftNotice =
        "A ponte OpenCodeBridge percebeu uma possivel repeticao nesta resposta. " +
        "Se fizer sentido, vale reformular o raciocinio ou seguir por outro caminho; " +
        "se a repeticao for intencional, pode continuar. Obrigado.";

    /// <summary>Tamanho da cauda comparada, em caracteres normalizados.</summary>
    public int Window { get; }

    /// <summary>Onde este detector atua (vai para log/sonda).</summary>
    public abstract string Scope { get; }

    /// <summary>Cria o detector com a janela configurada (nunca menor que <see cref="MinWindow"/>).</summary>
    /// <param name="window">Janela em caracteres.</param>
    protected ContentAntiRepeat(int window) => Window = Math.Max(MinWindow, window);

    /// <summary>O texto acumulado entrou em repeticao? (regra unica da classe-mae)</summary>
    /// <param name="text">Texto acumulado ate aqui.</param>
    /// <returns>True = repeticao invalida provavel.</returns>
    public virtual bool IsRepeat(string? text) => TailRepeats(text, Window);

    /// <summary>Normaliza texto para comparacao: minusculo, espacos colapsados e trim.</summary>
    /// <param name="s">Texto cru.</param>
    /// <returns>Texto canonico (vazio quando a entrada e vazia).</returns>
    public static string Normalize(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var b = new StringBuilder(s.Length);
        bool space = false;
        foreach (char c in s)
        {
            if (char.IsWhiteSpace(c))
            {
                if (!space) { b.Append(' '); space = true; }
            }
            else
            {
                b.Append(char.ToLowerInvariant(c));
                space = false;
            }
        }
        return b.ToString().Trim();
    }

    /// <summary>Razao de caracteres iguais entre dois blocos de mesmo tamanho.</summary>
    /// <param name="a">Bloco A.</param>
    /// <param name="b">Bloco B.</param>
    /// <returns>0..1 (0 quando algum bloco e vazio).</returns>
    public static double Similarity(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        if (a.Length == 0 || b.Length == 0) return 0;
        int len = Math.Min(a.Length, b.Length);
        int same = 0;
        for (int i = 0; i < len; i++)
            if (a[i] == b[i]) same++;
        return (double)same / len;
    }

    /// <summary>
    /// Regra unica de repeticao: cauda verbatim ja vista ou 3 blocos finais
    /// consecutivos similares (loop curto com variacao, ex.: "Ola!").
    /// </summary>
    /// <param name="accumulated">Texto acumulado.</param>
    /// <param name="window">Janela comparada (chars normalizados).</param>
    /// <returns>True = repeticao detectada.</returns>
    public static bool TailRepeats(string? accumulated, int window)
    {
        if (string.IsNullOrEmpty(accumulated) || window < MinWindow) return false;
        string norm = Normalize(accumulated);
        // Cauda verbatim (loops longos exatos).
        if (norm.Length >= 2 * window)
        {
            string tail = norm[^window..];
            if (norm.AsSpan(0, norm.Length - window).IndexOf(tail.AsSpan(), StringComparison.Ordinal) >= 0)
                return true;
        }
        // Loop curto com variacao: 3 blocos finais consecutivos parecidos.
        foreach (int unit in new[] { 24, 32, 40, 48, 64, 80, 96, 128 })
        {
            if (norm.Length < 3 * unit) continue;
            var a = norm.AsSpan(norm.Length - unit);
            var b = norm.AsSpan(norm.Length - 2 * unit, unit);
            var c = norm.AsSpan(norm.Length - 3 * unit, unit);
            if (Similarity(a, b) >= BlockSimilarity && Similarity(b, c) >= BlockSimilarity) return true;
        }
        return false;
    }

    /// <summary>Posicao (no texto normalizado) onde a cauda repetida comecou, para a sonda.</summary>
    /// <param name="accumulated">Texto acumulado.</param>
    /// <param name="window">Janela comparada.</param>
    /// <returns>Indice da primeira ocorrencia da cauda, ou -1.</returns>
    public static int TailDuplicateAt(string? accumulated, int window)
    {
        string norm = Normalize(accumulated);
        int w = Math.Min(window, norm.Length);
        if (w <= 0 || norm.Length < 2 * w) return -1;
        return norm[..^w].IndexOf(norm[^w..], StringComparison.Ordinal);
    }
}

/// <summary>
/// Helper fino de conteudo: detector padrao de texto (herda toda a regra da
/// classe-mae; so fixa o escopo).
/// </summary>
public class AntiRepeatReply : ContentAntiRepeat
{
    /// <summary>Cria o detector de texto.</summary>
    /// <param name="window">Janela em caracteres.</param>
    public AntiRepeatReply(int window) : base(window) { }

    /// <inheritdoc />
    public override string Scope => "content";
}

/// <summary>Helper fino de conteudo no STREAM: cauda dos deltas (thinking/content/SSE).</summary>
public sealed class StreamAntiRepeatReply : AntiRepeatReply
{
    /// <summary>Cria o detector do stream.</summary>
    /// <param name="window">Janela em caracteres (64..2048 na CLI).</param>
    public StreamAntiRepeatReply(int window) : base(window) { }

    /// <inheritdoc />
    public override string Scope => "stream";
}

/// <summary>Helper fino de conteudo nos TOOL-ARGS: cauda dos argumentos de uma chamada.</summary>
public sealed class ToolArgsAntiRepeatReply : AntiRepeatReply
{
    /// <summary>Tamanho minimo dos args para valer a pena sondar (write/edit
    /// carrega o arquivo inteiro; arquivo pequeno tambem entra em loop).</summary>
    public const int ProbeMinChars = 256;

    /// <summary>
    /// Janela PADRAO dos argumentos, em caracteres normalizados. E' maior que a
    /// do conteudo de proposito: `write`/`edit` carrega arquivo inteiro e trechos
    /// legitimos se repetem (mesmo bloco de codigo, migration com 30 casos
    /// iguais). Com a janela do texto o detector cortava a geracao no MEIO dos
    /// args e o salvage recusava a call — medido no L3 de 2026-10-29
    /// (`anti-repeat where=stream-abort window=512 chars=2050` seguido de
    /// `salvage-args-rejected tools=edit`).
    /// </summary>
    public const int ArgsDefaultWindow = 1024;

    /// <summary>Menor janela aceita para args (abaixo da janela de conteudo nao separa nada).</summary>
    public const int ArgsWindowMin = 256;

    /// <summary>Teto da janela de args no deployment.</summary>
    public const int ArgsWindowMax = 8192;

    /// <summary>Cria o detector de argumentos.</summary>
    /// <param name="window">Janela em caracteres.</param>
    public ToolArgsAntiRepeatReply(int window) : base(window) { }

    /// <inheritdoc />
    public override string Scope => "tool-args";
}
