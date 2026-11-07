#!/usr/bin/env bash
# Deploy do PROD (/usr/src/opencodebridge): SOMENTE pull --rebase + rebuild + restart.
# A edicao de codigo acontece no repositorio persistente; /usr/src nunca recebe edit.
set -euo pipefail

ROOT="${DEPLOY_ROOT:-/usr/src/opencodebridge}"
PERSIST="${DEPLOY_REMOTE:-/media/mint/78AA0831AA07EB08/Projetos/Git/OpenCodeBridge}"

cd "$ROOT"

if ! git remote get-url persist >/dev/null 2>&1; then
  git remote add persist "$PERSIST"
fi

# O checkout vem do volume USB, que nao guarda bit de execucao: sem isto todo
# chmod +x aparece como "alteracao local" e o deploy aborta.
git config core.filemode false

# Nunca reset --hard: o PROD so avanca por fast-forward/rebase do persistente.
if [ -n "$(git status --porcelain)" ]; then
  echo "ERRO: $ROOT tem alteracao local; PROD e somente deploy (pull + rebuild)." >&2
  exit 1
fi

git pull --rebase persist main
# O volume USB nao guarda bit de execucao (todos os scripts vem 100644):
# restaura o +x no PROD logo apos o pull, senao run-all-levels falha com rc=126.
chmod +x scripts/*.sh scripts/war/*.sh scripts/diag/*.sh 2>/dev/null || true
dotnet build Rochas.OpenCodeBridge.slnx -c Release --nologo -v:minimal
# REGRA: rebuild exige restart da bridge (binario novo + pool HTTP). Zerar
# contador NAO exige restart: POST /api/diag/reset?probe=1&files=1 zera os
# diagnosticos sem downtime. O Grafana entra no mesmo ciclo para reaplicar
# provisioning e recarregar dashboard/datasource.
systemctl restart bridge-gpu bridge-web grafana-server
# Grafana sobe em ~5-10s (varia com o deb); bridge responde em ~2s. Espera com
# teto explicito: reprovar o deploy por pressa e' falso negativo.
for i in $(seq 1 30); do
  if curl -fsS -m 3 -H 'Authorization: Bearer guest@123' http://127.0.0.1:4124/api/status >/dev/null 2>&1; then break; fi
  sleep 1
done
for i in $(seq 1 60); do
  if curl -fsS -m 3 http://127.0.0.1:4800/api/health >/dev/null 2>&1; then break; fi
  sleep 1
done

curl -fsS -m 5 -H 'Authorization: Bearer guest@123' http://127.0.0.1:4124/api/status >/dev/null
curl -fsS -m 5 http://127.0.0.1:4800/api/health >/dev/null
echo "OK: $ROOT atualizado por pull --rebase e redeploy concluido (bridge + grafana)."
