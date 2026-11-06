#!/usr/bin/env bash
# Push do repositorio persistente para o origin/main.
# O token NUNCA fica no repo: vem de GITHUB_TOKEN (ou do 1o argumento).
# Sem credencial o push falha rapido — e' o comportamento desejado (fail fast).
#
# Uso:
#   GITHUB_TOKEN=ghp_... scripts/push-persist.sh
#   GITHUB_TOKEN=... scripts/push-persist.sh --branch main
set -euo pipefail
set +x

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

BRANCH="main"
TOKEN="${GITHUB_TOKEN:-}"
while [ $# -gt 0 ]; do
  case "$1" in
    --branch) BRANCH="${2:?--branch exige valor}"; shift 2 ;;
    -h|--help) sed -n '2,10p' "$0"; exit 0 ;;
    ghp_*) TOKEN="$1"; shift ;;
    *) echo "ERRO: argumento desconhecido '$1'" >&2; exit 2 ;;
  esac
done

if [ -z "$TOKEN" ]; then
  echo "ERRO: defina GITHUB_TOKEN (push anonimo para https nao existe)" >&2
  exit 2
fi

REMOTE="$(git remote get-url origin)"
SLUG=$(echo "$REMOTE" | sed -E 's#^https?://[^/]+/##; s#\.git$##')
[ -n "$SLUG" ] || { echo "ERRO: origin sem slug reconhecivel: $REMOTE" >&2; exit 2; }

URL="https://${TOKEN}@github.com/${SLUG}.git"
echo "push ${BRANCH} -> ${SLUG}"
git push "$URL" "${BRANCH}" 2>&1 | sed -E "s#${TOKEN}#***#g"

head=$(git rev-parse HEAD)
remote=$(git ls-remote "$URL" "refs/heads/${BRANCH}" | awk '{print $1}')
echo "local=${head}"
echo "origin/${BRANCH}=${remote}"
[ "$head" = "$remote" ] || { echo "ERRO: origin/${BRANCH} nao bateu com HEAD" >&2; exit 1; }
