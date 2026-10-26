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

# Nunca reset --hard: o PROD so avanca por fast-forward/rebase do persistente.
if [ -n "$(git status --porcelain)" ]; then
  echo "ERRO: $ROOT tem alteracao local; PROD e somente deploy (pull + rebuild)." >&2
  exit 1
fi

git pull --rebase persist main
dotnet build Rochas.OpenCodeBridge.slnx -c Release --nologo -v:minimal
systemctl restart bridge-gpu bridge-web
sleep 3

curl -fsS -m 5 -H 'Authorization: Bearer guest@123' http://127.0.0.1:4124/api/status >/dev/null
echo "OK: $ROOT atualizado por pull --rebase e redeploy concluido."
