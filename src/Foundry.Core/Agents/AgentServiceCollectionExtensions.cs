using Microsoft.Extensions.DependencyInjection;

namespace Foundry.Core.Agents;

/// <summary>
/// Extension methods for registering Foundry agents with the DI container.
/// </summary>
public static class AgentServiceCollectionExtensions
{
    /// <summary>
    /// Registers <typeparamref name="TAgent"/> as a singleton
    /// <see cref="IAgent"/> in the DI container.
    /// </summary>
    /// <typeparam name="TAgent">
    /// The concrete agent type to register. Must implement
    /// <see cref="IAgent"/> and have a public constructor resolvable by the
    /// container.
    /// </typeparam>
    /// <param name="services">The service collection to add to.</param>
    /// <returns>
    /// The same <paramref name="services"/> instance for chaining.
    /// </returns>
    /// <remarks>
    /// Agents are dispatched in registration order by
    /// <see cref="AgentDispatcher"/>. Register higher-priority agents first.
    /// </remarks>
    public static IServiceCollection AddFoundryAgent<TAgent>(this IServiceCollection services)
        where TAgent : class, IAgent
    {
        services.AddSingleton<IAgent, TAgent>();
        return services;
    }
}
