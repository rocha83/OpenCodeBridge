# Arquitetura de Camadas — Rochas.AI.Manager

## Regra: Interfaces no Domain
Todas as interfaces (incluindo serviços de aplicação) ficam em `Domain/Interfaces`.
Isso centraliza contratos e especificações no domínio, evitando dependências cruzadas.

## Camadas
- **Domain**: entidades, base classes, interfaces (contratos).
- **Application**: implementação dos serviços (regras de negócio).
- **Infrastructure**: persistência (DapperRepository, AppDb, SQLite).
- **Web**: MVC (views, partials, modals, sidebar).
- **Api**: Web API + Swagger.
- **TrainingEngine**: motor de treinamento (orquestração/monitoramento).

## Compartilhamento
Web e Api compartilham Domain + Application + Infrastructure + TrainingEngine.
Nenhuma regra de negócio duplicada; apenas apresentação distinta.

## Documentos de arquitetura e pendências
- `Rochas.AI.Manager-Arquitetura v1.md` — guideline v1.2 + seção 32 (as-built).
- `Rochas.AI.Manager-Arquitetura v2.md` — arquitetura vigente (baseline, ADRs, roadmap).
- `REPORT-PENDINGS.md` — catálogo de relatórios R1–R30, facetas e recortes De/Até.
