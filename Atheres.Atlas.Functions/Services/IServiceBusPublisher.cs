namespace Atheres.Atlas.Functions.Services;

public interface IServiceBusPublisher
{
    Task PublishAsync<T>(string queueName, T message, CancellationToken ct = default)
        where T : class;

    Task PublishBatchAsync<T>(string queueName, IEnumerable<T> messages, CancellationToken ct = default)
        where T : class;
}
