---
name: dotnet-ddd-ef
description: "Use quando o pedido envolver DDD com .NET e banco relacional: entidade com invariantes, value object, agregado, DbContext/EF Core, mapeamento e repositório genérico (GetByIdAsync, Add, Update)."
---

=== EXEMPLO 1 — DDD + EF + repositório genérico (não copie: leia a dinâmica) ===
Contratos no domínio; implementação na infra; regra mora no domínio.
`.csproj`: `net10.0` + `Microsoft.EntityFrameworkCore.Sqlite 10.x`.

```csharp
// Domain/Customer.cs — invariantes no domínio
public sealed class Customer {
    public Guid Id { get; private init; }
    public string Name { get; private set; } = "";
    public string Document { get; private set; } = "";
    public static Customer Create(string name, string document) {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("name");
        if (!CpfCnpj.IsValid(document)) throw new ArgumentException("document");
        return new Customer { Id = Guid.NewGuid(), Name = name.Trim(), Document = document.Trim() };
    }
}
// Domain/IRepository.cs e ICustomerService.cs — contratos no domínio
public interface IRepository<T> where T : class {
    Task<T?> GetAsync(Guid id, CancellationToken ct = default);
    Task AddAsync(T entity, CancellationToken ct = default);
    Task<int> SaveChangesAsync(CancellationToken ct = default);
}
public interface ICustomerService { Task<Guid> CreateAsync(string name, string document, CancellationToken ct = default); }
// Application/CustomerService.cs — orquestra; regra fica no domínio
public sealed class CustomerService(IRepository<Customer> repository) : ICustomerService {
    public async Task<Guid> CreateAsync(string name, string document, CancellationToken ct = default) {
        var customer = Customer.Create(name, document);
        await repository.AddAsync(customer, ct);
        await repository.SaveChangesAsync(ct);
        return customer.Id;
    }
}
// Infrastructure/AppDbContext.cs + Repository.cs
public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options) {
    public DbSet<Customer> Customers => Set<Customer>();
    protected override void OnModelCreating(ModelBuilder b) => b.Entity<Customer>().HasIndex(c => c.Document).IsUnique();
}
public sealed class Repository<T>(AppDbContext db) : IRepository<T> where T : class {
    public async Task<T?> GetAsync(Guid id, CancellationToken ct = default) => await db.Set<T>().FindAsync([id], ct);
    public async Task AddAsync(T entity, CancellationToken ct = default) => await db.Set<T>().AddAsync(entity, ct);
    public Task<int> SaveChangesAsync(CancellationToken ct = default) => db.SaveChangesAsync(ct);
}
// Program.cs — composição (console)
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddDbContext<AppDbContext>(o => o.UseSqlite("Data Source=app.db"));
builder.Services.AddScoped(typeof(IRepository<>), typeof(Repository<>));
builder.Services.AddScoped<ICustomerService, CustomerService>();
using var app = builder.Build();
var service = app.Services.CreateScope().ServiceProvider.GetRequiredService<ICustomerService>();
Console.WriteLine(await service.CreateAsync("Maria", "52998224725"));
```
