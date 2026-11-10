# Gate 10 belts — placar e diagnostico (2026-11-10)

Modelo em teste: `llamapure/Gemma4-E4B-GK-Q4` (GPU1 :4100, 524k ctx, temp 0.1 para build/scripter,
0.1/140 steps para orch). Harness: `battery-war.sh chat tools belts-<agente>-<cor>` via
`belts-run.sh` (3 tentativas por belt), collector `:4199` com `hints.py`.

## Placar

| # | belt | status | tent. | tempo | evidencia |
|---|------|--------|-------|-------|-----------|
| 1 | build roxa | **PASS** | 3 | 379s | `Cadastros.csproj` unico + 4 `.cs` + DDL, 0 erros |
| 2 | build marrom | **PASS** | 3 | 955s | `Reembolsos.csproj` unico + `reembolsos.sql` + 3 `.cs`, 0 erros |
| 3 | build preta | **PASS** | 2 | 405s | `Portal.csproj` + `src/Program.cs`, 0 erros |
| 4 | build azul | **PASS** | 1 | 49s | `Consulta.csproj`, 0 erros |
| 5 | build verde | **PASS** | 1 | 93s | 0 erros |
| 6 | orch azul | **PASS** | 1 | 53s | `Reembolsos.csproj` raiz + `Reembolso.sql` + repo generico + servico, 0 erros |
| 7 | orch roxa | FAIL | 3 | 1644s | 6 rodadas de ajuste; multi-entidade nao fecha |
| 8 | orch verde | FAIL | 3 | 969s | DDL ausente + erro de compilacao na 3a |
| 9 | orch preta | FAIL | 3 | 1614s | 3a sem `Colaborador`; orch relata "nao posso verificar com as ferramentas desabilitadas" |
| 10 | orch marrom | FAIL | 3 | 1843s | erro de compilacao nas 3 tentativas |

**Build: 5/5 PASS. Orch: 1/5 PASS** (azul, escopo de uma entidade). Gate completo medido: 6/10.

## Por que a trilha orch falha (causa medida, nao suposta)

1. **O orch nao tem `shell`** — nao compila, nao inspeciona, nao move arquivo. Ele agrega o texto
   que o Scripter devolve e encerra. Na belt preta o proprio modelo escreveu: *"A verificacao final
   do build da raiz nao pode ser realizada com as ferramentas desabilitadas."* Sem verificacao, o
   erro so aparece no aceite do harness.
2. **O Scripter escolhe a arvore de pastas** — em duas delegacoes nasceram `Cadastros/` e `src/`
   com projetos distintos, ou `.csproj` aninhado. O aceite exige um `.csproj` com nome na raiz.
3. **Enumeracao de entidades** — "clientes e fornecedores" virou so `Cliente`; "colaborador + folha
   de ponto" virou nada com o nome do dominio. O 4B trata o enunciado como um item, nao como lista.
4. **Exemplo vira conteudo** — o system do orch tinha `Reembolso.cs`/`Reembolsos.csproj` literal e o
   modelo copiou: a primeira rodada "passou" com um CRUD de reembolsos num caso de cadastros.

## O que foi corrigido no harness (vale para todas as belts)

- Prompts/agents sao gerados no **heredoc de `battery-war.sh`**; `war-tools-chat/opencode.json` e
  saida. Guarda nova: o JSON gerado e validado e aborta com mensagem se invalido — um par de aspas
  no prompt derrubou a rodada inteira como "Model unavailable".
- Exemplo do orch virou `<parte do escopo>` + regra explicita "EXEMPLO e' FORMA, nao conteudo".
- `orch.steps` 80 -> 140; `build` e `scripter` com "namespace e tipo declarados UMA vez".
- Aceite por belt exige o **dominio do enunciado** nos `.cs/.sql`, aceitando PT/EN
  (`Cliente|Customer`, `Fornecedor|Supplier`, `Reembolso|Refund|Reimbursement`,
  `Colaborador|Employee`, azul `Repositor|Repository`) e `.csproj` **com nome**.
- Nudge do harness informa o que falta (`escopo faltando: Fornecedor|Supplier`) e os erros de build.
- `rerun.sh` com `setsid`: a rodada fica em sessao propria e sobrevive ao fim da tool call que a
  lancou (antes o runner+battery morriam e o `opencode` ficava orfao).

## Caminhos para liberar as orch (decisao do usuario)

1. **Dar `shell` ao orch** (allow `dotnet build`, `grep`, `ls`, `mv`) para ele verificar e corrigir a
   arvore antes de concluir. Menor mudanca de harness; muda a definicao da role (deixa de ser
   "so delega").
2. **Orquestrador maior** (>= 8B, ou o UD-Q3 da GPU2 como orquestrador) mantendo a role como esta.
3. **Serializar a construcao**: o proprio harness injeta a lista de arquivos-alvo no enunciado do
   orch (receita), aceitando que a belt deixa de medir planejamento.

Recomendacao tecnica: (1) para o gate atual — e' o unico caminho que nao muda o significado da belt
(o orch continua orquestrando, mas passa a poder conferir o artefato que agrega).
