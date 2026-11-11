---
name: dotnet-base
description: "Use quando o pedido for criar ou rodar um projeto .NET: console app, .csproj, target framework, dotnet build/run, estrutura de pastas e um projeto na raiz."
---

=== EXEMPLO 0 — comandos base do stack .NET (não copie: leia a dinâmica) ===
```bash
dotnet new console -n App -o . --framework net10.0   # projeto novo: uma vez, na pasta atual
dotnet build                                        # integridade: 0 Erro(s), 0 Aviso(s) novo
dotnet run                                          # saída do programa = critério do enunciado
```
- `.csproj`: `net10.0`; ORM/EF Core 10.x, todas as referências na mesma versão maior.
- `dotnet new` só ao criar projeto novo; enunciado que cita projeto existente nunca roda `dotnet new` (sobrescreve e perde o código).
- Saída com rótulo não vale: um valor por linha, na ordem pedida.
- `.csproj` na raiz e um só; `obj/`/`bin/` não entram em commit nem em pacote.
