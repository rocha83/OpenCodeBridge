# PENDÊNCIAS — chroot Cubic (LMDE 7 "gigi" / Debian 13 trixie)

Base da release: `lmde-7-cinnamon-64bit-20261023.iso` (Cubic 2026-10-23 13:43).
Tudo abaixo é o que falta aplicar no chroot (não está na imagem). Este é o checklist
único. O kit está neste zip:

```
# descompactar no chroot:  S=/root/remaster-linux-2610231822
```

**Não alterar:** bridges (`0.0.0.0` + `--api-key`, sem Basic auth), blocos já existentes
do `Caddyfile`, `/opt/models/*` (GGUF de 3,98 GB já na imagem) e `/etc/sudoers.d/opencode`.

---

## P1 — Arquivos de sistema (scripts + wrapper zenity + 5 units + configs)

- [ ] Copiar scripts e units:

```bash
S=/root/remaster-linux-2610231822
install -m 755 "$S"/artefatos/bin/*.sh /usr/local/bin/
install -m 755 "$S"/artefatos/bin/zenity /usr/local/bin/zenity   # wrapper (sem .sh)
install -m 644 "$S"/artefatos/systemd/*.service /etc/systemd/system/
install -m 644 "$S"/artefatos/etc/60-ocagents.conf /etc/ssh/sshd_config.d/
```

Scripts: `ocagent-sandbox.sh`, `start-ocagent-tui.sh`, `start-opencode-web-dev.sh`,
`start-opencode-web-moe.sh`, `start-opencodeweb.sh`, `start-ocagent-web.sh`,
`start-llama-gpu2.sh`, `start-bridge-gpu2.sh`, `svc-common.sh`, `svc-firewall.sh`,
`share-samba.sh`, `start-caddy.sh`, `start-lazydocker.sh`, `setup-ufw-remaster.sh` — e o wrapper **`zenity`**
(sem `.sh`; `/usr/local/bin` precisa vir antes de `/usr/bin` no PATH da sessão).
Units: `opencode-web.service`, `opencode-web-dev.service`, `opencode-web-moe.service`,
`llama-gpu2.service`, `bridge-gpu2.service`.

- [ ] Confirmar o wrapper (deve imprimir o caminho `/usr/local/bin/zenity` e o teste
      abaixo deve sair com `rc=5`, sem janela no canto superior esquerdo):

```bash
command -v zenity
timeout 10 /usr/local/bin/zenity --question --title="TESTE ZENITY" \
  --text="Se esta janela aparecer CENTRALIZADA e ESCURA, o padrao esta ok." --timeout=5
echo "rc=$?"
```

- [ ] Confirmar o tema sob sudo (a janela deve sair **escura**, não branca):

```bash
sudo /usr/local/bin/zenity --info --title="TESTE ROOT" --text="Tema escuro?" --timeout=5
```

- [ ] LightDM — **mesclar** `hidden-users` (não sobrescrever o arquivo inteiro):

```bash
sed -i 's/^hidden-users=.*/hidden-users=nobody nobody4 noaccess moe-agent dev-agent/' /etc/lightdm/users.conf
grep '^hidden-users' /etc/lightdm/users.conf
```

- [ ] Caddy — o ícone (`start-caddy.sh`) **reescreve** o Caddyfile inteiro, então os blocos
      novos precisam estar no gerador. Instalar gerador + snippet e só então anexar o bloco
      (se o Caddyfile ainda não os tiver):

```bash
install -m 755 "$S"/artefatos/bin/start-caddy.sh /usr/local/bin/start-caddy.sh
install -m 644 "$S"/artefatos/etc/caddy-agents.conf /usr/local/share/caddy-agents.conf
grep -q 'rocha26.ddns.net:4230' /etc/caddy/Caddyfile || \
  cat "$S"/artefatos/etc/caddy-bloco.conf >> /etc/caddy/Caddyfile   # 4230/4240/4250
```

- [ ] Recarregar e validar:

```bash
systemctl daemon-reload
sshd -t && systemctl reload ssh
caddy validate --config /etc/caddy/Caddyfile --adapter caddyfile
```

**ACEITE P1**

