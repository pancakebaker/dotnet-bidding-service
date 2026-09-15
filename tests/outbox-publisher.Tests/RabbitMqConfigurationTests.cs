using Microsoft.Extensions.Configuration;
using outbox_publisher.Options;

namespace outbox_publisher.Tests;

public sealed class RabbitMqConfigurationTests
{
    [Fact]
    public void ServiceEnvironmentOverridesStandardRabbitMqSection()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RabbitMq:Exchange"] = "generic.exchange",
                ["RabbitMq:Port"] = "5674",
                ["RabbitMq:UserName"] = "generic-user",
                ["RabbitMq:DebugQueue"] = "generic.debug",
                ["OUTBOX_PUBLISHER_RABBITMQ_EXCHANGE"] = "service.exchange",
                ["OUTBOX_PUBLISHER_RABBITMQ_DEBUG_QUEUE"] = "service.debug",
                ["OUTBOX_PUBLISHER_RABBITMQ_PORT"] = "not-a-number",
                ["OUTBOX_PUBLISHER_RABBITMQ_USERNAME"] = string.Empty
            })
            .Build();
        var options = configuration.GetSection(RabbitMqOptions.SectionName)
            .Get<RabbitMqOptions>() ?? new();

        RabbitMqOptions.ApplyEnvironmentOverrides(options, configuration);

        Assert.Equal("service.exchange", options.Exchange);
        Assert.Equal("service.debug", options.DebugQueue);
        Assert.Equal(5674, options.Port);
        Assert.Equal("generic-user", options.UserName);
    }
}