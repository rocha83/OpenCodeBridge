#!/usr/bin/env bash
set -euo pipefail
ROOT="${ROOT:-$(cd "$(dirname "$0")/.." && pwd)}"
# Behavior e tools vem do C# (HarnessTails.DefaultBehavior/DefaultTools).
# Nao existe mais override de texto em docs/: o script nao duplica o harness.
exec dotnet run --project "$ROOT/Rochas.OpenCodeBridge" -- \
  --passthrough-responses true \
  --legacy-harness 0 \
  --anti-repeat 1 --anti-repeat-window 512
