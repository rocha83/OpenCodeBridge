# 10 — Ícones, scripts e units: estado canônico

Atualizado em 2026-10-27. Este é o retrato operacional dos launchers do desktop:
**ícone → script → formulário zenity → unit/systemd ou processo**. A unit é
derivada; o script é a fonte. Não editar unit à mão: o próximo clique no ícone
regenera o arquivo. Para reconstruir tudo em chroot/live, use o runbook
[`11-FIX-ICONES-UNITS.md`](11-FIX-ICONES-UNITS.md) (instalador único).

## 1. Regra de ouro

1. O ícone `.desktop` chama um script em `/usr/local/bin`.
2. O script abre **uma única janela zenity** (quando aplicável), valida e grava:
   - `/etc/systemd/system/<unit>.service`; ou
   - `/etc/opencode-web-<role>.env` + `systemctl restart`.
3. O script faz `daemon-reload` e `restart`; no fim mostra sucesso/erro.
4. `--defaults` pula o formulário e usa os valores padrão (usado por launchers
   sem diálogo e por recuperação headless).
5. **Cancelar o formulário ou o firewall encerra sem reiniciar** (`|| exit 0`).
6. O fallback de defaults só roda com `--defaults` ou sem `DISPLAY`/`zenity`.

## 2. Mapa de ícones

| ícone (`~/Desktop`) | script | porta | bind | unit/processo | exposição |
|---|---|---|---|---|---|
| `OpenCode Bridge GPU.desktop` | `start-bridge-gpu.sh` | 4124 | `0.0.0.0` | `bridge-gpu.service` | direto, `--api-key` |
| `OpenCode Bridge GPU2.desktop` | `start-bridge-gpu2.sh` | 4125 | `0.0.0.0` | `bridge-gpu2.service` | direto, `--api-key` |
| `OpenCode Bridge CPU Forced.desktop` | `start-bridge-cpu-forced.sh` | 4126 | `0.0.0.0` | `bridge-cpu-forced.service` (inativa) | direto, `--api-key` |
| `OpenCode Web.desktop` | `start-opencodeweb.sh` | 4200 | **`0.0.0.0`** | processo `opencode serve` + pidfile `/run/opencodeweb.pid` | **HTTP direto + Basic auth `guest@123`**; Caddy `4230` opcional |
| `OpenCode Web dev-agent (4210).desktop` | `start-ocagent-web.sh dev` | 4210 | `127.0.0.1` | `opencode-web-dev.service` | só loopback; Caddy/cloudflared |
| `OpenCode Web moe-agent (4220).desktop` | `start-ocagent-web.sh moe` | 4220 | `127.0.0.1` | `opencode-web-moe.service` | só loopback; Caddy/cloudflared |
| `Bridge Web UI.desktop` | `start-bridge-web.sh` | 4130 | `0.0.0.0` | `bridge-web.service` | Caddy `4151` |
| `Llama GPU.desktop` | `start-llama-gpu.sh` | 4100 | `127.0.0.1` | `llama-gpu.service` | interno (bridge 4124) |
| `Llama GPU2.desktop` | `start-llama-gpu2.sh` | 4110 | `127.0.0.1` | `llama-gpu2.service` | interno (bridge 4125) |
| `Llama CPU Forced.desktop` | `start-llama-cpu-forced.sh` | 4120 | `127.0.0.1` | `llama-cpu-forced.service` | interno (bridge 4126) |

**4200:** o bind `0.0.0.0` existe em **dois lugares** — na unit
`opencode-web.service` e no `start-opencodeweb.sh`. O ícone sobe processo próprio
com pidfile, fora do systemd. Mudar só um dos dois faz o próximo clique reverter
o bind. O padrão é HTTP (sem TLS), porta 4200, Basic auth do `opencode serve`
com usuário `opencode` e senha `guest@123`.

**4210/4220:** bind `127.0.0.1` nos scripts `start-opencode-web-{dev,moe}.sh`;
a porta/senha vem de `/etc/opencode-web-<role>.env` (`EnvironmentFile` da unit).
Nunca trocar para `0.0.0.0`; a exposição é só Caddy/cloudflared.

## 3. Formulários zenity

