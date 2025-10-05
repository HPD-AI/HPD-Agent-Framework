using System;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Collections.Generic;

namespace HPD_Agent.FFI
{
    /// <summary>
    /// FFI bindings for the Rust plugin system
    /// </summary>
    public static class RustPluginFFI
    {
        private const string LibraryName = "hpd_rust_agent";

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr rust_get_plugin_registry();

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr rust_get_plugin_schemas();

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr rust_get_plugin_stats();

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr rust_get_function_list();

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr rust_execute_plugin_function(
            [MarshalAs(UnmanagedType.LPStr)] string functionName,
            [MarshalAs(UnmanagedType.LPStr)] string argsJson);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        private static extern void rust_free_string(IntPtr ptr);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool rust_register_plugin_executors(
            [MarshalAs(UnmanagedType.LPStr)] string pluginName);

        /// <summary>
        /// Register plugin executors on the Rust side
        /// This MUST be called after loading plugin info to populate the function registry
        /// </summary>
        public static bool RegisterPluginExecutors(string pluginName)
        {
            return rust_register_plugin_executors(pluginName);
        }

        /// <summary>
        /// Get all registered plugins information
        /// </summary>
        public static PluginRegistry GetPluginRegistry()
        {
            var ptr = rust_get_plugin_registry();
            if (ptr == IntPtr.Zero)
                return new PluginRegistry { Plugins = new List<PluginInfo>() };

            try
            {
                var json = Marshal.PtrToStringAnsi(ptr);
                if (string.IsNullOrEmpty(json))
                    return new PluginRegistry { Plugins = new List<PluginInfo>() };

                return JsonSerializer.Deserialize(json, HPDJsonContext.Default.PluginRegistry) ?? 
                       new PluginRegistry { Plugins = new List<PluginInfo>() };
            }
            finally
            {
                rust_free_string(ptr);
            }
        }

        /// <summary>
        /// Get all function schemas as a JSON object
        /// </summary>
        public static JsonDocument GetPluginSchemas()
        {
            var ptr = rust_get_plugin_schemas();
            if (ptr == IntPtr.Zero)
                return JsonDocument.Parse("{}");

            try
            {
                var json = Marshal.PtrToStringAnsi(ptr);
                return JsonDocument.Parse(json ?? "{}");
            }
            finally
            {
                rust_free_string(ptr);
            }
        }

        /// <summary>
        /// Get plugin statistics
        /// </summary>
        public static PluginStats GetPluginStats()
        {
            var ptr = rust_get_plugin_stats();
            if (ptr == IntPtr.Zero)
                return new PluginStats();

            try
            {
                var json = Marshal.PtrToStringAnsi(ptr);
                if (string.IsNullOrEmpty(json))
                    return new PluginStats();

                return JsonSerializer.Deserialize(json, HPDJsonContext.Default.PluginStats) ?? new PluginStats();
            }
            finally
            {
                rust_free_string(ptr);
            }
        }

        /// <summary>
        /// Get list of all available function names
        /// </summary>
        public static List<string> GetFunctionList()
        {
            var ptr = rust_get_function_list();
            if (ptr == IntPtr.Zero)
                return new List<string>();

            try
            {
                var json = Marshal.PtrToStringAnsi(ptr);
                if (string.IsNullOrEmpty(json))
                    return new List<string>();

                return JsonSerializer.Deserialize(json, HPDJsonContext.Default.ListString) ?? new List<string>();
            }
            finally
            {
                rust_free_string(ptr);
            }
        }

        /// <summary>
        /// Execute a plugin function with the given arguments
        /// </summary>
        public static PluginExecutionResult ExecuteFunction(string functionName, Dictionary<string, object> arguments)
        {
            var argsJson = JsonSerializer.Serialize(arguments, HPDJsonContext.Default.DictionaryStringObject);
            var ptr = rust_execute_plugin_function(functionName, argsJson);
            
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
                rust_free_string(ptr);
            }
        }
    }

    /// <summary>
    /// Plugin registry information from Rust
    /// </summary>
    public class PluginRegistry
    {
        public List<PluginInfo> Plugins { get; set; } = new();
    }

    /// <summary>
    /// Information about a single plugin
    /// </summary>
    public class PluginInfo
    {
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public List<FunctionInfo> Functions { get; set; } = new();
    }

    /// <summary>
    /// Information about a plugin function
    /// </summary>
    public class FunctionInfo
    {
        public string Name { get; set; } = string.Empty;
        public string Wrapper { get; set; } = string.Empty;
    }

    /// <summary>
    /// Plugin statistics from Rust
    /// </summary>
    public class PluginStats
    {
        public int TotalPlugins { get; set; }
        public int TotalFunctions { get; set; }
        public List<PluginSummary> Plugins { get; set; } = new();
    }

    /// <summary>
    /// Summary information about a plugin
    /// </summary>
    public class PluginSummary
    {
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public int FunctionCount { get; set; }
    }

    /// <summary>
    /// Result of executing a plugin function
    /// </summary>
    public class PluginExecutionResult
    {
        public bool Success { get; set; }
        public JsonDocument? Result { get; set; }
        public string? Error { get; set; }
    }
}
