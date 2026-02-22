using HPD.Agent.AspNetCore.DependencyInjection;
using HPD.Agent.AspNetCore.EndpointMapping.Endpoints;
using HPD.Agent.AspNetCore.Lifecycle;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace HPD.Agent.AspNetCore;

/// <summary>
/// Extension methods for mapping HPD Agent API endpoints.
/// </summary>
public static class HPDAgentEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps all HPD-Agent API endpoints for the default (unnamed) agent.
    /// </summary>
    public static RouteGroupBuilder MapHPDAgentApi(
        this IEndpointRouteBuilder endpoints)
        => endpoints.MapHPDAgentApi(Options.DefaultName);

    /// <summary>
    /// Maps all HPD-Agent API endpoints for a named agent.
    /// The name must match a previous AddHPDAgent(name, ...) registration.
    /// </summary>
    /// <returns>RouteGroupBuilder for further customization</returns>
    /// <remarks>
    /// Maps 20+ endpoints:
    /// - Session CRUD (Create, Search/List, Get, Update, Delete)
    /// - Branch CRUD (List, Get, Create, Fork, Delete, Messages, Siblings)
    /// - Asset management (Upload, Download, List, Delete)
    /// - Streaming (SSE + WebSocket)
    /// - Middleware responses (Permissions, Client Tools)
    /// </remarks>
    public static RouteGroupBuilder MapHPDAgentApi(
        this IEndpointRouteBuilder endpoints,
        string name)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(name);
        // Allow empty string for Options.DefaultName

        var routeGroup = endpoints.MapGroup("");

        // Resolve the named AgentSessionManager for this agent
        var registry = endpoints.ServiceProvider.GetRequiredService<AgentSessionManagerRegistry>();
        var baseManager = registry.Get(name);

        // Cast to AspNetCoreSessionManager (registry returns this type internally)
        if (baseManager is not AspNetCoreSessionManager manager)
        {
            throw new InvalidOperationException(
                $"Manager for agent '{name}' is not an AspNetCoreSessionManager");
        }

        // Map all endpoint groups
        SessionEndpoints.Map(routeGroup, manager);
        BranchEndpoints.Map(routeGroup, manager);
        AssetEndpoints.Map(routeGroup, manager);
        StreamingEndpoints.Map(routeGroup, manager);
        MiddlewareResponseEndpoints.Map(routeGroup, manager);

        return routeGroup;
    }
}
