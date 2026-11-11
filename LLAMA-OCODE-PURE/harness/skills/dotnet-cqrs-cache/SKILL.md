---
name: dotnet-cqrs-cache
description: "Use quando o pedido envolver CQRS e cache: ICommand/IQuery com handler, IDistributedCache, cache-aside que repopula no miss e invalida na escrita, circuit breaker."
---

=== EXEMPLO 5 — CQRS + cache resiliente (não copie: leia a dinâmica) ===
Command escreve e alimenta o cache; query lê read-through; edição invalida.

```csharp
// Domain/ICustomerQueries.cs — lado de leitura (read model)
public sealed record CustomerDto(Guid Id, string Name, string Document);
public interface ICustomerQueries { Task<CustomerDto?> GetAsync(Guid id, CancellationToken ct = default); }
// Domain/ICacheService.cs — contrato no domínio, implementação na infra
public interface ICacheService {
    Task<T?> GetAsync<T>(string key, CancellationToken ct = default);
    Task SetAsync<T>(string key, T value, TimeSpan ttl, CancellationToken ct = default);
    Task RemoveAsync(string key, CancellationToken ct = default);
}
// Application/CachedCustomerQueries.cs — read-through: miss consulta e repopula
public sealed class CachedCustomerQueries(ICustomerQueries inner, ICacheService cache) : ICustomerQueries {
    static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);
    public async Task<CustomerDto?> GetAsync(Guid id, CancellationToken ct = default) {
        var key = $"customer:{id}";
        var hit = await cache.GetAsync<CustomerDto>(key, ct);
        if (hit is not null) return hit;
        var dto = await inner.GetAsync(id, ct);
        if (dto is not null) await cache.SetAsync(key, dto, Ttl, ct);
        return dto;
    }
}
// Application/CustomerService.cs — escrita alimenta; edição invalida
public async Task<Guid> CreateAsync(string name, string document, CancellationToken ct = default) {
    var customer = Customer.Create(name, document);
    await repo.AddAsync(customer, ct);
    await repo.SaveChangesAsync(ct);
    await cache.SetAsync($"customer:{customer.Id}", new CustomerDto(customer.Id, customer.Name, customer.Document), Ttl, ct);
    return customer.Id;
}
public async Task UpdateNameAsync(Guid id, string name, CancellationToken ct = default) {
    var customer = await repo.GetAsync(id, ct) ?? throw new KeyNotFoundException($"customer {id}");
    customer.Rename(name);
    await repo.SaveChangesAsync(ct);
    await cache.RemoveAsync($"customer:{id}", ct);
}
// Infrastructure/RedisCache.cs — cache fora não derruba o request
public sealed class RedisCache(IDistributedCache cache, ILogger<RedisCache> log) : ICacheService {
    public async Task<T?> GetAsync<T>(string key, CancellationToken ct = default) {
        try {
            var bytes = await cache.GetAsync(key, ct);
            return bytes is null ? default : JsonSerializer.Deserialize<T>(bytes);
        } catch (Exception ex) { log.LogWarning(ex, "cache get {Key}", key); return default; }
    }
    public async Task SetAsync<T>(string key, T value, TimeSpan ttl, CancellationToken ct = default) {
        try {
            await cache.SetAsync(key, JsonSerializer.SerializeToUtf8Bytes(value),
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttl }, ct);
        } catch (Exception ex) { log.LogWarning(ex, "cache set {Key}", key); }
    }
    public async Task RemoveAsync(string key, CancellationToken ct = default) {
        try { await cache.RemoveAsync(key, ct); } catch (Exception ex) { log.LogWarning(ex, "cache del {Key}", key); }
    }
}
```
