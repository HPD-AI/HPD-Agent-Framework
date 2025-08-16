using Microsoft.KernelMemory;
using Microsoft.KernelMemory.Search;
using Microsoft.KernelMemory.Configuration;


/// <summary>
/// Custom extension to register a search client (engine) in the kernel-memory builder.
/// </summary>
public static partial class KernelMemoryBuilderExtensions
{
    public static IKernelMemoryBuilder WithCustomSearchEngine(
        this IKernelMemoryBuilder builder,
        ISearchClient searchClient)
    {
        if (searchClient == null)
        {
            throw new ConfigurationException("Memory Builder: the search client instance is NULL");
        }
        builder.AddSingleton<ISearchClient>(searchClient);
        return builder;
    }
}