```bash
for f in ocagent-sandbox start-ocagent-tui start-opencode-web-dev start-opencode-web-moe \
         start-opencodeweb start-ocagent-web start-llama-gpu2 start-bridge-gpu2 \
         start-caddy start-lazydocker setup-ufw-remaster svc-common; do
  test -x /usr/local/bin/$f.sh || echo "FALTA $f"; done
grep -q 'hostname 127.0.0.1' /usr/local/bin/start-opencodeweb.sh && echo web-script-ok
grep -q 'hostname 127.0.0.1' /etc/systemd/system/opencode-web.service && echo web-unit-ok
grep -c 'rocha26.ddns.net:4230\|rocha26.ddns.net:4240\|rocha26.ddns.net:4250' /etc/caddy/Caddyfile   # 3
grep -c 'caddy-agents.conf' /usr/local/bin/start-caddy.sh   # 1
sshd -t && echo sshd-ok
caddy validate --config /etc/caddy/Caddyfile --adapter caddyfile 2>&1 | tail -1   # Valid configuration
```

---

## P2 — Contas isoladas (uid fixo 1001/1002)

- [ ] Criar as contas (se já existirem no chroot, ajustar shell/home/grupos em vez de recriar):

```bash
useradd -m -u 1001 -s /usr/sbin/nologin -c "OpenCode MoE (chat-only)" moe-agent
useradd -m -u 1002 -s /bin/bash        -c "OpenCode dev-agent (TUI+Web)" dev-agent
chmod 750 /home/moe-agent /home/dev-agent
install -d -o moe-agent -g moe-agent -m 750 /home/moe-agent/workspace /home/moe-agent/.run
install -d -o dev-agent -g dev-agent -m 750 /home/dev-agent/workspace /home/dev-agent/.run
```

- [ ] Senhas (`chpasswd` falha sob PAM — usar `usermod -p`):

```bash
usermod -p "$(openssl passwd -6 'Convidado.PCSS@LVP006')" moe-agent
usermod -p "$(openssl passwd -6 'Developer.PCSS@LVP006')" dev-agent
```

**ACEITE P2**

```bash
id moe-agent; id dev-agent          # nenhum grupo sudo/docker/adm
stat -c '%a %U %n' /home/moe-agent /home/dev-agent      # 750
getent passwd moe-agent dev-agent    # 1001 / 1002
```

---

## P3 — Config do OpenCode por conta (3 homes, sem skel)

- [ ] `mint` (live user) — provedores com `package` + ctx 65536 e 6 papéis:

```bash
install -d -m 755 /home/mint/.config/opencode/agents
cp "$S"/artefatos/opencode-mint.json /home/mint/.config/opencode/opencode.json
cp "$S"/artefatos/agents/mint/*.md   /home/mint/.config/opencode/agents/
chown -R mint:mint /home/mint/.config/opencode
```

- [ ] `dev-agent` — só Q3, papéis Build/Plan/MoE/Scripter:

```bash
install -d -o dev-agent -g dev-agent -m 750 /home/dev-agent/.config/opencode/agents
cp "$S"/artefatos/opencode-dev-agent.json /home/dev-agent/.config/opencode/opencode.json
cp "$S"/artefatos/AGENTS.md               /home/dev-agent/.config/opencode/AGENTS.md
cp "$S"/artefatos/agents/dev/*.md         /home/dev-agent/.config/opencode/agents/
cp "$S"/artefatos/etc/service-dev-agent.json /home/dev-agent/.config/opencode/service.json
chown -R dev-agent:dev-agent /home/dev-agent/.config
```

- [ ] `moe-agent` — só `Gemma4-E4B-UD-Q3-moe`, papel `moe`:

```bash
install -d -o moe-agent -g moe-agent -m 750 /home/moe-agent/.config/opencode/agents
cp "$S"/artefatos/opencode-moe-agent.json /home/moe-agent/.config/opencode/opencode.json
cp "$S"/artefatos/agents/moe/*.md         /home/moe-agent/.config/opencode/agents/
cp "$S"/artefatos/etc/service-moe-agent.json /home/moe-agent/.config/opencode/service.json
chown -R moe-agent:moe-agent /home/moe-agent/.config
```

