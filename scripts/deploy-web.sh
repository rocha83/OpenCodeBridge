#!/bin/bash
set -euo pipefail
# REPO derivado da localizacao do script (scripts/ -> raiz); override por env.
REPO="${REPO:-$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)}"
cd "$REPO"
dotnet build Rochas.OpenCodeBridge.Web -c Release
dotnet run -c Release --project Rochas.OpenCodeBridge.Web.Test -- --web http://127.0.0.1:${BRIDGE_WEB_PORT:-4130}
# Se testes passam, reinicia systemd/user service ou nohup
