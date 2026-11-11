## Formas do stack .NET (contrato, não código: escreva o seu)

Uma linha por capacidade. Isto é assinatura e lugar, não arquivo pronto — o
conteúdo é o do enunciado, com os nomes dele.

- `.csproj`: um só na raiz; começa com `<Project Sdk="Microsoft.NET.Sdk">` e
  `net10.0`; dependência `<PackageReference Include="X" Version="Y" />`;
  projeto que usa outro: `<ProjectReference Include="..\X\X.csproj" />`.
- Console: `class Program` com `static void Main(string[] args)`; top-level
  statements não convivem com `namespace`.
- DDD: entidade com invariante no construtor ou factory
  (`static Result<T> Create(...)`); value object `readonly record struct Cpf`;
  contrato do repositório mora no domínio (`Task<T?> GetByIdAsync(Guid id)`).
- EF: `DbContext` + `DbSet<T>` + `IEntityTypeConfiguration<T>`; transação
  explícita quando o enunciado pedir atomicidade; regra na entidade, não no
  controller.
- Dapper: `using Dapper;` e `IDbConnection` com `QueryAsync<T>(sql, args)`,
  `ExecuteAsync`, `ExecuteScalarAsync<T>`.
- Serviço de aplicação: recebe a interface do repositório, aplica a regra e
  devolve `Result<T>`; HTTP fica na borda (endpoint), nunca no serviço.
- Minimal API: `app.MapGet("/api/x/{id:guid}", async (Guid id, IRepo repo) => …)`
  com `Results.NotFound()`, `Results.Conflict()`, `Results.BadRequest()`,
  `Results.NoContent()`.
- CQRS: `ICommand<T>`/`IQuery<T>` + handler por comando; registro no DI com
  `AddScoped`.
- Cache-aside: `IDistributedCache`; leitura repopula no miss; escrita
  **invalida** a chave (`RemoveAsync`) depois do commit, nunca antes.
- Bus/outbox: tabela `Outbox(Id, MessageId, Payload, PublishedAt)`, publisher em
  `BackgroundService.ExecuteAsync(CancellationToken)`, consumidor idempotente
  por `MessageId` (`ProcessedMessages`), retry com backoff e jitter.
- SQL: `CREATE TABLE t (Id INTEGER PRIMARY KEY, …)` com `UNIQUE`, `CHECK` e
  `FOREIGN KEY … REFERENCES`; `CREATE INDEX ix_t_col ON t(col)`; seeds em
  `INSERT`.
- React 19 + TS: `export default function X(props: Props)`; `useState` para
  estado; `useEffect` com `AbortController` e cleanup; SVG próprio quando o
  enunciado não pedir biblioteca.
- Python: `def f(...)` com laço explícito e `if __name__ == "__main__":`; sem
  placeholder.
- Verificação: `dotnet build <Proj>.csproj` até `0 Erro(s)`; `git commit -m
  "<mensagem pedida>"`; `bash -n arquivo.sh` vale só para `.sh`.