- [ ] Remover config órfã do skel (evita agente duplicado em usuário novo):

```bash
rm -rf /etc/skel/.config/opencode
```

**ACEITE P3**

```bash
grep -c '"package"' /home/mint/.config/opencode/opencode.json     # 2 (llama, cpu)
grep -c '"context": 65536' /home/mint/.config/opencode/opencode.json   # 6
test ! -d /etc/skel/.config/opencode && echo skel-ok
sudo -u dev-agent bash -lc 'cd /home/dev-agent/workspace && HOME=/home/dev-agent \
  XDG_RUNTIME_DIR=/home/dev-agent/.run opencode models 2>/dev/null | sort'
# esperado: 6 IDs llama/Gemma4-E4B-UD-Q3* e nada de cpu/opencode
```

---

## P4 — GPU2 em 2×64k

- [ ] `start-llama-gpu2.sh` com `-c 131072 -np 2` e `start-bridge-gpu2.sh` com `SVC_CTX=65536`
      (já vêm assim nos artefatos do P1) e units coerentes:

```bash
grep -o '\-c 131072 -np 2' /etc/systemd/system/llama-gpu2.service
grep -o 'SVC_CTX=65536' /usr/local/bin/start-bridge-gpu2.sh
grep -o '\--context-tokens 65536' /etc/systemd/system/bridge-gpu2.service
systemctl daemon-reload
```

**ACEITE P4** (com GPU disponível; no chroot só validar arquivos)

```bash
systemctl restart llama-gpu2 && sleep 20
curl -s -H 'Authorization: Bearer guest@123' http://127.0.0.1:4110/v1/models \
  | python3 -c 'import json,sys;d=json.load(sys.stdin);print(d["data"][0].get("meta",{}).get("n_ctx"))'
# esperado: 65536
```

---

## P5 — Bridge: seeder novo + build

- [ ] Aplicar o `AgentSeeder.cs` (MoE chat-only, `subagent` em todos, cláusula 90%,
      `SeedCpuCopies` removido, linhas `(CPU Forced)` apagadas):

```bash
cd /usr/src/opencodebridge
cp "$S"/artefatos/bridge/AgentSeeder.cs Rochas.OpenCodeBridge.Web/Data/AgentSeeder.cs
systemctl stop bridge-gpu bridge-gpu2 bridge-web
dotnet build -c Release Rochas.OpenCodeBridge.slnx
systemctl start bridge-gpu bridge-gpu2 bridge-web
```

**ACEITE P5**

```bash
cd /usr/src/opencodebridge
dotnet build -c Release Rochas.OpenCodeBridge.slnx 2>&1 | tail -3     # 0 Aviso(s) / 0 Erro(s)
python3 - <<'PY'
import sqlite3
c = sqlite3.connect('/usr/src/opencodebridge/Rochas.OpenCodeBridge.Web/bin/Release/net9.0/web.db')
rows = list(c.execute("select name, tools from agents order by id"))
for n, t in rows: print(f"{n[:44]:44} | {t}")
print("total", len(rows),
      "| clausula", c.execute("select count(*) from agents where system_prompt like '%DELEGACAO POR CONTEXTO%'").fetchone()[0],
      "| cpu-forced", c.execute("select count(*) from agents where name like '%(CPU Forced)%'").fetchone()[0])
PY
# esperado: total 10 | clausula 10 | cpu-forced 0 | MoE = fetch,display,subagent
```

---

## P6 — Firewall (portas novas do Caddy) — **já aplicado na live, replicar no chroot**

- [x] Na live: `ufw allow 4230/tcp` (mint), `4240/tcp` (dev-agent), `4250/tcp` (moe-agent) e
      `ufw delete allow` de 4147/4152/4153 (v4+v6).
- [ ] No chroot: instalar o script canônico já ajustado (o loop dele agora inclui
      `4220 4230 4240 4250`) e reaplicar:

```bash
install -m 755 "$S"/artefatos/bin/setup-ufw-remaster.sh /usr/local/bin/setup-ufw-remaster.sh
/usr/local/bin/setup-ufw-remaster.sh
```

