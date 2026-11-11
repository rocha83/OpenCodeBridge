# Belts: estabilizacao e limites dos direcionamentos

Data: 2026-11-10

## O que foi ajustado
- `P_AZUL` passou a declarar o aceite: um `.csproj` na raiz, fontes `.cs` reais, DDL quando
  houver banco e `dotnet build` ate `rc=0`.
- Harness: direcionamento quando falta `.csproj` na raiz e direcionamento quando o build falha, reenviando o
  erro do compilador (ex.: CS8954) e a tabela de exemplos otimistas; sem revelar resultado
  esperado do caso.

## Limite
Direcionamento e rede de seguranca, nao substituto do system prompt + enunciado + tabela de erros.
Pode mascarar enunciado incompleto. Proximo teste: cenarios independentes de criacao de
`.csproj` (workspace vazio, class library, projeto existente, projeto aninhado, dois
projetos na raiz) devem passar sem direcionamento.

## Execucao
Matriz 10 belts em `/tmp/opencode/llama-pure/belts-stabilize.sh`; tabela em
`metrics/belts-stabilize.tsv` e log em `belts-stabilize.log`. Resultados serao anexados
quando a execucao terminar.
