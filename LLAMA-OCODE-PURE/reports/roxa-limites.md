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

## O que ficou no lugar da receita
1. `hints.py` + collector `build_hint`: no erro real, o modelo recebe o `arquivo(linha,col)`
   e a acao objetiva — para CS8954, `sed -i '<linha>d' <arquivo>`; para CS0101/CS0111/
   CS8863, "apague a segunda declaracao em arquivo:linha, nao reescreva o arquivo".
2. Regra permanente no system prompt do agente `build`: nunca declarar o mesmo tipo duas
   vezes no arquivo (class OU record) e `namespace X;` uma unica vez.

## Aceite (asserts)
O assert da belt continua o mais simples que cobre o enunciado de build:
`rc=0 do dotnet build` no `.csproj` da raiz + existencia de fonte `.cs` real fora de
`obj/bin` (um `.csproj` so, na raiz). A cobertura de SQL/React aparece no
`PARCIAL` (csharp/sql/react), nao como corte: o criterio da belt build e' compilar.
