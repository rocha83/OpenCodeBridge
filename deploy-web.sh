#!/bin/bash
set -euo pipefail
cd /media/mint/3686C649614854E6/Projetos/Git/OpenCodeBridge
dotnet build Rochas.OpenCodeBridge.Web -c Release
dotnet run -c Release --project Rochas.OpenCodeBridge.Web.Test -- --web http://127.0.0.1:4130
# Se testes passam, reinicia systemd/user service ou nohup
