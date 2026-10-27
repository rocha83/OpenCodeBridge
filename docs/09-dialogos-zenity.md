# 09 — Diálogos dos ícones: padrão único (`zenity` house style)

Os ~30 scripts de ícone chamam `zenity` direto. Em vez de editar cada um, existe um
**wrapper** em `/usr/local/bin/zenity`; como a sessão tem `/usr/local/bin` antes de
`/usr/bin`, **todos** passam por ele. O binário real continua em `/usr/bin/zenity`,
intocado. O wrapper é o único lugar que define estética, posição e tema.

## O que o wrapper faz

| chamada do script | resultado |
|---|---|
| `zenity --password --title=X` | vira `--forms --text=X --add-password="Senha"` (mesma estética do Caddy) |
| `zenity --forms ...` | mantém os campos; largura fixa **520** e **altura natural** (o GTK dimensiona pelo número de campos) |
| `zenity --entry ...` | mantém `--entry-text`; largura 520 |
| `zenity --info/--error/--warning/--question` | passam com título/texto intactos (e largura/altura que o script pediu) |
| qualquer tipo | janela **centralizada** na tela |
| qualquer tipo, rodando como **root** | a janela é desenhada pelo **usuário da sessão** (tema escuro) |

`stdout` e código de saída são preservados: `--forms` continua imprimindo `campo1|campo2`,
`--question` continua saindo 0/1 (os scripts usam `|| exit 0`), `--timeout` continua
retornando 5.

## Uma janela por ícone

Regra da casa: **o preenchimento acontece em uma única janela** com todos os campos;
depois dela vem **somente** a confirmação de firewall (quando o serviço publica porta).
Nada de perguntar porta, usuário e senha em janelas separadas.

**Defaults:** todo campo abre pedindo o dado, com o valor padrão no rótulo
(`(padrao: X)`); deixar vazio usa X. `--forms` do zenity não tem preenchimento
inicial de texto, mas **combo** (`--add-combo`/`--combo-values`) abre no primeiro
item da lista — o Diagnóstico das bridges usa `desligado|ligado` e abre em
`desligado`. Quem usa `--entry` (Web UI) recebe `--entry-text` já preenchido. Os
launchers também aceitam `--defaults` para pular a janela e usar os mesmos valores.

- `ask_port_user_pass` → 1 form (Porta, Usuário, Senha) + 1 confirmação de firewall.
- `ask_port_pass_only` → 1 form (Porta, Senha) + 1 confirmação.
- `ask_bridge_config` → 1 form (Porta, URL upstream, API key, Modelo, Ctx, Usuário,
  Senha da bridge, **Diagnóstico em combo/dropdown `desligado|ligado`**) + 1
  confirmação. O dropdown abre em `desligado` (padrão); `ligado` vira
  `--diagnostic 1 --diagnostic-level metrics` na unit gerada e `desligado` omite
  a flag.
- `start-opencodeweb.sh` (principal, `:4200`) → 1 form (porta bridge, API key
  bridge, porta web, senha web), defaults `4124` / `guest@123` / `4200` /
  `guest@123`.
- `start-ocagent-web.sh dev|moe` → 1 form (porta bridge, API key bridge, porta
  web, senha da instância). Defaults dev `4125` / `guest@123` / `4210` /
  `Developer.PCSS@LVP006`; moe `4125` / `guest@123` / `4220` /
  `Convidado.PCSS@LVP006`.
- `start-bridge-web.sh` → 1 `--entry` (porta da Web UI, `--entry-text=4130`,
  pré-preenchido) e grava `/etc/bridge-web.env`; sem outros campos.
- `ask_bridge_ports` → 1 form (2 portas) + **1** confirmação para as duas portas
  (`ask_fw_ports_or_cancel`), não duas.
- `share-samba.sh` → 1 form com 6 campos, incluindo a **senha do sudo**; não existe
  mais a segunda janela `--password` no modo Anônimo.
- Confirmações legítimas continuam separadas: “já está rodando, reiniciar?”, “criar
  diretório?”, “abrir porta no firewall?”.

## Centralização (por que precisa)

O Muffin **não** centraliza diálogos sem janela-pai: eles abrem no canto superior
esquerdo (`-22,-10` no `wmctrl`). O wrapper:

1. guarda os ids de janela existentes (`wmctrl -l`) **antes** de abrir;
2. sobe o zenity real em background e espera a janela **nova** com o título pedido
   (evita casar com um diálogo anterior de mesmo título);
3. mede com `xwininfo` (largura/altura) e a tela com `xdpyinfo`;
4. move com `wmctrl -i -r <id> -e 0,(SW-W)/2,(SH-H)/2,-1,-1`.

Atenção: `wmctrl -lG` mostra números em **pixels físicos** — em HiDPI eles parecem o
dobro do centro lógico. A conferência é **visual**, por screenshot, não pelo número do
`wmctrl`.

## Tema (janela branca sob sudo)

Scripts que se reexecutam com `sudo` (ex.: `share-samba.sh`) desenhavam a janela
**branca**: o GTK do root não herda o tema escuro da sessão (`Mint-Y-Dark-Aqua`) e cai
no Adwaita claro. O wrapper resolve na origem: se `id -u` = 0, ele se reexecuta como o
dono da sessão (`SUDO_USER` / `logname`), passando `DISPLAY` e `XAUTHORITY`. O zenity só
desenha — não precisa de root — e o valor volta normalmente para o script root.

## Como testar sem clicar

```bash
# transformação de argumentos (sem abrir janela)
DISPLAY= ZENITY_REAL=/caminho/stub /usr/local/bin/zenity --password --title="Caddy"

# centralização real (a janela fecha sozinha; rc=5 = timeout)
timeout 10 /usr/local/bin/zenity --question --title="ZSHOT" --text="Posicionamento" --timeout=4

# tema sob sudo (deve sair ESCURO, não branco)
sudo /usr/local/bin/zenity --info --title="ZROOT" --text="root" --timeout=6 &
sleep 2; DISPLAY=:0 gnome-screenshot -f /tmp/shot.png

# helpers (uma janela + uma confirmação)
PATH=/tmp/zstub:$PATH ZSTUB_OUT='4125|http://127.0.0.1:4110|k|M|65536|mint|s|ligado' bash -c '
  source /usr/local/bin/svc-common.sh
  ask_bridge_config 4125 http://127.0.0.1:4110 M 65536 T; echo "$SVC_PORT $SVC_MODEL $SVC_DIAG"'
```

Validado nesta sessão: `--question`, `--forms` (Caddy) e form de 8 campos (bridge)
apareceram **centralizados e escuros**; `sudo zenity` saiu escuro (antes: branco).

## Limites

- Sem `DISPLAY` (chroot/ssh sem X) o wrapper aplica só o padrão de tamanho e delega —
  nada de `wmctrl`.
- Se o script não passar `--title`, não há como localizar a janela: ela fica onde o WM pôs.
- `--file-selection`/`--list`/`--notification` passam direto (não são formulários).
