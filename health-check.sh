#!/bin/bash
set -euo pipefail
curl -sf http://127.0.0.1:4130/Account/Login && curl -sf http://127.0.0.1:4130/Chat/Ping?agentId=1 | jq -e '.ok==true'
