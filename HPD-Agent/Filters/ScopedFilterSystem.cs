using Microsoft.Extensions.AI;

/// <summary>
/// Represents the scope of a filter - what functions it applies to
/// </summary>
public enum FilterScope
{
    /// <summary>Filter applies to all functions globally</summary>
    Global,
    /// <summary>Filter applies to all functions from a specific plugin</summary>
    Plugin,
    /// <summary>Filter applies to a specific function only</summary>
    Function
}

/// <summary>
/// Associates a filter with its scope and target
/// </summary>
public class ScopedFilter
{
    public IAiFunctionFilter Filter { get; }
    public FilterScope Scope { get; }
    public string? Target { get; } // Plugin type name or function name, null for global
    
    public ScopedFilter(IAiFunctionFilter filter, FilterScope scope, string? target = null)
    {
        Filter = filter ?? throw new ArgumentNullException(nameof(filter));
        Scope = scope;
        Target = target;
    }
    
    /// <summary>
    /// Determines if this filter should be applied to the given function
    /// </summary>
    public bool AppliesTo(string functionName, string? pluginTypeName)
    {
        return Scope switch
        {
            FilterScope.Global => true,
            FilterScope.Plugin => !string.IsNullOrEmpty(pluginTypeName) && 
                                 string.Equals(Target, pluginTypeName, StringComparison.Ordinal),
            FilterScope.Function => string.Equals(Target, functionName, StringComparison.Ordinal),
            _ => false
        };
    }
}

/// <summary>
/// Manages the collection of scoped filters and provides methods to retrieve applicable filters
/// </summary>
public class ScopedFilterManager
{
    private readonly List<ScopedFilter> _scopedFilters = new();
    private readonly Dictionary<string, string> _functionToPluginMap = new();
    
    /// <summary>
    /// Adds a filter with the specified scope
    /// </summary>
    public void AddFilter(IAiFunctionFilter filter, FilterScope scope, string? target = null)
    {
        _scopedFilters.Add(new ScopedFilter(filter, scope, target));
    }
    
    /// <summary>
    /// Registers that a function belongs to a specific plugin
    /// </summary>
    public void RegisterFunctionPlugin(string functionName, string pluginTypeName)
    {
        _functionToPluginMap[functionName] = pluginTypeName;
    }
    
    /// <summary>
    /// Gets all filters that apply to the specified function, ordered by scope priority:
    /// 1. Function-specific filters
    /// 2. Plugin-specific filters  
    /// 3. Global filters
    /// </summary>
    public IEnumerable<IAiFunctionFilter> GetApplicableFilters(string functionName, string? pluginTypeName = null)
    {
        // If no plugin type provided, try to look it up
        if (string.IsNullOrEmpty(pluginTypeName))
        {
            _functionToPluginMap.TryGetValue(functionName, out pluginTypeName);
        }
        
        var applicableFilters = _scopedFilters
            .Where(sf => sf.AppliesTo(functionName, pluginTypeName))
            .OrderBy(sf => sf.Scope) // Function(2) -> Plugin(1) -> Global(0)
            .Select(sf => sf.Filter);
            
        return applicableFilters;
    }
    
    /// <summary>
    /// Gets all scoped filters
    /// </summary>
    public IReadOnlyList<ScopedFilter> GetAllScopedFilters() => _scopedFilters.AsReadOnly();
    
    /// <summary>
    /// Gets all global filters that apply to all functions
    /// </summary>
    public List<IAiFunctionFilter> GetGlobalFilters()
    {
        return _scopedFilters
            .Where(sf => sf.Scope == FilterScope.Global)
            .Select(sf => sf.Filter)
            .ToList();
    }
}

/// <summary>
/// Tracks the current context for scoped filter registration in the builder
/// </summary>
internal class BuilderScopeContext
{
    public FilterScope CurrentScope { get; set; } = FilterScope.Global;
    public string? CurrentTarget { get; set; }
    
    public void SetGlobalScope()
    {
        CurrentScope = FilterScope.Global;
        CurrentTarget = null;
    }
    
    public void SetPluginScope(string pluginTypeName)
    {
        CurrentScope = FilterScope.Plugin;
        CurrentTarget = pluginTypeName;
    }
    
    public void SetFunctionScope(string functionName)
    {
        CurrentScope = FilterScope.Function;
        CurrentTarget = functionName;
    }
}
