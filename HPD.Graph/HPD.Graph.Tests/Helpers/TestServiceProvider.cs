using HPDAgent.Graph.Abstractions.Handlers;
using HPDAgent.Graph.Core.Context;
using Microsoft.Extensions.DependencyInjection;

namespace HPD.Graph.Tests.Helpers;

/// <summary>
/// Creates a test service provider with common test handlers registered.
/// </summary>
public static class TestServiceProvider
{
    public static IServiceProvider Create(Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();

        // Register test handlers as IGraphNodeHandler<GraphContext>
        services.AddTransient<IGraphNodeHandler<GraphContext>, SuccessHandler>();
        services.AddTransient<IGraphNodeHandler<GraphContext>, FailureHandler>();
        services.AddTransient<IGraphNodeHandler<GraphContext>, TransientFailureHandler>();
        services.AddTransient<IGraphNodeHandler<GraphContext>, SuspendingHandler>();
        services.AddTransient<IGraphNodeHandler<GraphContext>, EchoHandler>();
        services.AddTransient<IGraphNodeHandler<GraphContext>, CounterHandler>();

        // Register delay handler factory
        services.AddTransient<IGraphNodeHandler<GraphContext>>(sp => new DelayHandler(TimeSpan.FromMilliseconds(100)));

        // Allow custom configuration
        configure?.Invoke(services);

        return services.BuildServiceProvider();
    }

    public static IServiceProvider CreateWithHandler<THandler>(THandler instance)
        where THandler : class, IGraphNodeHandler<GraphContext>
    {
        var services = new ServiceCollection();
        services.AddSingleton<IGraphNodeHandler<GraphContext>>(instance);
        return services.BuildServiceProvider();
    }
}