**ACEITE P6:** `ufw status | grep -E '4230|4240|4250'` → 3 linhas `ALLOW` (6 contando v6)
e nenhuma regra para 4147/4152/4153.

> Teste do proxy **exige SNI** (os sites do Caddy são por hostname):
> `curl -sk --resolve rocha26.ddns.net:4240:127.0.0.1 -u 'dev:Developer.PCSS@LVP006' https://rocha26.ddns.net:4240/` → `200`;
> `4230` com `admin:guest@123` → `200`; `4250` com `moe:...` → `200`; sem credencial → `401`.
> sem `--resolve` o TLS responde `tlsv1 alert internal error` (curl `000`).

---

## P7 — Serviços fora do boot + ícones

- [ ] Desabilitar tudo (sobe só por ícone):

```bash
for u in llama-gpu llama-gpu2 llama-cpu-forced bridge-gpu bridge-gpu2 bridge-cpu-forced \
         bridge-web caddy opencode-web opencode-web-dev opencode-web-moe; do
  systemctl disable "$u" 2>/dev/null; done
```

- [ ] Instalar os 2 ícones novos e atualizar o ícone do Lazydocker (wrapper que
      inicia o Docker antes da TUI):

```bash
install -m 755 "$S/artefatos/desktop/OpenCode Web dev-agent (4210).desktop" /home/mint/Desktop/
install -m 755 "$S/artefatos/desktop/OpenCode Web moe-agent (4220).desktop" /home/mint/Desktop/
install -m 755 "$S/artefatos/desktop/Lazydocker.desktop" /home/mint/Desktop/Lazydocker.desktop
```

- [ ] Remover atalhos redundantes de bridge (se ainda existirem na origem). Os ícones
      canônicos de bridge são **só três**: `OpenCode Bridge GPU`, `OpenCode Bridge GPU2`
      e `OpenCode Bridge CPU Forced` (ver `04-rede-e-exposicao.md` §4.1.1):

```bash
rm -f "/home/mint/Desktop/Llama GPU2 + Bridge GPU2 (padrao).desktop" \
      "/home/mint/Desktop/Llama CPU Forced + Bridge CPU Forced (padrao).desktop" \
      "/root/Desktop/Llama GPU2-CPU.desktop"
```

> O `Llama GPU2-CPU.desktop` era o **toggle** que trocava a instância `:4125` entre GPU2 e
> CPU. Com o CPU Forced em instância própria (`:4120` llama / `:4126` bridge) ele não deve
> existir.

- [ ] Garantir dono/permissão dos ícones: cada `.desktop` é `755` e pertence ao **dono do
      Desktop** que o executa (`mint`, `dev-agent`, `moe-agent`, `root`):

```bash
for d in /home/*/Desktop /root/Desktop; do
  [ -d "$d" ] || continue
  chown "$(stat -c %U "$d"):$(stat -c %G "$d")" "$d"/*.desktop 2>/dev/null
  chmod 755 "$d"/*.desktop 2>/dev/null
  find "$d" -maxdepth 1 -name '*.desktop' -printf '%u:%g %m %f\n'
done
```

**ACEITE P7**

```bash
systemctl is-enabled llama-gpu llama-gpu2 bridge-gpu bridge-gpu2 bridge-web caddy \
  opencode-web opencode-web-dev opencode-web-moe 2>&1 | sort | uniq -c   # todos disabled
ls -l /home/mint/Desktop/'OpenCode Web dev-agent (4210).desktop'
# nenhum Exec de /usr/local/bin pode faltar e todo .desktop deve ser 755 do dono:
for f in /home/*/Desktop/*.desktop; do
  e=$(grep -m1 '^Exec=' "$f" | sed 's/^Exec=//' | awk '{print $1}')
  case "$e" in /usr/local/bin/*) [ -x "$e" ] || echo "QUEBRADO: $f -> $e";; esac
done
```

---

## P8 — Fechamento da live

- [ ] Remover o sudo irrestrito da live (**manter** `/etc/sudoers.d/opencode`):

```bash
rm -f /etc/sudoers.d/live
visudo -c
```

- [ ] Limpar rastros de sessão dos homes novos (serviço/DB gerados na live):

