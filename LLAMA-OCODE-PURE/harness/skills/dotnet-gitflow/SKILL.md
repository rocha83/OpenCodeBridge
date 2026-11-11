---
name: dotnet-gitflow
description: "Use quando o pedido envolver git sobre projeto existente: init, add, commit com mensagem específica, log, branch e identidade sem mexer na config global."
---

=== EXEMPLO 2 — git flow sobre projeto existente (não copie: leia a dinâmica) ===
Repositório existente não é reinicializado; a mudança entra no domínio.

```bash
git init -b main
git add -A
git commit -m "feat: console ddd with ef and generic repository"
# alteração: nova invariante em Domain/Customer.Create
git add -A
git commit -m "feat: validate document in domain"
git log --format="%h %s" > gitlog.txt
git log --oneline | head -2
```

Identidade sem mexer na config global:
`git -c user.name=dev -c user.email=dev@local commit -m "..."`.
O segundo commit só existe se houver mudança real de arquivo: `git status --short` antes.
