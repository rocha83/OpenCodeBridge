# Roxa: limite do modelo vs. "receita" no enunciado (2026-11-10)

## Pergunta
O `P_ROXA` chegou a listar arquivo por arquivo e a exigir `namespace Cadastros;` unico.
Isso e' o que empurra o PASS? Um dev senior escreveria assim? O aceite precisa disso?

## Evidencia (rodada 15:18-15:24, enunciado COM a lista de arquivos)
O workspace saiu exatamente no layout pedido (Dominio/Cep.cs, Dominio/Cliente.cs, ...,
Sql/schema.sql), mas o modelo gravou **duas declaracoes do mesmo tipo no mesmo arquivo**:

```
Dominio/Cep.cs
1  namespace Cadastros;
3  public readonly record struct Cep(string Valor);
5  namespace Cadastros;            <- CS8954 (namespace repetido)
7  public sealed class Cep { ... }  <- mesmo tipo, segunda declaracao

Dominio/Cliente.cs
1  namespace Cadastros;
3  public sealed class Cliente { ... }
11 namespace Cadastros;             <- CS8954
13 public sealed class Fornecedor { ... }  <- CS0101 (Fornecedor tambem em Fornecedor.cs)
```

Resultado: `RESULTADO [belts-build-roxa]: FAIL` (358 s) **com** a receita completa.
Ou seja, a lista de arquivos nao evita o defeito: o modelo emite uma variante "record"
e uma variante "class" para o mesmo conceito e repete a linha `namespace`.

## Conclusao
- A "receita" de arquivos **nao e' o que garante PASS** — com ela a roxa falhou igual.
- O defeito e' de geracao do modelo (duplicar declaracao/namespace no mesmo arquivo),
  nao de especificacao do enunciado.
- Decisao: `P_ROXA` volta ao enunciado natural (pastas por camada, um tipo por arquivo,
  aceite de build). A receita arquivo-a-arquivo **nao fica** no enunciado.

## Segundo achado: explosao de arquivos e args de tool quebrados (rodada 15:32-15:50)

Com o enunciado natural e os hints ativos, o modelo entrou em outro laco:

- `Dominio/CpfCnpjBrFormatadoComDddCepValid4.cs` ate `...Valid8.cs`: em vez de corrigir o
  arquivo existente, ele **cria um arquivo novo com nome incrementado** a cada tentativa
  (provavel reacao ao CS0101 "ja contem uma definicao").
- `edit` falha com `Invalid arguments for tool "edit"`: o argumento chega com lixo do
  template dentro do texto (`... }<tool_call|><|tool_call>call:write{content:"`), e as
  vezes com `path` em vez de `filePath`. O `write` funciona, o `edit` nao.
- Resultado: ~35 requests so de erro de protocolo, 160 passos consumidos, FAIL.

Conclusao: o bloqueio da roxa nao e' o enunciado — e' (a) o idioma de geracao do modelo
(namespace/tipo repetido) e (b) a emissao de argumentos de tool quebrados quando o texto
tem regex/escapes. Ambos sao limitacao de modelo/template, nao de especificacao.

## Terceiro achado: args de tool invalidos (coberto por codigo, nao por enumerado)

Referencia na bridge C#: `ToolCallRepair` (tira do historico a assistant com args
invalido e devolve UM nudge com a regra de JSON), `ToolArgsParser` (le JSON5
tolerante), `ToolHelp` (contrato de uso por tool + exemplo) e `ToolContentGuard`
(recusa conteudo elidido / reescrita total no `edit`).

Porte enxuto no tunel puro (`hints.py::tool_call_problem` + `collector::tool_arg_hint`):

- valida a ULTIMA tool call do request (o que o modelo mandou no turno anterior);
- defeitos cobertos, cada um com exemplo otimista: `alias` (`path`->`filePath`,
  `old_string`->`oldString`), `json` (cortado), `colado` (`<tool_call>` dentro do
  texto), `faltando` (campo obrigatorio) e `tipo` (args nao e' objeto);
- injeta UMA mensagem `[tool-hint]` com a forma exata que passa, anti-spam por
  (tool, defeito) e no mesmo teto `HINT_MAX` do build-hint;
- nao e' erro de compilacao (CSxxxx), entao o retry do build-hint nao cobre:
  fica neste caminho proprio, decidido no request.

## O que ficou no lugar da receita

1. `hints.py` + collector `build_hint`: no erro real, o modelo recebe o `arquivo(linha,col)`
   e a acao objetiva — para CS8954, **um unico** `sed -i '21d;22d;24d' arquivo` (todas as
   linhas de namespace repetido do arquivo, sem repetir erro); para CS0101/CS0111/CS8863,
   "apague a segunda declaracao em arquivo:linha, nao reescreva o arquivo".
2. CS0234 e desambiguado no texto: pai `NamespaceA.NamespaceB` = namespace declarado 2x
   (mesma causa do CS8954); `AspNetCore/Extensions` ausente = `FrameworkReference`.
3. CS0246 cobre as duas causas: `using <Namespace do tipo>;` quando o tipo existe em outra
   camada, ou a declaracao do tipo quando nao existe.
4. Regra permanente no system prompt do agente `build`: nunca declarar o mesmo tipo duas
   vezes no arquivo (class OU record) e `namespace X;` uma unica vez.

O erro "duplicar declaracao/namespace no mesmo arquivo" e' conhecido e agora tem hint por
codigo (CS8954/CS0101/CS0111/CS8863) com acao executavel — nao precisa entrar no enunciado.

## Aceite (asserts)
O assert da belt continua o mais simples que cobre o enunciado de build:
`rc=0 do dotnet build` no `.csproj` da raiz + existencia de fonte `.cs` real fora de
`obj/bin` (um `.csproj` so, na raiz). A cobertura de SQL/React aparece no
`PARCIAL` (csharp/sql/react), nao como corte: o criterio da belt build e' compilar.
