# 04 — Rede, exposição e o 2×56k da GPU2

## 4.1 Mapa de portas

| porta | dono | bind | exposto por |
|---|---|---|---|
| 4100 | `llama-gpu` (E4B Q4) | 127.0.0.1 | bridge 4124 |
| 4110 | `llama-gpu2` (E4B UD-Q3) | 127.0.0.1 | bridge 4125 |
| 4120 | `llama-cpu-forced` | 127.0.0.1 | bridge 4126 |
| 4124 | `bridge-gpu` | **0.0.0.0** | direto (ApiKey) |
| 4125 | `bridge-gpu2` | **0.0.0.0** | direto (ApiKey) |
| 4126 | `bridge-cpu` | **0.0.0.0** | direto (ApiKey) |
| 4130 | Bridge Web UI | 0.0.0.0 | Caddy `4151` |
| 4200 | `opencode serve` (mint) | **0.0.0.0** | HTTP direto (Basic auth `guest@123`); Caddy `4230` opcional |
| **4210** | `opencode serve` (dev-agent) | 127.0.0.1 | Caddy **`4240`** |
| **4220** | `opencode serve` (moe-agent) | 127.0.0.1 | Caddy **`4250`** |
| 4800 | app existente | 127.0.0.1 | Caddy `4150` |
| 49376 / 49377 | serviço gerenciado OpenCode (dev/moe) | 127.0.0.1 | interno |

**Bridges intactas:** seguem em `0.0.0.0` + `--api-key`. Elas **não** fazem Basic auth
(respondem 401 mesmo com `-u`). Quem autentica usuário é o host: LightDM (desktop; só
`mint` aparece — `dev-agent`/`moe-agent` são `hidden-users` e barrados no PAM), SSH
(chave/senha; `dev-agent` cai direto no TUI via `ForceCommand`, `moe-agent` é
`DenyUsers`) e Caddy (basicauth) para o Web.

### 4.1.1 Ícones das bridges (canônicos)

Só existem **três** ícones de bridge, um por backend:

| ícone | script | backend |
|---|---|---|
| `OpenCode Bridge GPU` | `start-bridge-gpu.sh` | llama `:4100` → bridge `:4124` |
| `OpenCode Bridge GPU2` | `start-bridge-gpu2.sh` | llama `:4110` → bridge `:4125` |
| `OpenCode Bridge CPU Forced` | `start-bridge-cpu-forced.sh` | llama `:4120` → bridge `:4126` |

Os atalhos “tudo de uma vez” **`Llama GPU2 + Bridge GPU2 (padrao)`** e
**`Llama CPU Forced + Bridge CPU Forced (padrao)`** (`start-models-default.sh` /
`start-models-cpu-forced.sh`) foram **removidos** do Desktop: duplicavam os ícones
individuais e confundiam qual serviço estava subindo. Para subir o par, use o ícone do
llama e depois o da bridge.

**Permissão dos ícones (regra):** cada `.desktop` deve ser `755` e pertencer ao **dono do
Desktop que vai executá-lo** (`mint`, `dev-agent`, `moe-agent` ou `root`), e o `Exec` deve
apontar para um script existente e executável (os de `/usr/local/bin` são `755 root:root`).
Verificação:

```bash
for d in /home/*/Desktop /root/Desktop; do
  [ -d "$d" ] || continue
  find "$d" -maxdepth 1 -name '*.desktop' -printf '%u:%g %m %f\n'
done
# nenhum Exec de /usr/local/bin pode faltar:
for f in /home/*/Desktop/*.desktop; do
  e=$(grep -m1 '^Exec=' "$f" | sed 's/^Exec=//' | awk '{print $1}')
  case "$e" in /usr/local/bin/*) [ -x "$e" ] || echo "QUEBRADO: $f -> $e";; esac
done
```

Removido nesta sessão: o **toggle antigo `Llama GPU2-CPU.desktop`**, que trocava a
instância `:4125` entre GPU2 e CPU. Ele apontava para `start-llama-cpu.sh` (inexistente)
e não faz sentido com o CPU Forced como instância própria (`:4120` llama / `:4126`
bridge). Para CPU use `Llama CPU Forced` + `OpenCode Bridge CPU Forced`.

## 4.2 Caddy

Bloco aplicado ao final de `/etc/caddy/Caddyfile` (**nada removido**):

