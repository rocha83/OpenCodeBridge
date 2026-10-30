namespace Rochas.OpenCodeBridge.Core;

/// <summary>
/// Caudas de inducao do harness. Os textos abaixo sao fallback do codigo; o
/// deployment ajusta em appsettings (harnessBehaviorTail/harnessToolsTail) ou
/// CLI (--harness-behavior-tail/--harness-tools-tail). O nucleo base
/// (MainHarness) e hardcoded e nao passa por aqui.
/// </summary>
public static class HarnessTails
{
    /// <summary>
    /// Delta de comportamento da bridge (o nucleo ja mora no MainHarness).
    /// Mantido curto de proposito: so o que e especifico deste canal/workspace.
    /// </summary>
    public const string DefaultBehavior =
        "COMPORTAMENTO (delta da bridge; a base ja cobre postura, caminhos e erros): " +
        "Formato: em conversa use Unicode (setas, <=, pi, somatorio) em vez de LaTeX; use $...$ so em bloco de codigo ou formula, onde o cliente renderiza. " +
        "TOOLS PRIMEIRO: em enunciado que pede execucao, a PRIMEIRA saida e a tool call; ZERO linha de prosa antes do primeiro disparo e nenhum texto entre uma call e a proxima. " +
        "Excecao: no maximo 1 linha entre calls dependentes, so para indicar o proximo passo. " +
        "So escreva ao usuario depois da ultima acao, com evidencia real (trecho de log, saida de comando). " +
        "PERSISTENCIA: cada resultado de tool e insumo da proxima call no MESMO turno; nao espere 'siga' e nao encerre com 'vou fazer', 'iniciando' ou confirmacao. " +
        "PRIORIDADE: se o master mandar explicar antes de comando nao trivial, execute primeiro e mostre a saida; a explicacao, se necessaria, vem depois, em 1 linha.";

    /// <summary>
    /// Variante do delta para pedido SEM tools. A cauda de tools e a linha
    /// "TOOLS PRIMEIRO" empurram o modelo a disparar tool call inexistente
    /// (ele devolve o JSON da chamada como TEXTO e trunca a resposta). Em
    /// pedido de texto/plano a cauda vira so formato.
    /// </summary>
    public const string DefaultBehaviorNoTools =
        "COMPORTAMENTO (delta da bridge; a base ja cobre postura, caminhos e erros): " +
        "Formato: em conversa use Unicode (setas, <=, pi, somatorio) em vez de LaTeX; use $...$ so em bloco de codigo ou formula, onde o cliente renderiza. " +
        "PEDIDO SEM TOOLS: responda em TEXTO com o CODIGO/PRODUTO COMPLETO pedido (no corpo da mensagem). " +
        "NAO entregue 'plano de implementacao', 'plano arquitetural', 'estrutura do projeto', sumario nem indice de passos: o texto deve ser o entregavel final. " +
        "NAO elida codigo: proibido '// ...', '/* ... */', 'resto igual' ou comentario no lugar do corpo — escreva cada metodo/trecho por inteiro. " +
        "Quando o pedido terminar com 'ENTREGUE O ARTEFATO COMPLETO EM TEXTO, SEM TOOL CALLS' (ou 'MOSTRE-ME COMO MODO PLANO EM TEXTO'), " +
        "entregue o ARTEFATO FINAL no corpo; NAO responda com documento de plano, 'plano de implementacao' nem 'aguardando confirmacao'.";

