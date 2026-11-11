---
name: dotnet-aggregate
description: "Use quando o pedido envolver entidade composta header+details: aggregate root com coleção, OwnsMany, transação com BeginTransaction/Commit/Rollback e rollback total em falha de item."
---

=== EXEMPLO 9 — entidade composta (header + details) (não copie: leia a dinâmica) ===

```csharp
// Domain/Order.cs — aggregate root: o detalhe só entra e sai pelo header
public sealed class Order {
    private readonly List<OrderItem> _items = [];
    public Guid Id { get; private init; }
    public Guid CustomerId { get; private init; }
    public DateTimeOffset CreatedAt { get; private init; }
    public IReadOnlyList<OrderItem> Items => _items;
    public decimal Total => _items.Sum(i => i.LineTotal);

    public static Order Create(Guid customerId) =>
        new() { Id = Guid.NewGuid(), CustomerId = customerId, CreatedAt = DateTimeOffset.UtcNow };

    public void AddItem(Guid productId, int quantity, decimal unitPrice) {
        if (quantity <= 0) throw new ArgumentException("quantity");
        if (unitPrice <= 0) throw new ArgumentException("unitPrice");
        if (_items.Any(i => i.ProductId == productId)) throw new InvalidOperationException("duplicate product");
        _items.Add(new OrderItem(productId, quantity, unitPrice));
    }
    public void RemoveItem(Guid productId) {
        if (_items.RemoveAll(i => i.ProductId == productId) == 0)
            throw new InvalidOperationException("item not found");
    }
}
// Domain/OrderItem.cs — detalhe sem identidade fora do agregado
public sealed class OrderItem {
    public Guid ProductId { get; private init; }
    public int Quantity { get; private init; }
    public decimal UnitPrice { get; private init; }
    public decimal LineTotal => Quantity * UnitPrice;
    internal OrderItem(Guid productId, int quantity, decimal unitPrice) {
        ProductId = productId; Quantity = quantity; UnitPrice = unitPrice;
    }
}
// Infrastructure/AppDbContext.cs — 1-N, chave composta e cascade
public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options) {
    public DbSet<Order> Orders => Set<Order>();
    protected override void OnModelCreating(ModelBuilder b) {
        var order = b.Entity<Order>();
        order.HasKey(o => o.Id);
        order.OwnsMany(o => o.Items, item => {
            item.ToTable("OrderItems");
            item.WithOwner().HasForeignKey("OrderId");
            item.HasKey("OrderId", nameof(OrderItem.ProductId));
            item.Property(i => i.UnitPrice).HasPrecision(18, 2);
        });
        order.Navigation(o => o.Items).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
// Application/OrderService.cs — header + detalhes em UMA transação
public async Task<Guid> CreateAsync(OrderRequest request, CancellationToken ct = default) {
    var order = Order.Create(request.CustomerId);
    foreach (var item in request.Items) order.AddItem(item.ProductId, item.Quantity, item.UnitPrice);
    await using var tx = await db.Database.BeginTransactionAsync(ct);
    db.Orders.Add(order);
    await db.SaveChangesAsync(ct);
    await tx.CommitAsync(ct);
    return order.Id;
}
// Application/OrderDto.cs — DTO aninhado: detalhe dentro do header
public sealed record OrderItemDto(Guid ProductId, int Quantity, decimal UnitPrice, decimal LineTotal);
public sealed record OrderDto(Guid Id, Guid CustomerId, decimal Total, IReadOnlyList<OrderItemDto> Items);
```

Regras: `Total` é derivado (não é coluna gravada); detalhe não tem `DbSet` próprio; item repetido é erro de domínio, não de banco.
