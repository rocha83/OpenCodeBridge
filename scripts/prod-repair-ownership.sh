#!/usr/bin/env bash
# Recupera o PROD quando .git/worktree ficaram de root (DRIVER-FIXES #21):
#   - "insufficient permission for adding an object to repository database"
#   - "unable to unlink old '<arquivo>': Permissao negada"
#   - deploy recusando com "tem alteracao local" por pull interrompido
#
# Faz: chown -R do dono do repo, DESCARTA o worktree do PROD (deploy-only) e
# mostra o status. Nao mexe em refs (nunca reset --hard).
#
# Uso: scripts/prod-repair-ownership.sh [--dry-run]
set -euo pipefail

PROD="${PROD_ROOT:-/usr/src/opencodebridge}"
OWNER="${PROD_OWNER:-mint:mint}"
DRY=0
[ "${1:-}" = "--dry-run" ] && DRY=1

[ -d "$PROD/.git" ] || { echo "ERRO: $PROD nao e' repo git" >&2; exit 1; }

owner_now=$(stat -c '%U:%G' "$PROD/.git")
root_files=$(sudo -n find "$PROD" -user root -print -quit 2>/dev/null || true)

echo "PROD=$PROD dono(.git)=$owner_now alvo=$OWNER"
if [ -n "$root_files" ]; then
  echo "arquivos de root detectados (ex.: $root_files)"
else
  echo "sem arquivos de root (nada a corrigir no dono)"
fi

if [ "$DRY" -eq 1 ]; then
  echo "dry-run: sem alteracao"
  exit 0
fi

if ! sudo -n chown -R "$OWNER" "$PROD"; then
  echo "ERRO: chown falhou (sudo -n sem permissao?)" >&2
  exit 1
fi

# PROD e' deploy-only: descartar o worktree e' o conserto do pull interrompido.
git -C "$PROD" checkout -- .
left=$(git -C "$PROD" status --porcelain | head -5)
[ -z "$left" ] && echo "worktree limpo" || { echo "AINDA SUJO:"; echo "$left"; exit 1; }

echo "OK: $PROD com dono $OWNER e worktree limpo — rode scripts/deploy-pull.sh"
