using System.Text.Json;
using Google.Api.Gax;
using Google.Cloud.PubSub.V1;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shipping.Contracts;

namespace Shipping.Platform;

/// <summary>
/// Transactional outbox row. A service writes its business change AND the event in ONE database transaction;
/// the relay below publishes it afterwards. This is the diagram's "txn.outbox: Events to publish (write + poll)".
/// Delivery is at-least-once, so consumers must be idempotent (status-service dedupes on eventId).
/// </summary>
public class OutboxMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Type { get; set; } = "";
    public string AggregateId { get; set; } = "";
    public string Payload { get; set; } = "";
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }

    public static OutboxMessage Create<T>(string type, string aggregateId, T payload, DateTimeOffset occurredAt) => new()
    {
        Type = type,
        AggregateId = aggregateId,
        Payload = JsonSerializer.Serialize(payload, EventJson.Options),
        OccurredAt = occurredAt,
    };
}

public static class OutboxModel
{
    public static ModelBuilder ConfigureOutbox(this ModelBuilder modelBuilder, string schema)
    {
        modelBuilder.Entity<OutboxMessage>(e =>
        {
            e.ToTable("outbox", schema);
            e.HasKey(x => x.Id);
            e.Property(x => x.Type).HasMaxLength(100).IsRequired();
            e.Property(x => x.AggregateId).HasMaxLength(64).IsRequired();
            e.Property(x => x.Payload).IsRequired();
            e.Property(x => x.LastError).HasMaxLength(1000);
            e.HasIndex(x => x.OccurredAt).HasFilter("\"PublishedAt\" IS NULL");   // the relay's work queue
        });
        return modelBuilder;
    }
}

public sealed class OutboxOptions
{
    public string Schema { get; set; } = "";
    public int BatchSize { get; set; } = 50;
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);
}

public interface IEventPublisher
{
    Task PublishAsync(OutboxMessage message, CancellationToken ct);
}

public static class OutboxRegistration
{
    public static IServiceCollection AddOutboxRelay<TContext>(this IServiceCollection services, string schema)
        where TContext : DbContext
    {
        services.Configure<OutboxOptions>(o => o.Schema = schema);
        services.AddSingleton<IEventPublisher, PubSubEventPublisher>();
        services.AddHostedService<OutboxRelay<TContext>>();
        return services;
    }
}

/// <summary>
/// Polls the outbox table and publishes to Pub/Sub. "FOR UPDATE SKIP LOCKED" lets several replicas
/// run the relay concurrently without publishing the same row twice (crash-after-publish can still
/// duplicate => at-least-once).
/// </summary>
public sealed class OutboxRelay<TContext>(
    IServiceScopeFactory scopes,
    IEventPublisher publisher,
    Microsoft.Extensions.Options.IOptions<OutboxOptions> options,
    ILogger<OutboxRelay<TContext>> log) : BackgroundService where TContext : DbContext
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var published = await DrainOnceAsync(ct);
                if (published == 0) await Task.Delay(options.Value.PollInterval, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Outbox relay iteration failed; backing off");
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }
    }

    private async Task<int> DrainOnceAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        var o = options.Value;

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var sql = $"SELECT * FROM {o.Schema}.outbox WHERE \"PublishedAt\" IS NULL " +
                  $"ORDER BY \"OccurredAt\" LIMIT {o.BatchSize} FOR UPDATE SKIP LOCKED";
        var batch = await db.Set<OutboxMessage>().FromSqlRaw(sql).ToListAsync(ct);

        var published = 0;
        foreach (var message in batch)
        {
            try
            {
                await publisher.PublishAsync(message, ct);
                message.PublishedAt = DateTimeOffset.UtcNow;
                published++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                message.Attempts++;
                message.LastError = ex.Message.Length > 900 ? ex.Message[..900] : ex.Message;
                log.LogWarning(ex, "Publishing outbox message {Id} ({Type}) failed (attempt {Attempts})",
                    message.Id, message.Type, message.Attempts);
            }
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return published;
    }
}

public sealed class PubSubEventPublisher(IConfiguration cfg) : IEventPublisher, IAsyncDisposable
{
    private readonly Lazy<Task<PublisherClient>> _client = new(() =>
    {
        var topic = new TopicName(
            cfg["PubSub:ProjectId"] ?? throw new InvalidOperationException("PubSub:ProjectId is required"),
            cfg["PubSub:Topic"] ?? "transaction-events");

        // EmulatorOrProduction: honours PUBSUB_EMULATOR_HOST locally, uses real Pub/Sub (ADC / Workload Identity) on GKE.
        return new PublisherClientBuilder
        {
            TopicName = topic,
            EmulatorDetection = EmulatorDetection.EmulatorOrProduction,
        }.BuildAsync();
    });

    public async Task PublishAsync(OutboxMessage message, CancellationToken ct)
    {
        var client = await _client.Value;
        var pubsubMessage = new PubsubMessage
        {
            Data = ByteString.CopyFromUtf8(message.Payload),
            Attributes =
            {
                ["eventType"] = message.Type,
                ["eventId"] = message.Id.ToString(),
                ["aggregateId"] = message.AggregateId,
                ["occurredAt"] = message.OccurredAt.ToString("O"),
            },
        };
        await client.PublishAsync(pubsubMessage);   // completes when Pub/Sub has durably accepted the message
    }

    public async ValueTask DisposeAsync()
    {
        if (_client.IsValueCreated)
            await (await _client.Value).ShutdownAsync(TimeSpan.FromSeconds(5));
    }
}

/// <summary>Creates the topic (and optionally the subscription) in the LOCAL emulator. No-op against real Pub/Sub.</summary>
public static class PubSubEmulator
{
    public static async Task EnsureAsync(IConfiguration cfg, bool withSubscription, ILogger log)
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PUBSUB_EMULATOR_HOST"))) return;

        var project = cfg["PubSub:ProjectId"] ?? "local-dev";
        var topic = new TopicName(project, cfg["PubSub:Topic"] ?? "transaction-events");

        var publisherApi = await new PublisherServiceApiClientBuilder
        {
            EmulatorDetection = EmulatorDetection.EmulatorOnly,
        }.BuildAsync();
        try
        {
            await publisherApi.CreateTopicAsync(topic);
            log.LogInformation("Created emulator topic {Topic}", topic);
        }
        catch (RpcException ex) when (ex.StatusCode == Grpc.Core.StatusCode.AlreadyExists) { }

        if (!withSubscription) return;

        var subscriberApi = await new SubscriberServiceApiClientBuilder
        {
            EmulatorDetection = EmulatorDetection.EmulatorOnly,
        }.BuildAsync();
        var subscription = new SubscriptionName(project, cfg["PubSub:Subscription"] ?? "status-service-sub");
        try
        {
            await subscriberApi.CreateSubscriptionAsync(subscription, topic, pushConfig: null, ackDeadlineSeconds: 30);
            log.LogInformation("Created emulator subscription {Subscription}", subscription);
        }
        catch (RpcException ex) when (ex.StatusCode == Grpc.Core.StatusCode.AlreadyExists) { }
    }
}
