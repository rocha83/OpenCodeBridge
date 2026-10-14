#!/bin/bash
set -euo pipefail
PORT="${BRIDGE_WEB_PORT:-4130}"
curl -sf http://127.0.0.1:$PORT/Account/Login && curl -sf http://127.0.0.1:$PORT/Chat/Ping?agentId=1 | jq -e '.ok==true'
