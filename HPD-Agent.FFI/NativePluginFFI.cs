using System;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Collections.Generic;

namespace HPD_Agent.FFI
{
    /// <summary>
    /// Language-agnostic FFI bindings for external plugin systems.
    /// Supports any language that exports C-compatible functions (Rust, C++, Zig, Go, Swift, etc.)
    ///
    /// Protocol: JSON over C ABI
    /// - All data exchanged as JSON strings via pointers
    /// - Standard C calling convention (cdecl)
    /// - Memory management: caller allocates, callee frees via free_string()
    ///
    /// Compatible Languages:
    /// - Rust (with #[no_mangle] extern "C")
    /// - C/C++ (with extern "C")
    /// - Zig (with export fn)
    /// - Go (with //export via CGO)
    /// - Swift (with @_cdecl)
    /// - Python (with ctypes/cffi)
    /// - Node.js (with Node-API/NAPI)
    /// </summary>
    public static class NativePluginFFI
    {
        //    
        // CONFIGURATION: Native library name
        //
        // Customize per platform/language:
        // - Rust:   "hpd_rust_agent" or "libhpd_rust_agent.so"
        // - C++:    "hpd_cpp_plugins" or "hpd_cpp_plugins.dll"
        // - Zig:    "hpd_zig_plugins"
        // - Go:     "hpd_go_plugins"
        // - Swift:  "hpd_swift_plugins"
        // - Multi:  "hpd_native_plugins" (any language)
        //    
        private const string LibraryName = "hpd_native_plugins";

        //    
        // FFI IMPORTS: C ABI functions (language-agnostic)
        //
        // Any language can implement these by exporting C-compatible symbols.
        // All functions use JSON for data exchange to ensure language neutrality.
        //    