    public const string DefaultTools =
        "MODO DE USO DAS TOOLS (regras transversais; o contrato de cada tool vai no schema): " +
        "Prefira a tool dedicada: read/write/edit para arquivo, glob/grep para descobrir, webfetch/websearch para rede, shell para executar. " +
        "Um passo = uma acao; argumentos JSON completos; leia o resultado antes de repetir. " +
        "JSON DO ARGS: o conteudo de `write`/`edit` e' string JSON — quebra de linha = `\\n` (barra + n, dois caracteres) e aspas = `\\\"`; nunca quebre a linha crua dentro da string (o parser recusa). " +
        "EDITAR ACRESCENTANDO: o `oldString` e' o trecho que CONTINUA no arquivo; o `newString` traz esse mesmo trecho + o texto novo, para o arquivo terminar com os dois. " +
        "ARQUIVO GRANDE: grave um `write` curto e complete com `edit` (args menor = menos risco de truncar). " +
        "Independentes no mesmo turno; dependentes esperam. Nao repita a mesma chamada/caminho; em loop, mude a abordagem. " +
        "`execute` nao e sandbox: a bridge desembrulha tools.X; bloqueio de role nao se contorna por shell. " +
        "DOTNET: com mais de um projeto na pasta, `--project <pasta>` e ambiguo: aponte o ARQUIVO (`<dir>/<nome>.fsproj` ou `.csproj`). " +
        "ARQUIVO: pedido que manda criar/gravar arquivo entrega via `write` (conteudo completo); devolver o codigo em prosa no chat NAO conclui o pedido. " +
        "PROJETO EXISTENTE: edite os arquivos do projeto; `dotnet new` so em pasta vazia (segundo .csproj/.fsproj na mesma pasta quebra `dotnet run --project <pasta>`). " +
        "FLUXO ARQUIVO+REPO (exemplo; use os nomes do seu pedido): `mkdir` da pasta → `write` com o path da pasta (`<pasta>/<arquivo>`) → `git init`/`add`/`commit` com o mesmo `path` → `git log` e a saida colada no historico (passo a passo comentado no help da tool git). " +
        "PROVA: depois de `write`/`edit`, `read` o arquivo — o conteudo dele e' o texto que a tool devolveu; historico de comando e' a saida literal colada (ex.: `git log`, com os hashes que vieram). " +
        "SUBAGENTE: o schema de cada tool viaja no array `tools` do request; preserve o array e o system do role ao delegar; nao troque schema por prosa.";

    /// <summary>
    /// Mini tutorial F#: injetado apenas na fase F# (nao entra no DefaultTools,
    /// para nao impor sintaxe F# em requests C#/Orch).
    /// </summary>
    public const string FsMini =
        "F# MINI (sintaxe geral; os exemplos NAO sao o algoritmo do pedido). " +
        "BASE: cada diretiva na propria linha — a 1a linha tem so' `module Program` e o `open` vai na linha de baixo (`open System`); funcao `let f x = x + 1`; saida inteira `printfn \"%d\" v`; entrada `[<EntryPoint>] let main argv = ... 0`. " +
        "LISTA e ARRAY: `[ 1; 2; 3 ]` e' lista e combina com `List.sum`, `List.map (fun x -> x * x)`, `List.filter (fun x -> x > 0)`, `List.fold (fun acc x -> acc + x) 0`, `List.length`, `List.forall`, `List.exists`; `[| 1; 2; 3 |]` e' array de valores fixos e combina com `Array.sum`, `Array.map`, `Array.length`. " +
        "RANGE: `a .. b`; com passo, o PASSO e' o SEGUNDO termo. " +
        "INTEIROS: `%` e' resto; `/` entre dois int trunca. " +
        "IGUALDADE: `=` compara dois valores e `<>` testa a diferenca. " +
        "LAYOUT: o recuo define o bloco; cada expressao do corpo fica alinhada sob o `=`. " +
        "RETORNO: o valor da funcao e' a ULTIMA expressao do corpo (`printfn` so' imprime, nao devolve). " +        "RECURSAO: `let rec f n = if n <= 1 then 1 else n * f (n - 1)`. " +
        "AUXILIAR: vive no NIVEL DO MODULO, antes de quem a chama, e recebe por parametro o que usa de fora: `let isPrime n = n > 1 && List.forall (fun d -> n % d <> 0) [2 .. int (Math.Sqrt(float n))]`; no ramo `if/else` vai so' a chamada. " +
        "PRIMOS: divisores candidatos de 2 ate a raiz; o intervalo de ENTRADA pode comecar em 1. " +
        "FATORIAL/PRODUTO: `List.fold (fun acc x -> acc * x) 1 [1..10]`, sem recursao. " +
        "TABELA DE ERROS (codigo: sintoma -> forma correta): " +
        "FS0010 lista -> elemento com `-> expr` ou `do yield expr`: `[for i in 1 .. n-1 -> expr]` ou `[for i in 1 .. n-1 do yield expr]`; lambda e' `fun x -> expr`: `List.fold (fun acc x -> acc + x) 0 xs`; anotacao de retorno leva SO' o tipo, sem `nome:` dentro dos parenteses: `let hanoi (n: int) : int * string list =`. " +
        "FS0751/FS0003 passo -> passo no MEIO dos dois `..`: `[for i in 1 .. 2 .. n-1]` percorre 1, 3, .., n-1; aplicacao -> a chamada e a declaracao usam a MESMA quantidade de parametros: `let rec soma a b c = ...` chamado `soma 1 2 3`. " +
        "FS0001 tipo -> lista com `List.sum [ 1; 2 ]` e array com `Array.sum [| 1; 2 |]`; no range os dois limites sao do MESMO tipo; nos dois ramos do `if` o tipo e' o mesmo (`@` concatena LISTA, array usa `Array.append a b`). " +
        "FS0039 nome nao definido -> parametro ou valor ja declarado; a declaracao vem ANTES do primeiro uso (o modulo e' lido de cima para baixo): `let auxiliar = 3` acima de quem o usa, ou receba por parametro; resto de inteiro e' `a % b`; auto-chamada pede `let rec f`. " +
        "FS0037 nome repetido -> uma definicao por nome. " +
        "FS0058 offside -> auxiliar no nivel do modulo; no ramo, so' a chamada. " +
        "FS0988 modulo sem entrada -> a entrada da BASE (`[<EntryPoint>]`). " +
        "FS3118/FS0588/FS0528 corpo -> toda funcao termina numa expressao, e a ultima linha do corpo e' o valor: `let total xs =` / `let dobro = List.map (fun x -> x * 2) xs` / `List.sum dobro` (as linhas do corpo alinhadas sob o `=`); quando o `else` abre ramo com `let`, o `let` vai na linha de baixo, indentado. " +
        "FS0603 array -> a comprehension de array envolve TUDO entre `[|` e `|]`: `[| for i in 1 .. 3 -> i * i |]`; lista de elementos leva `;` e tupla entre parenteses: `[ (1, 2); (1, 3) ]`. " +
        "FS0597 argumento -> quando o argumento e' chamada, pipe ou composicao, ele vai entre parenteses: `printfn \"%s\" (String.concat \",\" movs)`. " +
        "FS0072 tipo -> quando o tipo do parametro nao sai sozinho, anote no lambda: `List.map (fun (a: int, b: int) -> sprintf \"%d->%d\" a b) movs` (ou use `fst`/`snd`). " +
        "FS0193 funcao -> `@` e `List.append` exigem os DOIS operandos: `a @ b` ou `List.append a b` (no pipe, o segundo vem explicito: `x |> List.append ys`). " +
        "FS0020 resultado descartado -> `|> ignore` ou `printfn \"%d\" v`. " +
        "FS3563 identificador invalido -> crase dupla no nome (``mdc``). " +
        "SE O BUILD FALHAR: leia a linha `error`, aplique a forma da tabela e edite SO' o trecho; nao reescreva o arquivo igual.";

