using System.Text.Json;
using KnowledgeHub.Contracts;
using KnowledgeHub.Infrastructure;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace KnowledgeHub.Worker;

public sealed class Worker(IServiceScopeFactory scopes, IOptions<RabbitOptions> options, ILogger<Worker> logger) : BackgroundService
{
    private IConnection? connection;
    private IChannel? channel;
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var factory = new ConnectionFactory { HostName = options.Value.Host, UserName = options.Value.User, Password = options.Value.Password, AutomaticRecoveryEnabled = true };
        connection = await factory.CreateConnectionAsync(stoppingToken);
        channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken);
        await channel.QueueDeclareAsync(options.Value.Queue + ".dead", durable: true, exclusive: false, autoDelete: false, cancellationToken: stoppingToken);
        await channel.QueueDeclareAsync(options.Value.Queue, durable: true, exclusive: false, autoDelete: false, arguments: new Dictionary<string, object?> { ["x-dead-letter-exchange"] = "", ["x-dead-letter-routing-key"] = options.Value.Queue + ".dead" }, cancellationToken: stoppingToken);
        await channel.BasicQosAsync(0, 4, false, stoppingToken);
        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += HandleAsync;
        await channel.BasicConsumeAsync(options.Value.Queue, autoAck: false, consumer, stoppingToken);
        await Task.Delay(Timeout.Infinite, stoppingToken);
    }
    private async Task HandleAsync(object sender, BasicDeliverEventArgs args)
    {
        try
        {
            var message = JsonSerializer.Deserialize<JobMessage>(args.Body.Span) ?? throw new InvalidDataException("Invalid job payload.");
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<DocumentProcessor>().ProcessAsync(message.DocumentId, message.TenantId, args.CancellationToken);
            await channel!.BasicAckAsync(args.DeliveryTag, false, args.CancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Document processing failed for delivery {DeliveryTag}", args.DeliveryTag);
            await channel!.BasicNackAsync(args.DeliveryTag, false, requeue: false, args.CancellationToken);
        }
    }
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (channel is not null) await channel.DisposeAsync();
        if (connection is not null) await connection.DisposeAsync();
        await base.StopAsync(cancellationToken);
    }
}
