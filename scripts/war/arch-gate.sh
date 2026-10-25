#!/bin/bash
# ============================================================================
# Portao de arquitetura: impede "teste verde" conquistado fora do Core.
#
# Contrato:
#   - Core (SDK) nao conhece cliente nenhum (console/Web/Test).
#   - Cliente NAO reimplementa regra centralizada: o simbolo existe so no Core.
#   - Teste e cliente: nao mexe em estado estatico do console
#     (BridgeSrv.Program.*) para forcar comportamento.
#
# Uso:
#   scripts/war/arch-gate.sh          # 0 = ok, 1 = violacao
#   scripts/war/arch-gate.sh --list   # lista tambem o que falta centralizar
# ============================================================================
set -u
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="${REPO:-$(cd "$HERE/../.." && pwd)}"
CORE="$ROOT/Rochas.OpenCodeBridge.Core"
CONSOLE="$ROOT/Rochas.OpenCodeBridge"
WEB="$ROOT/Rochas.OpenCodeBridge.Web"
TEST="$ROOT/Rochas.OpenCodeBridge.Test"
LIST=0
[ "${1:-}" = "--list" ] && LIST=1
FAILS=0
ok()  { echo "  PASS $*"; }
bad() { echo "  FAIL $*"; FAILS=$((FAILS + 1)); }

src() { grep -rn --include=*.cs "$@" 2>/dev/null | grep -v "/obj/\|/bin/"; }

has_def() { # $1 = simbolo; demais = diretorios
  local sym="$1"
  shift
  src -E "^[[:space:]]*(internal |public |private )?static [^=]*\b${sym}[[:space:]]*\(" "$@" | head -1
}

echo "arch-gate: Core nao conhece cliente"
leak=$(src -E 'Rochas\.OpenCodeBridge\.Web|Rochas\.OpenCodeBridge\.Test|BridgeSrv' "$CORE" | head -3)
if [ -n "$leak" ]; then bad "Core referencia cliente: $leak"; else ok "Core sem referencia a Web/Test/console"; fi

echo "arch-gate: teste nao forca estado do console"
hook=$(src -E 'BridgeSrv\.Program\.|Program\.[A-Za-z]*Setup\(' "$TEST" | head -3)
if [ -n "$hook" ]; then bad "Test mexe em estado do console: $hook"; else ok "Test nao toca em estado estatico do console"; fi

echo "arch-gate: regra centralizada nao pode ser redefinida em cliente"
CENTRAL="InputToMessages ExtractText ExtractResponseText ToolNames ReqHasTools StripReasoningItems CanonToolAlias
StripNarratedCalls TryParseJson RepairJsonEscapes JsonToString NewId WithSystemGuardrail
FindObjectEnd ParseJsValue ParseJsString ParseJsNumber TrimUnbalanced IsJsonObject MissingArgs WalkText
ExtractChatText ExtractResponsesText BodyWantsStream"
DUPES=0
for sym in $CENTRAL; do
  def_core=$(has_def "$sym" "$CORE")
  [ -z "$def_core" ] && { bad "$sym ausente do Core"; continue; }
  dup=$(has_def "$sym" "$CONSOLE" "$WEB" "$TEST")
  [ -n "$dup" ] && { bad "$sym redefinido fora do Core: $dup"; DUPES=$((DUPES + 1)); }
done
[ "$DUPES" = "0" ] && ok "nenhuma regra centralizada duplicada em cliente ($(echo $CENTRAL | wc -w) simbolos)"

if [ "$LIST" = "1" ]; then
  echo "arch-gate: ainda no console (fila de centralizacao)"
  for sym in CoerceChatCalls AnnouncedTools ExtractAssistantToolCalls ExtractAngleCalls TryAddTextCall TryParseArgs \
             StripControlTags ContentHoldBack PartialTagLen BreakerTrip ApplyNudge IsRiskCall RiskScanAnswers \
             RiskConsumeApproval BannedFor BuildChatRequest ToolsToChat ChatToResponses HandleResponsesStream \
             HandleResponsesStreamShim ClientAuthorized; do
    d=$(has_def "$sym" "$CONSOLE")
    [ -n "$d" ] && printf '  - %-28s %s\n' "$sym" "$(echo "$d" | cut -d: -f2)"
  done
fi

if [ "$FAILS" = "0" ]; then echo "arch-gate: OK"; else echo "arch-gate: $FAILS VIOLACAO(OES)"; fi
exit $(( FAILS > 0 ? 1 : 0 ))
