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

**Estado: implementado (2026-10-30)**
- Bridge: `RecordSystemSent` passou a registrar `main_harness` (retorno real de
  `MainHarness.Apply`, não a flag de CLI), `has_tools`, `tools_tail` (cauda de
  tools presente no system final quando o request declara `tools`), `lane`
  (`bridge`/`opencode`) e `ts` — tudo em `GET /api/test/last-system`.
- Runner: `TestContext.Run` chama `BridgeFlowGate.Probe` ao fim de **todo**
  teste `E-*` em `Mode == "bridge"`; a checagem vive num lugar só
  (`Rochas.OpenCodeBridge.Test/BridgeFlowGate.cs`), sem copy-paste por teste.
- Refutação: `fluxo bridge incompleto: ausente <main_harness|tails|tools_tail>`
  + dump (`path`, `model`, `chars`, `tail_scope`, `harness_tails`, `has_tools`,
  `tools_tail`, `lane`).
- Honestidade do portão: registro anterior ao início do teste (`ts`) recusa com
  causa própria; registro de outro cliente (`lane != bridge`) sai como
  `INCONCLUSIVO` (não acusa o teste) porque a bridge guarda só o último system.
- Prova unitária: `U-flow-gate` (caso feliz + negativos: sem harness, sem cauda,
  request sem tools, `tail_scope=off`, registro ausente, registro de outro
  cliente, registro velho).

**Aceite**
- Um teste `bridge` sem harness/tail no fluxo falha com
  `fluxo bridge incompleto: ausente <main_harness|tools_tail>` e dump do
  registro; um teste `bridge` com fluxo completo passa com a linha de
  atestação no log; um teste `opencode` **não** é afetado.
- Prova no `events.jsonl`/log do run e no `[test] PASS/FAIL`.
