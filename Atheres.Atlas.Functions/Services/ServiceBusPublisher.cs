using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;

namespace Atheres.Atlas.Functions.Services;

public class ServiceBusPublisher : IServiceBusPublisher, IAsyncDisposable
{
    private readonly ServiceBusClient _client;
    private readonly ILogger<ServiceBusPublisher> _logger;
    private readonly Dictionary<string, ServiceBusSender> _senders = new();

    public ServiceBusPublisher(ServiceBusClient client, ILogger<ServiceBusPublisher> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task PublishAsync<T>(string queueName, T message, CancellationToken ct = default)
        where T : class
    {
        var sender = GetSender(queueName);
        var json = JsonSerializer.Serialize(message);
        var sbMessage = new ServiceBusMessage(json)
        {
            ContentType = "application/json",
            Subject = typeof(T).Name
        };

        await sender.SendMessageAsync(sbMessage, ct);
        _logger.LogDebug("Published {MessageType} to {Queue}", typeof(T).Name, queueName);
    }

    public async Task PublishBatchAsync<T>(string queueName, IEnumerable<T> messages, CancellationToken ct = default)
        where T : class
    {
        var sender = GetSender(queueName);
        var batch = await sender.CreateMessageBatchAsync(ct);

        foreach (var message in messages)
        {
            var json = JsonSerializer.Serialize(message);
            var sbMessage = new ServiceBusMessage(json)
            {
                ContentType = "application/json",
                Subject = typeof(T).Name
            };

            if (!batch.TryAddMessage(sbMessage))
            {
                // Batch full — send current batch and start a new one
                await sender.SendMessagesAsync(batch, ct);
                batch = await sender.CreateMessageBatchAsync(ct);
                batch.TryAddMessage(sbMessage);
            }
        }

        if (batch.Count > 0)
            await sender.SendMessagesAsync(batch, ct);

        _logger.LogDebug("Published batch of {MessageType} to {Queue}", typeof(T).Name, queueName);
    }

    private ServiceBusSender GetSender(string queueName)
    {
        if (!_senders.TryGetValue(queueName, out var sender))
        {
            sender = _client.CreateSender(queueName);
            _senders[queueName] = sender;
        }
        return sender;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var sender in _senders.Values)
            await sender.DisposeAsync();
        await _client.DisposeAsync();
    }
}