| script | janela | campos/defaults |
|---|---|---|
| `start-bridge-*.sh` | `ask_bridge_config` | porta, upstream, API key, modelo, ctx, usuário, senha, **Diagnóstico (combo `desligado|ligado`, default `desligado`)** |
| `start-llama-*.sh` | `ask_llama_config` | porta, usuário, senha, ctx, slots |
| `start-opencodeweb.sh` | form de 4 campos | bridge `4124`, key `guest@123`, web `4200`, senha `guest@123`; host `0.0.0.0` |
| `start-ocagent-web.sh dev` | form de 4 campos | bridge `4125`, key `guest@123`, web `4210`, senha `Developer.PCSS@LVP006` |
| `start-ocagent-web.sh moe` | form de 4 campos | bridge `4125`, key `guest@123`, web `4220`, senha `Convidado.PCSS@LVP006` |
| `start-bridge-web.sh` | `--entry` | porta `4130` já pré-preenchida com `--entry-text` |

Observações:

- `zenity --forms` não preenche campos de texto com valor inicial; o padrão é
  mostrado no rótulo `(padrao: X)` e aplicado se o campo ficar vazio.
- `--add-combo` abre no primeiro item: `desligado|ligado` abre em `desligado`.
- `--entry` aceita `--entry-text`; é o caso da porta da Web UI.
- **Cancelar** (form ou firewall) = `exit 0`, sem regenerar unit e sem reiniciar.
  O padrão antigo `|| { defaults; }` causava reinício e mensagem “ativo”; não
  reintroduzir.

## 4. Units — modelo canônico

### 4.1 Bridges

As três units de bridge são geradas com o mesmo núcleo:

```text
--thinking events --temperature 0.2 --max-tokens 16384
--context-guard 1 --context-tokens <ctx>
--passthrough-responses true
--main-harness 1 --harness-tails 1 --tail-scope all --legacy-harness 0
--anti-repeat 1 --anti-repeat-retry 1 --anti-repeat-window 512
--coerce-text-tools 0 --coerce-narrated 0 --tool-strategy 0
--upstream-parser native
# opcional: --diagnostic 1 --diagnostic-level metrics
```

`bridge-cpu-forced` usa `--temperature 0.5`, `--main-harness 0` na unit atual e
está inativa. As units `bridge-gpu` (`:4124`) e `bridge-gpu2` (`:4125`) estão
com `--main-harness 1`; a `:4125` está com diagnóstico `off`, a `:4124` com
`metrics`.

### 4.2 Harness: flag global × lane (importante)

O flag `--main-harness` é **global**, mas a aplicação é por **cliente**. O
código decide a lane por requisição (`Program.cs`):

```text
opencodeClient = UA opencode ou X-Bridge-Mode: opencode
BridgeLane     = !opencodeClient && (Mode is null || Mode.MainHarness)
mainHarness    = MainHarnessOn && BridgeLane
```

Consequência:

| cliente | unit `--main-harness 1` | efetivo | tails | `/api/status` |
|---|---|---|---|---|
| OpenCode CLI/Web (UA opencode ou header `opencode`) | sim | **off** | off | `main_harness=true`, `main_harness_opencode=false`, `tails_opencode=false` |
| WAR / cliente `X-Bridge-Mode: bridge` | sim | **on** | on | `main_harness=true`, `tails_managed=true` |
| Web UI `:4130` (Core in-process) | não usa bridge | **on** | n/a | `ChatController` chama `MainHarness.BuildSystem` |

Portanto: **“main harness off” para OpenCode é o efeito correto**, mas o flag da
unit deve continuar `1` quando a mesma bridge também atende a raia WAR/bridge.
Se colocar `--main-harness 0` na unit, o OpenCode continua off (não muda nada
para ele), mas a raia WAR fica órfã de base e o preflight
`bridge_main_harness` falha. Só use `0` se a bridge for **exclusivamente**
OpenCode/tubo puro e o WAR bridge não for usado.

### 4.3 Web UI `:4130`

A Web UI **não chama a bridge**: o `Core` roda in-process. O `ChatController`
sempre aplica:

```csharp
string harnessSystem = MainHarness.BuildSystem(agentSystem);   // chat
body["instructions"] = MainHarness.BuildSystem(instructions);  // /v1/responses
```

Assim, a Web UI mantém `mainHarness` **true** por dentro, independentemente do
flag `--main-harness` das bridges. O `BridgeUrl` do agente só dimensiona
contexto/anti-repeat; não é o cérebro do harness.

### 4.4 OpenCode Web

