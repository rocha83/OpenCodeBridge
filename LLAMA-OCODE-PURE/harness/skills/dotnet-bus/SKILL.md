---
name: dotnet-bus
description: "Use quando o pedido envolver mensageria: publicar/consumir evento em tópico, outbox transacional, retry com backoff e jitter, dead-letter e consumidor idempotente por message-id."
---

=== EXEMPLO 6 — bus em tópicos (publisher + consumer) (não copie: leia a dinâmica) ===
Fluxo separado: contrato no domínio, transporte na infra, consumo idempotente.

```csharp
// Domain/IEventBus.cs + CustomerCreated.cs
public interface IEventBus { Task PublishAsync<T>(string topic, T message, CancellationToken ct = default) where T : class; }
public sealed record CustomerCreated(Guid Id, string Name, DateTimeOffset OccurredAt);
// Application/CustomerService.cs — publica depois do commit, nunca antes
await repo.SaveChangesAsync(ct);
await bus.PublishAsync("customer.created", new CustomerCreated(customer.Id, customer.Name, DateTimeOffset.UtcNow), ct);
// Infrastructure/RabbitEventBus.cs — exchange topic + routing key
public sealed class RabbitEventBus(IConnection connection, ILogger<RabbitEventBus> log) : IEventBus {
    public async Task PublishAsync<T>(string topic, T message, CancellationToken ct = default) where T : class {
        await using var channel = await connection.CreateChannelAsync(cancellationToken: ct);
        await channel.ExchangeDeclareAsync("events", ExchangeType.Topic, durable: true, cancellationToken: ct);
        await channel.BasicPublishAsync("events", topic, JsonSerializer.SerializeToUtf8Bytes(message), ct);
        log.LogInformation("published {Topic}", topic);
    }
}
// Infrastructure/CustomerCreatedConsumer.cs — retry com backoff + idempotência
public sealed class CustomerCreatedConsumer(IConnection connection, ICacheService cache,
    ILogger<CustomerCreatedConsumer> log) : BackgroundService {
    const int MaxTentativas = 5;
    protected override async Task ExecuteAsync(CancellationToken ct) {
        var channel = await connection.CreateChannelAsync(cancellationToken: ct);
        await channel.ExchangeDeclareAsync("events", ExchangeType.Topic, durable: true, cancellationToken: ct);
        var queue = await channel.QueueDeclareAsync("customer-cache", durable: true, exclusive: false, autoDelete: false, cancellationToken: ct);
        await channel.QueueBindAsync(queue, "events", "customer.*", cancellationToken: ct);
        await foreach (var message in channel.ConsumeAsync<CustomerCreated>(queue, ct)) {
            for (var attempt = 1; ; attempt++) {
                try {
                    if (await cache.GetAsync<CustomerCreated>($"msg:{message.Id}", ct) is not null) {
                        await channel.BasicAckAsync(message.DeliveryTag, false, ct);   // já processado
                        break;
                    }
                    await cache.RemoveAsync($"customer:{message.Id}", ct);
                    await cache.SetAsync($"msg:{message.Id}", message, TimeSpan.FromDays(7), ct);
                    await channel.BasicAckAsync(message.DeliveryTag, false, ct);
                    break;
                } catch (Exception ex) when (attempt < MaxTentativas) {
                    log.LogWarning(ex, "retry {Attempt} customer.created", attempt);
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct);
                } catch (Exception ex) {
                    log.LogError(ex, "dead-letter customer.created");
                    await channel.BasicNackAsync(message.DeliveryTag, false, requeue: false, ct);
                    break;
                }
            }
        }
    }
}
// Program.cs
builder.Services.AddSingleton<IConnection>(_ => ConnectionFactory.CreateConnection(builder.Configuration.GetConnectionString("Bus")));
builder.Services.AddSingleton<IEventBus, RabbitEventBus>();
builder.Services.AddHostedService<CustomerCreatedConsumer>();
```