    /// <summary>
    /// Compõe o system prompt com as caudas. Com harness desligado devolve o
    /// texto original (tubo puro do OpenCode).
    /// </summary>
    /// <param name="system">System prompt original.</param>
    /// <param name="harness">Liga/desliga as caudas.</param>
    /// <param name="behavior">Cauda de comportamento (pode ser vazia).</param>
    /// <param name="tools">Cauda de tools (pode ser vazia).</param>
    /// <param name="hasTools">Request traz array `tools`? Sem tools a cauda de
    /// tools e a linha "TOOLS PRIMEIRO" saem (o modelo devolveria JSON de tool
    /// como texto).</param>
    /// <returns>System prompt composto.</returns>
    public static string Compose(string? system, bool harness, string? behavior, string? tools,
        bool hasTools = true)
    {
        if (!harness) return system ?? "";
        string t = (system ?? "").Trim();
        // Data do host no proprio harness: modelos pequenos inventam data de
        // memoria; o pedido "hoje" passa a ter uma fonte verdadeira no contexto.
        string hoje = "DATA ATUAL DO HOST: " + System.DateTime.Now.ToString("yyyy-MM-dd") +
                      " (use esta data quando o pedido citar hoje).";
        t = (t.Length > 0 ? t + "\n\n" : "") + hoje;
        // Pedido sem tools: troca o delta padrao (que manda disparar tool) pela
        // variante de texto. Override do deployment e preservado.
        string? beh = behavior;
        if (!hasTools && string.Equals(behavior, DefaultBehavior, StringComparison.Ordinal))
            beh = DefaultBehaviorNoTools;
        if (!string.IsNullOrWhiteSpace(beh)) t = (t.Length > 0 ? t + "\n\n" : "") + beh;
        if (hasTools && !string.IsNullOrWhiteSpace(tools)) t = (t.Length > 0 ? t + "\n\n" : "") + tools;
        return t;
    }
}
