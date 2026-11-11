# Orch (5 belts): onde o orquestrador 4B para — 2026-11-10

Trilha `belts-orch-*` = o mesmo modelo (`llamapure/Gemma4-E4B-GK-Q4`) com a role `orch`,
que so pode chamar `subagent(scripter)`; quem escreve e' o `scripter` (mesmo modelo, loop
de tools proprio). O aceite e' identico ao das belts build: um `.csproj` com nome na raiz,
`dotnet build` rc=0, fonte `.cs` real e o **dominio do enunciado** presente nos `.cs/.sql`.

## Rodadas medidas (roxa)

| # | quando | resultado | causa |
|---|--------|-----------|-------|
| 1 | 16:35 | PASS **invalido** | system do orch tinha `Reembolso.cs`/`Reembolsos.csproj` como exemplo literal e o 4B copiou o exemplo como conteudo |
| 2 | 16:46 | FAIL 3x | exemplo removido; aceite exigindo dominio; build quebrado (CS8954) |
| 3 | 17:15 | FAIL 3x | `.csproj` sem nome (MSB4044); tipos em ingles (Customer/Supplier) |
| 4 | 17:42 | FAIL 3x | entregou so `Cliente` (sem `Fornecedor`); 2 `.csproj` na raiz + 1 aninhado |
| 5 | 18:06 | FAIL 3x | 4 `.csproj` em arvores diferentes (`Cadastros/`, `src/`), duplicacao de trabalho por 2 scripters |
| 6 | 18:32 | FAIL 3x | tentativas 1 e 3 com erro de compilacao; tentativa 2 sem `Fornecedor` |

## O que foi corrigido no caminho (e continua valendo)

- Prompts/agents sao gerados no **heredoc do `battery-war.sh`**; `war-tools-chat/opencode.json`
  e' saida do harness. Guarda nova: o JSON gerado e' validado (`json.tool`) e aborta se invalido —
  um par de aspas duplas no prompt do orch derrubou a rodada inteira com "Model unavailable".
- Exemplo do orch virou placeholder (`<parte do escopo>`), com a regra "EXEMPLO e' FORMA, nao conteudo".
- `orch.steps` 80 -> 140; `build` e `scripter` com a regra "namespace e tipo declarados uma unica vez".
- Aceite: escopo aceita PT/EN (`Cliente|Customer`, `Fornecedor|Supplier`) e exige `.csproj` com nome.
- Direcionamento do harness passa o que falta do escopo (`escopo faltando: Fornecedor|Supplier`).
- Orch instruido a: listar as entidades antes de delegar, um micro-enunciado por vez, arquivos
  disjuntos, `.csproj` na raiz e nada em subpasta com outro projeto.

## Diagnostico

O modelo 4B **nao sustenta coordenacao**: ele (a) repete o exemplo do system, (b) enumera mal as
entidades do enunciado, (c) delega dois micro-enunciados que escolhem arvores de pastas diferentes
para o mesmo dominio, e (d) agrega sem verificar o build (as permissoes do orch negam shell).
Os hints de build funcionam (11-16 por rodada), mas cada tentativa gasta 160-270 requests e o
resultado final ainda quebra. Os erros nao sao de enunciado: sao de execucao multi-agente.

## Conclusao

- Trilha `build` (5/5): PASS com o harness atual (roxa 3a, marrom 3a, preta 2a, azul 1a, verde 1a).
- Trilha `orch`: **bloqueada por limitacao do modelo/template**, nao por especificacao. Os ajustes
  de prompt acima sao o maximo que o harness pode fazer sem inventar uma "receita" de arquivos no
  enunciado (que ja foi testada e nao garante PASS).
- Para liberar as 5 orch em producao: (1) orquestrador maior (>= 8B) ou (2) permitir `shell` ao orch
  para ele mesmo compilar antes de concluir, ou (3) trilha orch avaliar artefato agregado com o
  proprio `build` como fallback (muda o significado da belt).
