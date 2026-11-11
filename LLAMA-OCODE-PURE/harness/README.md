# harness/ — texto do sistema em arquivos editáveis

O túnel (`collector.py`) monta o system prompt a partir destes arquivos via
`harness_loader.py`. Nada de prompt dentro de `.sh` ou de string em Python.

## Arquivos

| caminho | papel |
|---|---|
| `manifest.json` | ordem de montagem, stacks e orçamento (fonte única da sequência) |
| `parts/*.md` | comportamento universal (14 seções, sem repetição) + `14-formas.md` (contrato do stack) |
| `skills/<id>/SKILL.md` | trilha sob demanda: frontmatter `name`/`description` + corpo (forma, não código pronto) |
| `../harness_loader.py` | lê, ordena, mede tokens e aplica o teto |

Sem texto por role: a role já chega no prompt do agente do cliente. MoE e
"roles e pipeline" saíram do pacote por serem repetição do que o cliente envia.

## Orçamento

`manifest.json` → `budget`:

- `max_total_tokens`: teto do **prefixo fixo** (system + catálogo de tools).
- `tools_reserve_tokens`: o quanto o catálogo consome (medido no dump: 12.165 ch).
- `chars_per_token`: estimador (padrão 3.5; pior caso observado 3.3).
- `on_overflow`: `drop_examples` (corta do fim das trilhas; nunca corta `parts`).

## Uso

```bash
python3 harness_loader.py --report              # tabela + total + veredito do teto
python3 harness_loader.py --write /tmp/sys.md   # grava o texto montado
curl -s localhost:4199/api/harness              # relatório do que está em produção
curl -s -X POST localhost:4199/api/harness/reload
```

O texto é montado **uma vez** por processo: editar arquivo exige reload/restart
para não quebrar o cache de prefixo do llama.

## Regras de escrita

1. Não repetir o schema das tools (o JSON já vai no request).
2. Não repetir o que uma trilha demonstra; o texto só proíbe o desvio.
3. Uma ideia por linha; seção sem duplicata (dedup é o corte permitido).
4. Identificadores de código sempre em en-US; texto no idioma do enunciado.
5. Toda trilha abre com `=== EXEMPLO n — … (não copie: leia a dinâmica) ===`;
   `parts/13-exemplos.md` fixa a regra: exemplo é forma, nunca código a reproduzir.

## Proporção medida (2026-11-11): trilha pesa mais que o pedido

Composição do prefixo montado: `parts` 4.711 ch (1.346 tok) e trilhas 23.828 ch
(6.808 tok) — **83% do prefixo são trilhas de código**. Numa tarefa de uma frase
(`tools-full`, hello world) o modelo copiou a trilha 08 e gravou
`src/api/customers.ts` + `src/Domain/Cliente.cs`, em vez do console app pedido.

Chamada direta com o mesmo enunciado, duas variantes de system:

| system | prompt | 1ª ação |
| --- | --- | --- |
| 132 ch | 216 tok | `write` com `path`/`content` (correto) |
| montado, 28.586 ch | 8.116 tok | `write` com `{"command":…}` (args de `shell` na tool errada) |

Leitura: o teto de 12k está correto; o que precisa de régua é a **proporção**.
Antes de acrescentar trilha, pergunte se o enunciado do caso precisa dela —
trilha que o caso não usa não ensina forma, só disputa atenção com o pedido.

## Modo de uso observado no opencode original (v2.0.21, 2026-11-11)

Medido no binário instalado (`node_modules/opencode-linux-x64/bin/opencode`),
na doc (`opencode.ai/docs/skills.md`) e no dump do request:

| etapa | como o opencode faz |
|---|---|
| descoberta | varre `~/.config/opencode/skills`, `~/.claude/skills`, `~/.agents/skills`, `.opencode/skills`, `.claude/skills`, `.agents/skills`; aceita `<id>/SKILL.md` ou `<id>.md` no nível da fonte; até 6 níveis; ignora ocultos e `node_modules` |
| identidade | id = nome da pasta; `name`/`description` vêm do frontmatter YAML; pasta com `SKILL.md` é **uma** skill (não varre dentro dela) |
| anúncio | o system recebe `<available_skills>` com **só** name+description (progressive disclosure), precedido por "Skills provide specialized instructions…"; o corpo não vai |
| carga | o modelo chama `skill` com o id; volta `<skill_content name="…">` com o corpo + arquivos irmãos listados (caminhos relativos à pasta) |
| permissão | a tool pede permissão `skill` por nome de skill |
| atualização | o bloco é um segmento de contexto (`core/skill-guidance`); quando a lista muda o cliente envia "The available skills have changed…" |

## Substituição cirúrgica do system original

O túnel substitui o system pelo harness montado, mas preserva **dois** blocos
do cliente, copiados verbatim:

1. `<env>` (`Working directory:`, `Workspace root folder:`, `Is directory a git repo:`…).
2. `<available_skills>` (com a linha de instrução que o precede) — sem ele a tool
   `skill` fica órfã: o modelo vê a ferramenta e nenhuma skill para rotear.

Todo o resto do system original é descartado de propósito (o harness **é** o
system). Prova no dump (`reqs/*.json`, campo `orig_system`):

| campo | tamanho | contém |
|---|---|---|
| `orig_system` | 10.321 ch | AGENTS.md do cliente + `<env>` + `<available_skills>` (13 skills) |
| `body.messages[0]` (pós-harness) | 9.7k ch | harness (2.087 tok) + `<env>` + `<available_skills>` |

O stream do modelo continua repassado sem edição (passthrough); a única
intervenção no corpo é essa troca de system e a re-serialização que a acompanha.

## Skills: medido negativa neste modelo (default OFF)

Testado com o catalogo preservado no system e as 13 skills instaladas no
workspace: **0 chamadas a `skill` em 45 requests**, mesmo com instrucao explicita
no system, e `tools-full:create` que passava na raia base voltou a **FAIL**
(CS1026: `namespace App;` + top-level statement). O 4B nao roteia por descricao;
o catalogo (11 descricoes, ~2,2k ch) e texto de stack no prefixo outra vez.
Por isso a bateria instala skill so com `WAR_SKILLS=1` e a instrucao de trilha
so sai com `SKILL_NUDGE=1`. O caminho deterministico (`HARNESS_STACK`) e o
default para caso conhecido.

## Skills: trilha sob demanda (mecanismo, com opt-in em 2026-11-11)

A trilha saiu do prefixo fixo e virou **skill** — o modelo carrega só a que o
pedido pede, chamando a tool `skill` com o id. O prefixo passou a ter o
**contrato** (`parts/14-formas.md`: uma linha por capacidade, sem arquivo pronto).

| camada | o que carrega | custo |
|---|---|---|
| `parts/` + `14-formas.md` | papel, ambiente, disciplina, contrato do stack | 2.043 tok |
| `skills/<id>` | forma concreta de uma capacidade, quando o pedido casa | ~1 chamada + o corpo |
| `HARNESS_STACK=dotnet-api\|dotnet-bus\|dotnet-react\|dotnet-ml\|dotnet` | trilha fixa, sem decisão do modelo | entra no prefixo |

Regras que valem para skill (medidas nesta máquina):

1. `description` **sempre entre aspas** no frontmatter. `description: texto: com
   dois-pontos` é YAML inválido e a skill é descartada **em silêncio** (o
   catálogo sai só com as builtin). Sintoma: modelo recebe a tool `skill` sem
   nenhuma skill listada.
2. O id é o nome da pasta; `name` no frontmatter é exibição. `description` é o
   roteador: escreva o vocabulário do pedido (o que pede, não o que a skill é).
3. O catálogo chega pelo system do cliente e é preservado por
   `collector.apply_harness` (bloco `<available_skills>`, do mesmo jeito que o
   `<env>`); sem isso o harness engolia a lista e a tool `skill` ficava órfã.
4. A bateria instala `harness/skills` em `<caso>/.opencode/skills` (desligue com
   `WAR_SKILLS=0`). Fonte única: o corpo da skill é o antigo `examples/dotnet/*`
   — não duplique trilha em dois lugares.