```caddyfile
rocha26.ddns.net:4240 {
	tls internal
	basicauth {
		dev $2a$14$GsMfwy5wzdk/JkOIXSzxe.aFwx7I53YrbzW0FG/6Sd1Ceong6Z2T2
	}
	reverse_proxy 127.0.0.1:4210
}

rocha26.ddns.net:4250 {
	tls internal
	basicauth {
		moe $2a$14$GJQ2uRd12/2nxkCnF9pjeOOjEB1P4czhycIwLHqlbpYzxqwcwBhCG
	}
	reverse_proxy 127.0.0.1:4220
}
```

Mapa final: **4230 → 4200** (mint, auth `admin`; substituiu a 4147), **4240 → 4210**
(dev-agent, auth `dev`), **4250 → 4220** (moe-agent, auth `moe`). A faixa 42xx foi escolhida
para não colidir com o circuito 41xx já publicado (4143–4151, 4150 = Grafana `:4800`).
As antigas 4147/4152/4153 estão fechadas (sem site e sem regra de ufw).

**Cuidado com o gerador:** `/usr/local/bin/start-caddy.sh` (ícone Caddy) **reescreve** o
`Caddyfile` inteiro (`> /etc/caddy/Caddyfile`). Ele já emite o bloco 4230 e, logo depois,
faz `cat /usr/local/share/caddy-agents.conf >> /etc/caddy/Caddyfile` — esse arquivo contém
os blocos 4240/4250 com os hashes bcrypt fixos. Sem esse snippet, o ícone apaga os dois.

Os hashes são bcrypt (`caddy hash-password`). Para regenerar:

```bash
caddy hash-password --plaintext 'Developer.PCSS@LVP006'
```

**ACEITE:** `sudo caddy validate --config /etc/caddy/Caddyfile --adapter caddyfile` →
`Valid configuration`; `sudo systemctl reload caddy`.

O site é **por hostname**, então o teste precisa de SNI:

```bash
curl -sk --resolve rocha26.ddns.net:4240:127.0.0.1 -u 'dev:Developer.PCSS@LVP006' \
  -o /dev/null -w '%{http_code}\n' https://rocha26.ddns.net:4240/     # 200
curl -sk --resolve rocha26.ddns.net:4240:127.0.0.1 \
  -o /dev/null -w '%{http_code}\n' https://rocha26.ddns.net:4240/     # 401
# sem --resolve (SNI 127.0.0.1): 'tlsv1 alert internal error' / curl 000
```

Firewall: `ufw allow 4230/tcp`, `4240/tcp` e `4250/tcp` (aplicado na live; 4147/4152/4153 fechadas).

## 4.3 GPU2 em 2×56k

Antes: `-c 131072 -np 1` (1 sessão de 128k). Agora:

```bash
# /usr/local/bin/start-llama-gpu2.sh  (linha do ExecStart gerado)
... -c 114688 -np 2 ...
```

```bash
# /usr/local/bin/start-bridge-gpu2.sh
SVC_CTX=57344
```

E no `opencode.json` (provedor `llama`), os modelos Q3 com:

```json
"limit": { "context": 57344, "output": 16384 }
```

Consequência prática: duas sessões simultâneas de 56k na GPU2 (antes uma de 128k). A
cláusula de delegação dos agentes Q3 fala em `>=51k de 57344 tokens` (90%). Os modelos
Q4 (`openai`/`:4124`) continuam com `131072`.

**ACEITE**

```bash
grep -o '\-c 114688 -np 2' /etc/systemd/system/llama-gpu2.service
grep -o 'SVC_CTX=57344' /usr/local/bin/start-bridge-gpu2.sh
curl -s -H 'Authorization: Bearer guest@123' http://127.0.0.1:4125/api/status \
  | python3 -c 'import json,sys;print(json.load(sys.stdin).get("context_tokens"))'
# esperado: 57344
```

## 4.4 `opencode serve` — Basic auth nativo

`opencode serve` protege tudo com Basic auth quando `OPENCODE_SERVER_PASSWORD` está
definida; usuário é sempre `opencode`. Por isso:

- dev: `curl -u 'opencode:Developer.PCSS@LVP006' http://127.0.0.1:4210/` → 200;
- sem credencial → 401.

Nunca expor 4210/4220 em `0.0.0.0` — o bind é `127.0.0.1` e o Caddy faz o proxy.
