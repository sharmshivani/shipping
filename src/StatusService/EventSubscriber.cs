using Google.Api.Gax;
using Google.Cloud.PubSub.V1;

namespace Shipping.StatusService;

/// <summary>
/// Pulls from the "status-service-sub" subscription (streaming pull).
///
/// The Cloud Run diagram used a PUSH subscription because Cloud Run scales to zero and needs an HTTPS endpoint.
/// On GKE the pod is long-lived, so PULL is simpler and safer: no public endpoint, no OIDC-verified push
/// route, and the service stays genuinely internal. After 5 failed deliveries Pub/Sub moves a message to
/// transaction-events-dlq (configured on the subscription, see deploy/infra/setup.sh).
/// </summary>
public sealed class EventSubscriber(IServiceScopeFactory scopes, IConfiguration cfg, ILogger<EventSubscriber> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var subscriptionName = new SubscriptionName(
            cfg["PubSub:ProjectId"] ?? throw new InvalidOperationException("PubSub:ProjectId is required"),
            cfg["PubSub:Subscription"] ?? "status-service-sub");

        var subscriber = await new SubscriberClientBuilder
        {
            SubscriptionName = subscriptionName,
            EmulatorDetection = EmulatorDetection.EmulatorOrProduction,
        }.BuildAsync(stoppingToken);

        log.LogInformation("Listening on {Subscription}", subscriptionName);
        var running = subscriber.StartAsync(HandleAsync);

        // Wait for shutdown (or for the subscriber to die, which should crash the pod so Kubernetes restarts it).
        await Task.WhenAny(running, Task.Delay(Timeout.Infinite, stoppingToken));

        await subscriber.StopAsync(TimeSpan.FromSeconds(15));
        await running;
    }

    private async Task<SubscriberClient.Reply> HandleAsync(PubsubMessage message, CancellationToken ct)
    {
        try
        {
            message.Attributes.TryGetValue("eventType", out var eventType);
            message.Attributes.TryGetValue("eventId", out var eventId);
            if (string.IsNullOrEmpty(eventType) || string.IsNullOrEmpty(eventId))
                throw new InvalidDataException("Message is missing eventType/eventId attributes");

            await using var scope = scopes.CreateAsyncScope();
            var projector = scope.ServiceProvider.GetRequiredService<StatusProjector>();
            await projector.ApplyAsync(eventType, eventId, message.Data.ToStringUtf8(), ct);
            return SubscriberClient.Reply.Ack;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Failed to process message {MessageId}; it will be redelivered (then dead-lettered)", message.MessageId);
            return SubscriberClient.Reply.Nack;
        }
    }
}
