---
name: dotnet-api
description: "Use quando o pedido envolver API REST em ASP.NET Core: Minimal API, MapGet/MapPost/MapPut/MapDelete, DTO, paginação, validação e status 400/404/409/204."
---

=== EXEMPLO 4 — micro API (minimal API) + guideline Microsoft (não copie: leia a dinâmica) ===
```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDbContext<AppDbContext>(o => o.UseSqlite(builder.Configuration.GetConnectionString("App")));
builder.Services.AddScoped(typeof(IRepository<>), typeof(Repository<>));
builder.Services.AddScoped<ICustomerService, CustomerService>();
builder.Services.AddProblemDetails();
var app = builder.Build();
app.MapPost("/customers", async (ICustomerService service, CustomerRequest request, CancellationToken ct) => {
    var id = await service.CreateAsync(request.Name, request.Document, ct);
    return Results.Created($"/customers/{id}", id);
});
app.MapGet("/customers/{id:guid}", async (Guid id, IRepository<Customer> repo, CancellationToken ct) =>
    await repo.GetAsync(id, ct) is { } c ? Results.Ok(new CustomerResponse(c.Id, c.Name)) : Results.NotFound());
app.Run();
public sealed record CustomerRequest(string Name, string Document);
public sealed record CustomerResponse(Guid Id, string Name);
```

Guideline que vale em qualquer borda:
- `async` em todo I/O; nunca `.Result`/`.Wait()`.
- Ciclo de vida correto no DI: `DbContext` e repositório `Scoped`.
- Options pattern para config; nenhuma connection string literal.
- `ILogger` injetado; `Console` só na saída do programa.
- Validação e regra no domínio; o endpoint só traduz HTTP.
- DTO na borda; nunca devolver entidade de domínio.
- Erro em ProblemDetails, sem stack trace no corpo.
- Migração explícita: `dotnet ef migrations add <nome>` + `dotnet ef database update`.
- `.csproj`: `<FrameworkReference Include="Microsoft.AspNetCore.App" />` (ASP.NET é framework, não pacote).