- `4200`: processo do ícone (`opencode serve --hostname 0.0.0.0`), sem unit
  obrigatória; a unit `opencode-web.service` também aponta para `0.0.0.0`.
- `4210`/`4220`: units systemd, `EnvironmentFile` e scripts que leem
  `OPENCODE_WEB_PORT`/`OPENCODE_SERVER_PASSWORD`.
- Os três são clientes OpenCode: não recebem MainHarness da bridge (lane off).

## 5. Estado atual (verificado em 2026-10-27)

| serviço | estado | bind | harness efetivo | diagnóstico |
|---|---|---|---|---|
| `bridge-gpu` `:4124` | active | `0.0.0.0` | `main_harness=true`, opencode off | `metrics` |
| `bridge-gpu2` `:4125` | active | `0.0.0.0` | `main_harness=true`, opencode off | `off` |
| `bridge-cpu-forced` `:4126` | inactive | `0.0.0.0` | unit atual `main_harness=0` | `off` |
| `opencode` `:4200` | processo ativo | `0.0.0.0` | lane OpenCode (base off) | Basic auth `guest@123` |
| `opencode-web-dev` `:4210` | inactive | `127.0.0.1` | lane OpenCode | senha `Developer.PCSS@LVP006` |
| `opencode-web-moe` `:4220` | inactive | `127.0.0.1` | lane OpenCode | senha `Convidado.PCSS@LVP006` |
| `bridge-web` `:4130` | active | `0.0.0.0` | Core MainHarness true | login web |

## 6. Como verificar

```bash
# bind/processo
ss -tlnH | grep -E ':(4100|4110|4120|4124|4125|4126|4130|4200|4210|4220)\b'

# 4200: HTTP + Basic auth (200 com senha; 401 sem)
curl -s -o /dev/null -w '%{http_code}\n' -u 'opencode:guest@123' http://127.0.0.1:4200/api/info
curl -s -o /dev/null -w '%{http_code}\n' http://127.0.0.1:4200/api/info

# harness efetivo
curl -s http://127.0.0.1:4125/api/harness
curl -s -H 'Authorization: Bearer guest@123' http://127.0.0.1:4125/api/status

# unit gerada
systemctl cat bridge-gpu2 opencode-web-dev opencode-web-moe bridge-web
grep -n 'main-harness\|diagnostic\|anti-repeat-window' /usr/local/bin/start-bridge-*.sh

# sintaxe dos launchers
for f in /usr/local/bin/start-{bridge,llama,opencode}*.sh; do bash -n "$f" || echo "FAIL $f"; done
```

Teste de cancelamento sem abrir janela (stub):

```bash
mkdir -p /tmp/zenity-cancel
printf '#!/bin/bash\ncase " $* " in *" --forms "*) exit 1;; *) exit 0;; esac\n' >/tmp/zenity-cancel/zenity
chmod +x /tmp/zenity-cancel/zenity
DISPLAY=:0 PATH=/tmp/zenity-cancel:$PATH /usr/local/bin/start-bridge-gpu2.sh
echo "rc=$?"   # 0 e sem restart
```

## 7. Como reaplicar/recuperar

1. Editar **o script** (não a unit).
2. Validar: `bash -n /usr/local/bin/<script>.sh`.
3. Rodar o ícone; ou `--defaults` para pular o formulário.
4. Conferir: `systemctl cat <unit>`, `ss -tln`, `/api/status`.
5. Se a unit estiver quebrada: `sudo systemctl reset-failed <unit>` e clique de
   novo; o script regrava o arquivo inteiro.
6. Backup dos scripts/units em `/tmp/opencode/zenity-backup-*` antes de mudanças.

## 8. Armadilhas que não voltar

- `ask_* ... || { defaults; }`: transforma Cancelar em reinício. Usar
  `|| exit 0` e condicionar defaults a `--defaults`/sem `DISPLAY`.
- `--harness`: alias removido; o processo não sobe. Usar `--main-harness` e
  `--harness-tails`.
- `main-harness 0` em bridge que atende WAR: preflight duro falha.
- Diagnóstico default é **desligado** no combo; para Grafana, escolher
  `ligado` (emite `--diagnostic 1 --diagnostic-level metrics`).
- 4200: mudar host só na unit ou só no script é revertido no próximo clique.
- 4210/4220: nunca `0.0.0.0`; expor só por Caddy/cloudflared.
- Unit editada à mão: o próximo clique sobrescreve. A fonte é o script.