```bash
rm -rf /home/dev-agent/.local/state/opencode /home/dev-agent/.local/share/opencode \
       /home/dev-agent/.cache/opencode /home/moe-agent/.local/state/opencode \
       /home/moe-agent/.local/share/opencode /home/moe-agent/.cache/opencode
install -d -o dev-agent -g dev-agent -m 750 /home/dev-agent/.run
install -d -o moe-agent -g moe-agent -m 750 /home/moe-agent/.run
```

**ACEITE P8:** `sudo -l -U mint | grep -c NOPASSWD:ALL` → `0`.

---

## P9 — Docker/lazydocker/Portainer (só o que não rodou no chroot)

> No chroot o `dockerd` manual (vfs, bridge none) subiu, `docker info` e
> `docker pull portainer/portainer-ce:latest` passaram. O que **não** rodou foi:
> `docker run` (`failed to create default sandbox: operation not permitted`), a TUI
> gráfica do lazydocker (sem `DISPLAY`) e o container do Portainer. Só esses itens
> ficam pendentes no host.

- [ ] Containers Docker: validar `docker run --rm hello-world` (ou o container do Portainer)
      com o Docker do sistema, não pelo `dockerd` manual do chroot.
- [ ] lazydocker pelo ícone: `Lazydocker.desktop` chama `start-lazydocker.sh`, que inicia
      `docker.service` e abre a TUI; validar numa sessão gráfica.
- [ ] Portainer: `start-portainer.sh` cria/inicia o container `portainer`, usa
      `portainer.service` e publica `:9100` (admin/guest@123); validar `/api/status`.
- [ ] O Docker **não** fica `enabled` no boot; `start-lazydocker.sh`/ícone sobe sob demanda.

```bash
sudo systemctl start docker
sudo -u mint /usr/local/bin/start-lazydocker.sh   # wrapper + TUI; feche depois
# Portainer: ícone; se o container já existir:
curl -s -o /dev/null -w '%{http_code}\n' http://127.0.0.1:9100/api/status
```

**ACEITE P9:** `docker run --rm hello-world` conclui; a TUI abre pelo ícone Lazydocker;
`/api/status` do Portainer retorna `200`, `302` ou `401`.

---

## Critérios de pronto (rodar antes de fechar a imagem)

```bash
# 1) build e sintaxe
cd /usr/src/opencodebridge && dotnet build -c Release Rochas.OpenCodeBridge.slnx 2>&1 | tail -2
for f in /usr/local/bin/ocagent-sandbox.sh /usr/local/bin/start-ocagent-tui.sh \
         /usr/local/bin/start-opencode-web-dev.sh /usr/local/bin/start-opencode-web-moe.sh \
         /usr/local/bin/start-ocagent-web.sh /usr/local/bin/start-llama-gpu2.sh \
         /usr/local/bin/start-bridge-gpu2.sh /usr/local/bin/start-lazydocker.sh; do
  bash -n "$f" || echo "ERRO $f"; done

# 2) contas e isolamento
getent passwd moe-agent dev-agent | cut -d: -f1,3,7
sudo -u dev-agent /usr/local/bin/ocagent-sandbox.sh /home/dev-agent /home/dev-agent/workspace \
  bash -c 'ls /home; ls /media /opt'      # só 'dev-agent'; /media e /opt vazios

# 3) configs
grep -c '"package"' /home/mint/.config/opencode/opencode.json      # 2
grep -c 'DELEGACAO POR CONTEXTO' /home/mint/.config/opencode/agents/*.md | grep -c ':1'  # 6

# 4) rede
ss -ltn | grep -E ':4210|:4220' || echo 'ok (sobem por ícone)'
grep -c 'reverse_proxy 127.0.0.1:4210\|reverse_proxy 127.0.0.1:4220' /etc/caddy/Caddyfile  # 2
ufw status | grep -cE '4230|4240|4250'   # 6 (v4+v6)

# 5) boot limpo
systemctl is-enabled opencode-web-dev opencode-web-moe   # disabled/disabled
```

**Fora de escopo (não fazer no chroot):** `git push` do repo, criação de chaves, alteração
das bridges, mudança de portas já publicadas (4230/4150/4151), remoção de modelos.
