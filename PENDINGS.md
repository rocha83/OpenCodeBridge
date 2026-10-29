# PENDINGS — itens abertos (não implementados)

> Registro honesto do que foi pedido e ainda **não** entregue. Cada item deve
> sair daqui só com prova (comando/aceite) no retorno.

## P1. Portão genérico de fluxo (main harness + tools tail) no fim de cada execução

**Pedido** — implementar um **portão genérico** no final das execuções: quando a
execução ocorreu em **modo BRIDGE**, validar nos logs se houve **fluxo de main
harness** *e* **tools tail** no request. Se esse fluxo **não** existir, o teste
deve ser **refutado**, sinalizando explicitamente a **ausência** dos mesmos no
fluxo (falha por instrumentação, não por assertiva semântica).

**Contrato desejado**
- Disparo: ao encerrar uma execução cujo modo é `bridge` (não vale para
  `opencode`, que se auto-gerencia).
- Fonte da verdade: o registro do **request efetivamente enviado** (não o
  system que o teste monta, não o `chars` da base do harness — hoje o evento
  `main-harness` loga só `MainHarness.Text.Length`, o que **não** prova nada).
- Checagens mínimas:
  - `main_harness` aplicado no caminho (`chat` e/ou `responses`);
  - `harness_tails` ativo e `tail_scope` != `off`;
  - **tools tail** presente no system final quando o request declara `tools`
    (é a cauda que carrega o contrato `read/write/edit/glob`);
  - FsMini (quando a fase é F#) presente no system final — já coberto pelo
    endpoint `GET /api/test/last-system` (`fsmini`, `int_regra`).
- Refutação: FAIL com mensagem do tipo
  `fluxo bridge incompleto: ausente main_harness` / `ausente tools_tail`, com o
  dump do que existe (path, tail_scope, harness_tails, tools=N).
- Deve valer para **qualquer** teste, não só `E-mission-tools` (por isso
  "genérico"): um check único no runner/driver, não copy-paste por teste.

**Estado atual (parcial)**
- Já existe `GET /api/test/last-system` + `RecordSystemSent` na bridge
  (`Program.cs`), que registra o system final e sinaliza `fsmini` /
  `int_regra` / `tail_scope` / `harness_tails`. É a **base** do portão, mas
  hoje só é consultado dentro de `E-mission-tools`.
- Falta: (a) incluir **tools tail** (`DefaultTools` presente ou
  `hasTools`+tail aplicada) no registro; (b) promover a checagem a **portão
  único** no runner (`TestContext.Run` / driver) para todo teste em `--mode
  bridge`; (c) mensagem de refutação nomeando a ausência.

**Aceite**
- Um teste `bridge` sem harness/tail no fluxo falha com
  `fluxo bridge incompleto: ausente <main_harness|tools_tail>` e dump do
  registro; um teste `bridge` com fluxo completo passa com a linha de
  atestação no log; um teste `opencode` **não** é afetado.
- Prova no `events.jsonl`/log do run e no `[test] PASS/FAIL`.

## P2. Guard de tool call idêntica ENTRE turnos (anti-repeat de no-progresso)

**Pedido** — o anti-repeat atual (`ContentAntiRepeat`) compara repetição *dentro
do stream* (texto/args da mesma resposta). Chamada **idêntica entre turnos** —
mesmo `name` + mesmos `arguments`, com o mesmo resultado de erro — não é
detectada, e o modelo queima o orçamento de turnos.

**Evidência (2026-10-29, 09:40, `E-mission-tools`)** — `[F#]` T5..T21:
9 pares `edit`+`shell` com os MESMOS `oldString`/`newString` e o mesmo
`error FS0010` devolvido; 16 dos 22 turnos gastos sem mudar o arquivo.

**Contrato desejado**
- Detecção no servidor (payload traz o histórico): `tool_calls` do turno N
  iguais (name + args canonicalizados) às do turno N-1 **e** os `role: tool` de
  N-1 contendo erro.
- Aviso curto, **sem repetir a descrição do erro** (o modelo já a tem na
  mensagem anterior, `role: tool`): só a constatação de repetição e a ação que
  muda o estado (ler o arquivo atual / trocar args).
- Exceção: quando a call foi cortada (salvage/JSON recusado) e **não existe**
  `role: tool` daquela call — aí o aviso leva o erro, porque é a primeira vez que
  ele aparece.
- Segundo aviso consecutivo = encerra a tentativa com o mesmo texto (guard
  próprio contra laço, turno do aviso não debita orçamento).
- Vale para `chat` e `responses`; nada de lógica nova no consumidor/teste.

**Aceite** — unit que prova os dois ramos (com e sem `role: tool` anterior);
`E-mission-tools` isolado sem par `edit`/`shell` repetido no trail do `_mission.log`.
