using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.Extensions.AI;

/// <summary>
/// Represents a plugin registration that can be used to create AIFunctions.
/// </summary>
public class PluginRegistration
{
    /// <summary>
    /// The type of the plugin class.
    /// </summary>
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)]
    public Type PluginType { get; }

    /// <summary>
    /// Optional instance of the plugin. If null, a new instance will be created.
    /// </summary>
    public object? Instance { get; }

    /// <summary>
    /// Whether this registration uses a pre-created instance.
    /// </summary>
    public bool IsInstance => Instance != null;

    /// <summary>
    /// Optional function filter - if set, only these functions will be registered.
    /// Phase 4.5: Used for selective function registration from skills.
    /// </summary>
    public string[]? FunctionFilter { get; }

    /// <summary>
    /// Factory method to register a plugin by type (will be instantiated when needed).
    /// </summary>
    public static PluginRegistration FromType<T>() where T : class, new()
    {
        return new PluginRegistration(typeof(T), null, null);
    }

    /// <summary>
    /// Factory method to register a plugin by type.
    /// </summary>
    public static PluginRegistration FromType(Type pluginType)
    {
        return new PluginRegistration(pluginType, null, null);
    }

    /// <summary>
    /// Factory method to register a plugin using a pre-created instance.
    /// </summary>
    public static PluginRegistration FromInstance<T>(T instance) where T : class
    {
        if (instance == null) throw new ArgumentNullException(nameof(instance));
        return new PluginRegistration(typeof(T), instance, null);
    }

    /// <summary>
    /// Factory method to register specific functions from a plugin by type.
    /// Phase 4.5: Used for selective function registration from skills.
    /// </summary>
    public static PluginRegistration FromTypeFunctions(Type pluginType, string[] functionNames)
    {
        if (functionNames == null || functionNames.Length == 0)
            throw new ArgumentException("Function names cannot be null or empty", nameof(functionNames));
        return new PluginRegistration(pluginType, null, functionNames);
    }

    /// <summary>
    /// Internal constructor to ensure valid state.
    /// </summary>
    private PluginRegistration(Type pluginType, object? instance, string[]? functionFilter)
    {
        PluginType = pluginType ?? throw new ArgumentNullException(nameof(pluginType));
        Instance = instance;
        FunctionFilter = functionFilter;

        // If instance is provided, validate it matches the type
        if (instance != null && !pluginType.IsInstanceOfType(instance))
        {
            throw new ArgumentException(
                $"Instance type {instance.GetType().Name} does not match plugin type {pluginType.Name}");
        }
    }

    /// <summary>
    /// Creates an instance of the plugin if one is not already provided.
    /// </summary>
    public object GetOrCreateInstance()
    {
        if (Instance != null)
            return Instance;

        try
        {
            return Activator.CreateInstance(PluginType)
                ?? throw new InvalidOperationException($"Failed to create instance of {PluginType.Name}");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to create instance of plugin {PluginType.Name}. " +
                $"Ensure the plugin has a parameterless constructor.", ex);
        }
    }

    /// <summary>
    /// Converts this plugin registration to a list of AIFunctions using the generated registration code.
    /// This is the bridge between the runtime and the compile-time generated code.
    /// </summary>
    [RequiresUnreferencedCode("This method uses reflection to call generated plugin registration code.")]
    public List<AIFunction> ToAIFunctions(IPluginMetadataContext? context = null)
    {
        var instance = GetOrCreateInstance();

        // Use reflection to find and call the generated registration method
        var registrationTypeName = $"{PluginType.Name}Registration";
        var registrationType = PluginType.Assembly.GetType($"{PluginType.Namespace}.{registrationTypeName}");

        if (registrationType == null)
        {
            throw new InvalidOperationException(
                $"Generated registration class {registrationTypeName} not found. " +
                $"Ensure the plugin has been processed by the source generator.");
        }

        var createPluginMethod = registrationType.GetMethod("CreatePlugin",
            BindingFlags.Public | BindingFlags.Static);

        if (createPluginMethod == null)
        {
            throw new InvalidOperationException(
                $"CreatePlugin method not found in {registrationTypeName}. " +
                $"Ensure the source generator ran successfully.");
        }

        try
        {
            // Check parameter count to handle skill-only containers (1 param) vs regular plugins (2 params)
            var parameters = createPluginMethod.GetParameters();
            object? result;
            
            if (parameters.Length == 1)
            {
                // Skill-only container: CreatePlugin(IPluginMetadataContext? context)
                result = createPluginMethod.Invoke(null, new[] { context });
            }
            else
            {
                // Regular plugin: CreatePlugin(TPlugin instance, IPluginMetadataContext? context)
                result = createPluginMethod.Invoke(null, new[] { instance, context });
            }
            
            var allFunctions = result as List<AIFunction> ?? new List<AIFunction>();

            // Phase 4.5: Filter functions if FunctionFilter is set
            if (FunctionFilter != null && FunctionFilter.Length > 0)
            {
                allFunctions = allFunctions
                    .Where(f => FunctionFilter.Contains(f.Name))
                    .ToList();
            }

            return allFunctions;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to create AIFunctions for plugin {PluginType.Name}", ex);
        }
    }
}

/// <summary>
/// Manager for plugin registrations and AIFunction creation.
/// </summary>
public class PluginManager
{
    private readonly List<PluginRegistration> _registrations = new();
    private readonly IPluginMetadataContext? _defaultContext;

    public PluginManager(IPluginMetadataContext? defaultContext = null)
    {
        _defaultContext = defaultContext;
    }

    public PluginManager RegisterPlugin<T>() where T : class, new()
    {
        _registrations.Add(PluginRegistration.FromType<T>());
        return this;
    }

    public PluginManager RegisterPlugin(Type pluginType)
    {
        _registrations.Add(PluginRegistration.FromType(pluginType));
        return this;
    }

    public PluginManager RegisterPlugin<T>(T instance) where T : class
    {
        _registrations.Add(PluginRegistration.FromInstance(instance));
        return this;
    }

    /// <summary>
    /// Registers specific functions from a plugin by type.
    /// Phase 4.5: Used for selective function registration from skills.
    /// </summary>
    public PluginManager RegisterPluginFunctions(Type pluginType, string[] functionNames)
    {
        _registrations.Add(PluginRegistration.FromTypeFunctions(pluginType, functionNames));
        return this;
    }

    /// <summary>
    /// Creates all AIFunctions from registered plugins.
    /// </summary>
    [RequiresUnreferencedCode("This method calls plugin registration methods that use reflection.")]
    public List<AIFunction> CreateAllFunctions(IPluginMetadataContext? context = null)
    {
        var effectiveContext = context ?? _defaultContext;
        var allFunctions = new List<AIFunction>();

        foreach (var registration in _registrations)
        {
            try
            {
                var functions = registration.ToAIFunctions(effectiveContext);
                allFunctions.AddRange(functions);
            }
            catch (Exception ex)
            {
                // Log error but continue with other plugins
                Console.WriteLine($"Failed to create functions for plugin {registration.PluginType.Name}: {ex.Message}");
            }
        }

        return allFunctions;
    }

    public IReadOnlyList<PluginRegistration> GetPluginRegistrations() => _registrations.AsReadOnly();

    public IReadOnlyList<Type> GetRegisteredPluginTypes()
    {
        return _registrations.Select(r => r.PluginType).ToList().AsReadOnly();
    }

    public void Clear()
    {
        _registrations.Clear();
    }
}