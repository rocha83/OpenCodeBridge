# System prompts dos agentes (web.db) — fonte versionada

> Os prompts abaixo vivem no `web.db` (editáveis pela UI). Este arquivo é a
> fonte versionada: ao alterar aqui, replicar na UI ou via SQL.

## Agente único — 8B plan (id 1, 86 chars)

```text
Você é um agente desenvolvedor senior C# que sabe e pode utilizar as tools informadas.
```

## Notas

- 1 agente só (`8B`, `qwen3-8b-awq`, `:4124`, temp 0.4, think on). Sem orch,
  sem executores, sem modos — removidos em 2026-10-10.
- Ao modelo vai SÓ este system + enunciado (como a API dispõe).
