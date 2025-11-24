using System.Text.Json.Serialization.Metadata;

namespace HPD;

/// <summary>
/// Helper class for configuring JSON serialization with AG-UI protocol support.
/// Use this in ASP.NET applications to configure the JSON serializer.
/// </summary>
public static class AGUIJsonSerializerHelper
{
    /// <summary>
    /// Creates a combined JSON type info resolver that includes:
    /// - HPD-Agent core types (HPDJsonContext)
    /// - AG-UI protocol types (AGUIJsonContext)
    ///
    /// Usage in ASP.NET:
    /// <code>
    /// builder.Services.ConfigureHttpJsonOptions(options =>
    /// {
    ///     options.SerializerOptions.TypeInfoResolverChain.Insert(0,
    ///         AGUIJsonSerializerHelper.CreateCombinedResolver(yourAppContext.Default));
    /// });
    /// </code>
    /// 
    /// NOTE: For A2A protocol support, use HPD-Agent.A2A library which provides A2AJsonSerializerContext
    /// </summary>
    /// <param name="additionalResolvers">Optional additional resolvers to include (e.g., your app's JsonSerializerContext)</param>
    /// <returns>Combined type info resolver</returns>
    public static IJsonTypeInfoResolver CreateCombinedResolver(params IJsonTypeInfoResolver[] additionalResolvers)
    {
        var baseResolvers = new IJsonTypeInfoResolver[]
        {
            HPDJsonContext.Default,
            AGUIJsonContext.Default
        };

        // Combine additional resolvers first (so app types take precedence), then library resolvers
        if (additionalResolvers.Length == 0)
        {
            return JsonTypeInfoResolver.Combine(baseResolvers);
        }

        var allResolvers = new IJsonTypeInfoResolver[additionalResolvers.Length + baseResolvers.Length];
        Array.Copy(additionalResolvers, 0, allResolvers, 0, additionalResolvers.Length);
        Array.Copy(baseResolvers, 0, allResolvers, additionalResolvers.Length, baseResolvers.Length);

        return JsonTypeInfoResolver.Combine(allResolvers);
    }

    /// <summary>
    /// Gets the default AG-UI JSON type info resolver without additional app resolvers.
    /// Use CreateCombinedResolver if you have app-specific types to serialize.
    /// </summary>
    public static IJsonTypeInfoResolver Default => CreateCombinedResolver();
}