        /// <summary>
        /// Get plugin registry as JSON string.
        /// Native signature: const char* get_plugin_registry()
        /// </summary>
        [DllImport(LibraryName, EntryPoint = "get_plugin_registry", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr GetPluginRegistryNative();

        /// <summary>
        /// Get plugin schemas as JSON string.
        /// Native signature: const char* get_plugin_schemas()
        /// </summary>
        [DllImport(LibraryName, EntryPoint = "get_plugin_schemas", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr GetPluginSchemasNative();

        /// <summary>
        /// Get plugin statistics as JSON string.
        /// Native signature: const char* get_plugin_stats()
        /// </summary>
        [DllImport(LibraryName, EntryPoint = "get_plugin_stats", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr GetPluginStatsNative();

        /// <summary>
        /// Get list of function names as JSON array.
        /// Native signature: const char* get_function_list()
        /// </summary>
        [DllImport(LibraryName, EntryPoint = "get_function_list", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr GetFunctionListNative();

        /// <summary>
        /// Execute a plugin function with JSON arguments, returns JSON result.
        /// Native signature: const char* execute_plugin_function(const char* function_name, const char* args_json)
        /// </summary>
        [DllImport(LibraryName, EntryPoint = "execute_plugin_function", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr ExecutePluginFunctionNative(
            [MarshalAs(UnmanagedType.LPStr)] string functionName,
            [MarshalAs(UnmanagedType.LPStr)] string argsJson);

        /// <summary>
        /// Free a string allocated by the native plugin runtime.
        /// Native signature: void free_string(char* ptr)
        /// </summary>
        [DllImport(LibraryName, EntryPoint = "free_string", CallingConvention = CallingConvention.Cdecl)]
        private static extern void FreeStringNative(IntPtr ptr);

        /// <summary>
        /// Register plugin executors in the native runtime.
        /// Native signature: bool register_plugin_executors(const char* plugin_name)
        /// </summary>
        [DllImport(LibraryName, EntryPoint = "register_plugin_executors", CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool RegisterPluginExecutorsNative(
            [MarshalAs(UnmanagedType.LPStr)] string pluginName);

        //    
        // PUBLIC API: Language-agnostic wrapper methods
        //    

        /// <summary>
        /// Register plugin executors in the native plugin runtime.
        /// This MUST be called after loading plugin info to populate the function registry.
        /// Works with any language that implements the C ABI.
        /// </summary>
        /// <param name="pluginName">Name of the plugin to register</param>
        /// <returns>True if registration succeeded</returns>
        public static bool RegisterPluginExecutors(string pluginName)
        {
            return RegisterPluginExecutorsNative(pluginName);
        }

        /// <summary>
        /// Get all registered plugins from the native runtime.
        /// Returns JSON data from Rust, C++, Zig, Go, Swift, or any C-compatible plugin system.
        /// </summary>
        /// <returns>Plugin registry containing all registered plugins</returns>
        public static PluginRegistry GetPluginRegistry()
        {
            var ptr = GetPluginRegistryNative();
            if (ptr == IntPtr.Zero)
                return new PluginRegistry { Plugins = new List<PluginInfo>() };

            try
            {
                var json = Marshal.PtrToStringAnsi(ptr);
                if (string.IsNullOrEmpty(json))
                    return new PluginRegistry { Plugins = new List<PluginInfo>() };

                return JsonSerializer.Deserialize(json, HPDFFIJsonContext.Default.PluginRegistry) ??
                       new PluginRegistry { Plugins = new List<PluginInfo>() };
            }
            finally
            {
                FreeStringNative(ptr);
            }
        }

        /// <summary>
        /// Get all function schemas as a JSON object.
        /// Schemas describe function parameters, return types, and documentation.
        /// </summary>
        /// <returns>JSON document containing all function schemas</returns>
        public static JsonDocument GetPluginSchemas()
        {
            var ptr = GetPluginSchemasNative();
            if (ptr == IntPtr.Zero)
                return JsonDocument.Parse("{}");

            try
            {
                var json = Marshal.PtrToStringAnsi(ptr);
                return JsonDocument.Parse(json ?? "{}");
            }
            finally
            {
                FreeStringNative(ptr);
            }
        }

        /// <summary>
        /// Get plugin statistics (counts, performance metrics, etc.).
        /// </summary>
        /// <returns>Plugin statistics from the native runtime</returns>
        public static PluginStats GetPluginStats()
        {
            var ptr = GetPluginStatsNative();
            if (ptr == IntPtr.Zero)
                return new PluginStats();

            try
            {
                var json = Marshal.PtrToStringAnsi(ptr);
                if (string.IsNullOrEmpty(json))
                    return new PluginStats();

                return JsonSerializer.Deserialize(json, HPDFFIJsonContext.Default.PluginStats) ?? new PluginStats();
            }
            finally
            {
                FreeStringNative(ptr);
            }
        }

        /// <summary>
        /// Get list of all available function names from the native runtime.
        /// </summary>
        /// <returns>List of function names</returns>
        public static List<string> GetFunctionList()
        {
            var ptr = GetFunctionListNative();
            if (ptr == IntPtr.Zero)
                return new List<string>();

            try
            {
                var json = Marshal.PtrToStringAnsi(ptr);
                if (string.IsNullOrEmpty(json))
                    return new List<string>();

                return JsonSerializer.Deserialize(json, HPDFFIJsonContext.Default.ListString) ?? new List<string>();
            }
            finally
            {
                FreeStringNative(ptr);
            }
        }

        /// <summary>
        /// Execute a plugin function in the native runtime.
        /// Communicates via JSON - works with any language.
        /// </summary>
        /// <param name="functionName">Name of the function to execute</param>
        /// <param name="arguments">Function arguments as a dictionary (will be serialized to JSON)</param>
        /// <returns>Execution result containing success status, result data, or error message</returns>
        public static PluginExecutionResult ExecuteFunction(string functionName, Dictionary<string, object> arguments)
        {
            var argsJson = JsonSerializer.Serialize(arguments, HPDFFIJsonContext.Default.DictionaryStringObject);
            var ptr = ExecutePluginFunctionNative(functionName, argsJson);

            if (ptr == IntPtr.Zero)
            {
                return new PluginExecutionResult
                {
                    Success = false,
                    Error = "Failed to execute function"
                };
            }

            try
            {
                var json = Marshal.PtrToStringAnsi(ptr);
                if (string.IsNullOrEmpty(json))
                {
                    return new PluginExecutionResult
                    {
                        Success = false,
                        Error = "Empty response from function"
                    };
                }

                var response = JsonDocument.Parse(json);
                return new PluginExecutionResult
                {
                    Success = true,
                    Result = response
                };
            }
            catch (Exception ex)
            {
                return new PluginExecutionResult
                {
                    Success = false,
                    Error = ex.Message
                };
            }
            finally
            {
                FreeStringNative(ptr);
            }
        }
    }

    //    
    // LANGUAGE-AGNOSTIC DATA STRUCTURES
    //
    // These work with JSON from any language that can serialize to JSON.
    // The structures are designed to be simple and portable across language boundaries.
    //    

    /// <summary>
    /// Plugin registry information from native runtime.
    /// Language-agnostic: works with JSON from Rust, C++, Zig, Go, Swift, etc.
    /// </summary>
    public class PluginRegistry
    {
        public List<PluginInfo> Plugins { get; set; } = new();
    }

    /// <summary>
    /// Information about a single plugin.
    /// </summary>
    public class PluginInfo
    {
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public List<FunctionInfo> Functions { get; set; } = new();
    }

    /// <summary>
    /// Information about a plugin function.
    /// </summary>
    public class FunctionInfo
    {
        public string Name { get; set; } = string.Empty;
        public string Wrapper { get; set; } = string.Empty;
    }

    /// <summary>
    /// Plugin statistics from native runtime.
    /// </summary>
    public class PluginStats
    {
        public int TotalPlugins { get; set; }
        public int TotalFunctions { get; set; }
        public List<PluginSummary> Plugins { get; set; } = new();
    }

    /// <summary>
    /// Summary information about a plugin.
    /// </summary>
    public class PluginSummary
    {
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public int FunctionCount { get; set; }
    }

    /// <summary>
    /// Result of executing a plugin function.
    /// Language-agnostic: success/error pattern works across all languages.
    /// </summary>
    public class PluginExecutionResult
    {
        public bool Success { get; set; }
        public JsonDocument? Result { get; set; }
        public string? Error { get; set; }
    }
}
