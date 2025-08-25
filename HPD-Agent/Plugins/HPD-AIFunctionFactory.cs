using System.Text.Json;
using System.Text.Json.Serialization;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.AI;
using System.Reflection;
using FluentValidation;

/// <summary>
/// Extended AIFunctionFactory that supports parameter descriptions, invocation filters, and enhanced JSON schema generation.
/// </summary>
public class HPDAIFunctionFactory
{
    private static readonly HPDAIFunctionFactoryOptions _defaultOptions = new();
    // AOT-compatible JSON context for basic serialization
    private static readonly AOTJsonContext _aotJsonContext = AOTJsonContext.Default;

    /// <summary>
    /// Creates an AIFunction with rich parameter descriptions and invocation filters.
    /// </summary>
    public static AIFunction Create(Delegate method, HPDAIFunctionFactoryOptions? options = null)
    {
        return new HPDAIFunction(method.Method, method.Target, options ?? _defaultOptions);
    }

    /// <summary>
    /// Creates an AIFunction with rich parameter descriptions and invocation filters.
    /// </summary>
    public static AIFunction Create(MethodInfo method, object? target, HPDAIFunctionFactoryOptions? options = null)
    {
        return new HPDAIFunction(method, target, options ?? _defaultOptions);
    }

    /// <summary>
    /// Creates an AIFunction with DTO-based parameter validation using the generated DTO type (sync).
    /// </summary>
    public static AIFunction CreateWithDto<TDto>(Func<TDto, object?> function, HPDAIFunctionFactoryOptions? options = null)
        where TDto : class, new()
    {
        return new HPDAIFunctionWithDto<TDto>(function, options ?? _defaultOptions);
    }

    /// <summary>
    /// Creates an AIFunction with DTO-based parameter validation using the generated DTO type (async).
    /// </summary>
    public static AIFunction CreateWithDto<TDto>(Func<TDto, Task<object?>> function, HPDAIFunctionFactoryOptions? options = null)
        where TDto : class, new()
    {
        return new HPDAIFunctionWithDto<TDto>(function, options ?? _defaultOptions);
    }

    /// <summary>
    /// AIFunction implementation that includes parameter descriptions in its JSON schema and supports invocation filters.
    /// </summary>
    public class HPDAIFunction : AIFunction
    {
        private readonly MethodInfo _method;
        private readonly object? _target;
        private readonly HPDAIFunctionFactoryOptions _options;
        private readonly Lazy<JsonElement> _jsonSchema;
        private readonly Lazy<JsonElement?> _returnJsonSchema;

        public HPDAIFunction(MethodInfo method, object? target, HPDAIFunctionFactoryOptions options)
        {
            _method = method ?? throw new ArgumentNullException(nameof(method));
            _target = target;
            _options = options;
            HPDOptions = options;

            _jsonSchema = new Lazy<JsonElement>(() => CreateJsonSchema());
            _returnJsonSchema = new Lazy<JsonElement?>(() => CreateReturnJsonSchema());

            Name = options.Name ?? method.Name;
            Description = options.Description ??
                method.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>()?.Description ?? "";
        }

        /// <summary>
        /// Exposes the HPDAIFunctionFactoryOptions used to create this function.
        /// </summary>
        public HPDAIFunctionFactoryOptions HPDOptions { get; }

        public override string Name { get; }
        public override string Description { get; }
        public override JsonElement JsonSchema => _jsonSchema.Value;
        public override JsonElement? ReturnJsonSchema => _returnJsonSchema.Value;
        public override MethodInfo? UnderlyingMethod => _method;

        private JsonElement CreateJsonSchema()
        {
            var parameters = _method.GetParameters();
            var properties = new Dictionary<string, object>();
            var required = new List<string>();

            foreach (var param in parameters)
            {
                // Skip special parameters like CancellationToken
                if (param.ParameterType == typeof(CancellationToken) ||
                    param.ParameterType == typeof(AIFunctionArguments) ||
                    param.ParameterType == typeof(IServiceProvider))
                {
                    continue;
                }

                // Get description from options first, then fall back to attribute
                var description = _options.ParameterDescriptions?.ContainsKey(param.Name!) == true
                    ? _options.ParameterDescriptions[param.Name!]
                    : param.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>()?.Description;

                var paramSchema = new Dictionary<string, object>
                {
                    { "type", GetJsonType(param.ParameterType) },
                };

                // Add description if available
                if (!string.IsNullOrEmpty(description))
                {
                    paramSchema["description"] = description;
                }

                properties[param.Name!] = paramSchema;

                // Add to required list if parameter has no default value
                if (!param.HasDefaultValue)
                {
                    required.Add(param.Name!);
                }
            }

            var schema = new Dictionary<string, object>
            {
                { "type", "object" },
                { "properties", properties }
            };

            if (required.Count > 0)
            {
                schema["required"] = required;
            }

            // FIX: Use AOT-compatible serialization
            var jsonString = JsonSerializer.Serialize(schema, _aotJsonContext.DictionaryStringObject);
            return JsonSerializer.Deserialize(jsonString, _aotJsonContext.JsonElement);
        }

        private JsonElement? CreateReturnJsonSchema()
        {
            if (_method.ReturnType == typeof(void) ||
                _method.ReturnType == typeof(Task) ||
                _method.ReturnType == typeof(ValueTask))
            {
                return null;
            }

            var returnType = _method.ReturnType;
            if (returnType.IsGenericType)
            {
                var genericTypeDef = returnType.GetGenericTypeDefinition();
                if (genericTypeDef == typeof(Task<>) ||
                    genericTypeDef == typeof(ValueTask<>))
                {
                    returnType = returnType.GetGenericArguments()[0];
                }
            }

            var schema = new Dictionary<string, object>
            {
                { "type", GetJsonType(returnType) }
            };

            var description = _method.ReturnParameter.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>()?.Description;
            if (!string.IsNullOrEmpty(description))
            {
                schema["description"] = description;
            }

            // FIX: Use AOT-compatible serialization
            var jsonString = JsonSerializer.Serialize(schema, _aotJsonContext.DictionaryStringObject);
            return JsonSerializer.Deserialize(jsonString, _aotJsonContext.JsonElement);
        }

        private string GetJsonType(Type type)
        {
            if (type == typeof(string))
                return "string";
            if (type == typeof(int) || type == typeof(long) || type == typeof(float) || type == typeof(double))
                return "number";
            if (type == typeof(bool))
                return "boolean";
            if (type.IsArray || typeof(System.Collections.IEnumerable).IsAssignableFrom(type))
                return "array";
            return "object";
        }

        [UnconditionalSuppressMessage("AOT", "IL2067", Justification = "Type conversion needed for function parameters")]
        private object? ConvertArgument(object? value, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] Type targetType)
        {
            if (value == null)
            {
                return targetType.IsValueType ? Activator.CreateInstance(targetType) : null;
            }

            if (targetType.IsAssignableFrom(value.GetType()))
            {
                return value;
            }

            if (value is JsonElement jsonElement)
            {
                // FIX: Use custom AOT-safe conversion instead of reflection-based deserialization
                return ConvertJsonElementToType(jsonElement, targetType);
            }

            // FIX: Use custom conversion for complex types
            return ConvertObjectToType(value, targetType);
        }

        [UnconditionalSuppressMessage("AOT", "IL2067", Justification = "Type conversion needed for function parameters")]
        private object? ConvertArgumentUnsafe(object? value, Type targetType)
        {
            if (value == null)
            {
                return targetType.IsValueType ? Activator.CreateInstance(targetType) : null;
            }

            if (targetType.IsAssignableFrom(value.GetType()))
            {
                return value;
            }

            if (value is JsonElement jsonElement)
            {
                // FIX: Use custom AOT-safe conversion instead of reflection-based deserialization
                return ConvertJsonElementToType(jsonElement, targetType);
            }

            // FIX: Use custom conversion for complex types
            return ConvertObjectToType(value, targetType);
        }

        private object? ConvertJsonElementToType(JsonElement jsonElement, Type targetType)
        {
            // AOT-safe conversion for common types
            if (targetType == typeof(string))
                return jsonElement.GetString();
            if (targetType == typeof(int))
                return jsonElement.GetInt32();
            if (targetType == typeof(long))
                return jsonElement.GetInt64();
            if (targetType == typeof(double))
                return jsonElement.GetDouble();
            if (targetType == typeof(float))
                return jsonElement.GetSingle();
            if (targetType == typeof(bool))
                return jsonElement.GetBoolean();
            if (targetType == typeof(decimal))
                return jsonElement.GetDecimal();

            // For complex types, convert to string and back
            var jsonString = jsonElement.GetRawText();
            return ConvertFromJsonString(jsonString, targetType);
        }

        private object? ConvertObjectToType(object value, Type targetType)
        {
            // Simple type conversions
            if (targetType == typeof(string))
                return value.ToString();
            
            // For complex conversions, serialize and deserialize
            var jsonString = JsonSerializer.Serialize(value, _aotJsonContext.Object);
            return ConvertFromJsonString(jsonString, targetType);
        }

        private object? ConvertFromJsonString(string jsonString, Type targetType)
        {
            // AOT-safe conversions for known types
            if (targetType == typeof(string))
                return JsonSerializer.Deserialize(jsonString, _aotJsonContext.String);
            if (targetType == typeof(int))
                return JsonSerializer.Deserialize(jsonString, _aotJsonContext.Int32);
            if (targetType == typeof(long))
                return JsonSerializer.Deserialize(jsonString, _aotJsonContext.Int64);
            if (targetType == typeof(double))
                return JsonSerializer.Deserialize(jsonString, _aotJsonContext.Double);
            if (targetType == typeof(float))
                return JsonSerializer.Deserialize(jsonString, _aotJsonContext.Single);
            if (targetType == typeof(bool))
                return JsonSerializer.Deserialize(jsonString, _aotJsonContext.Boolean);

            // For other types, return the JSON string and let the method handle it
            return jsonString;
        }

        // --- VVV THIS IS THE CORE CHANGE VVV ---
        protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            var parameters = _method.GetParameters();
            var args = new object?[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
            {
                var param = parameters[i];
                if (param.ParameterType == typeof(CancellationToken))
                {
                    args[i] = cancellationToken;
                }
                else if (param.ParameterType == typeof(AIFunctionArguments))
                {
                    args[i] = arguments;
                }
                else if (param.ParameterType == typeof(IServiceProvider))
                {
                    args[i] = arguments.Services;
                }
                else
                {
                    if (!arguments.TryGetValue(param.Name!, out var value))
                    {
                        if (param.HasDefaultValue)
                        {
                            args[i] = param.DefaultValue;
                            continue;
                        }
                        throw new ArgumentException($"Required parameter '{param.Name}' was not provided.");
                    }
                    args[i] = ConvertArgumentUnsafe(value, param.ParameterType);
                }
            }

            var result = _method.Invoke(_target, args);
            if (result is Task task)
            {
                await task.ConfigureAwait(true);
                result = ExtractTaskResult(task);
            }
            else if (result != null && result.GetType().Name.StartsWith("ValueTask"))
            {
                if (result is ValueTask nonGenericValueTask)
                {
                    await nonGenericValueTask.ConfigureAwait(true);
                    result = null;
                }
                else
                {
                    result = null;
                }
            }
            return result;
        }

        private static object? ExtractTaskResult(Task task)
        {
            // Use pattern matching instead of reflection for AOT compatibility
            return task switch
            {
                Task<object> objTask => objTask.Result,
                Task<string> stringTask => stringTask.Result,
                Task<int> intTask => intTask.Result,
                Task<long> longTask => longTask.Result,
                Task<double> doubleTask => doubleTask.Result,
                Task<float> floatTask => floatTask.Result,
                Task<bool> boolTask => boolTask.Result,
                Task<decimal> decimalTask => decimalTask.Result,
                _ => null // For non-generic tasks or unsupported types
            };
        }

        private static object? ExtractValueTaskResult(ValueTask valueTask)
        {
            // For ValueTask, we need to check the type using different approach
            // Since ValueTask is a struct, we can't use pattern matching directly
            // Return null for non-generic ValueTasks since they don't have results
            return null;
        }
    }

    /// <summary>
    /// DTO-based AIFunction implementation with automatic validation and parameter mapping.
    /// </summary>
    public class HPDAIFunctionWithDto<TDto> : AIFunction where TDto : class, new()
    {
        private readonly Func<TDto, object?>? _syncFunction;
        private readonly Func<TDto, Task<object?>>? _asyncFunction;
        private readonly HPDAIFunctionFactoryOptions _options;
        private readonly Lazy<JsonElement> _jsonSchema;
        private readonly Lazy<JsonElement?> _returnJsonSchema;
        private readonly IValidator<TDto>? _validator;
        private readonly bool _isAsync;

        public HPDAIFunctionWithDto(Func<TDto, object?> function, HPDAIFunctionFactoryOptions options)
        {
            _syncFunction = function ?? throw new ArgumentNullException(nameof(function));
            _options = options;
            _validator = options.Validator as IValidator<TDto>;
            _isAsync = false;

            _jsonSchema = new Lazy<JsonElement>(() => CreateDtoJsonSchema());
            _returnJsonSchema = new Lazy<JsonElement?>(() => CreateDtoReturnJsonSchema());

            Name = options.Name ?? function.Method.Name;
            Description = options.Description ??
                function.Method.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>()?.Description ?? "";
        }

        public HPDAIFunctionWithDto(Func<TDto, Task<object?>> function, HPDAIFunctionFactoryOptions options)
        {
            _asyncFunction = function ?? throw new ArgumentNullException(nameof(function));
            _options = options;
            _validator = options.Validator as IValidator<TDto>;
            _isAsync = true;

            _jsonSchema = new Lazy<JsonElement>(() => CreateDtoJsonSchema());
            _returnJsonSchema = new Lazy<JsonElement?>(() => CreateDtoReturnJsonSchema());

            Name = options.Name ?? function.Method.Name;
            Description = options.Description ??
                function.Method.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>()?.Description ?? "";
        }

        public override string Name { get; }
        public override string Description { get; }
        public override JsonElement JsonSchema => _jsonSchema.Value;
        public override JsonElement? ReturnJsonSchema => _returnJsonSchema.Value;
        public override MethodInfo? UnderlyingMethod => _syncFunction?.Method ?? _asyncFunction?.Method;

        private JsonElement CreateDtoJsonSchema()
        {
            // Generate schema from DTO type
            var properties = new Dictionary<string, object>();
            var required = new List<string>();

            foreach (var prop in typeof(TDto).GetProperties())
            {
                var propSchema = new Dictionary<string, object>
                {
                    { "type", GetJsonType(prop.PropertyType) }
                };

                // Get description from options
                if (_options.ParameterDescriptions?.ContainsKey(prop.Name) == true)
                {
                    propSchema["description"] = _options.ParameterDescriptions[prop.Name];
                }

                properties[prop.Name] = propSchema;

                // Check if property is required (no nullable type and no default value)
                if (!IsNullablePropertyType(prop.PropertyType))
                {
                    required.Add(prop.Name);
                }
            }

            var schema = new Dictionary<string, object>
            {
                { "type", "object" },
                { "properties", properties }
            };

            if (required.Count > 0)
            {
                schema["required"] = required;
            }

            var jsonString = JsonSerializer.Serialize(schema, _aotJsonContext.DictionaryStringObject);
            return JsonSerializer.Deserialize(jsonString, _aotJsonContext.JsonElement);
        }

        private JsonElement? CreateDtoReturnJsonSchema()
        {
            var returnType = _isAsync ? typeof(Task<object?>) : typeof(object);
            
            if (returnType == typeof(void) ||
                returnType == typeof(Task) ||
                returnType == typeof(ValueTask))
            {
                return null;
            }

            if (returnType.IsGenericType)
            {
                var genericTypeDef = returnType.GetGenericTypeDefinition();
                if (genericTypeDef == typeof(Task<>) ||
                    genericTypeDef == typeof(ValueTask<>))
                {
                    returnType = returnType.GetGenericArguments()[0];
                }
            }

            var schema = new Dictionary<string, object>
            {
                { "type", GetJsonType(returnType) }
            };

            var jsonString = JsonSerializer.Serialize(schema, _aotJsonContext.DictionaryStringObject);
            return JsonSerializer.Deserialize(jsonString, _aotJsonContext.JsonElement);
        }

        private string GetJsonType(Type type)
        {
            if (type == typeof(string))
                return "string";
            if (type == typeof(int) || type == typeof(long) || type == typeof(float) || type == typeof(double))
                return "number";
            if (type == typeof(bool))
                return "boolean";
            if (type.IsArray || typeof(System.Collections.IEnumerable).IsAssignableFrom(type))
                return "array";
            return "object";
        }

        private static bool IsNullablePropertyType(Type type)
        {
            return type.IsClass || 
                   (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Nullable<>));
        }

        /// <summary>
        /// Generates helpful retry guidance from validation errors.
        /// </summary>
        private static string GenerateValidationRetryGuidance(IEnumerable<FluentValidation.Results.ValidationFailure> errors)
        {
            var suggestions = errors.Select(error => $"- {error.PropertyName}: {error.ErrorMessage}");
            return $"Please correct these validation issues and try again:\n{string.Join("\n", suggestions)}";
        }

        /// <summary>
        /// Parses JSON error messages to extract user-friendly information.
        /// </summary>
        private static string ParseJsonError(string jsonError)
        {
            // Extract property name and expected type from JSON error messages
            if (jsonError.Contains("could not be converted to") && jsonError.Contains("Path: $."))
            {
                var pathStart = jsonError.IndexOf("Path: $.");
                if (pathStart >= 0)
                {
                    var pathEnd = jsonError.IndexOf(" |", pathStart);
                    if (pathEnd >= 0)
                    {
                        var propertyPath = jsonError.Substring(pathStart + 8, pathEnd - pathStart - 8);
                        var typeStart = jsonError.IndexOf("converted to ") + 13;
                        var typeEnd = jsonError.IndexOf(".", typeStart);
                        if (typeEnd > typeStart)
                        {
                            var expectedType = jsonError.Substring(typeStart, typeEnd - typeStart);
                            var friendlyType = GetFriendlyTypeName(expectedType);
                            return $"Parameter '{propertyPath}' must be a {friendlyType}";
                        }
                    }
                }
            }
            
            return "Invalid parameter type provided";
        }

        /// <summary>
        /// Converts .NET type names to user-friendly names.
        /// </summary>
        private static string GetFriendlyTypeName(string typeName)
        {
            return typeName switch
            {
                "System.Int64" => "number (integer)",
                "System.Int32" => "number (integer)", 
                "System.Double" => "number (decimal)",
                "System.Single" => "number (decimal)",
                "System.Boolean" => "boolean (true/false)",
                "System.String" => "text string",
                "System.Decimal" => "number (decimal)",
                _ => typeName.Replace("System.", "").ToLowerInvariant()
            };
        }

        /// <summary>
        /// Generates helpful guidance for type conversion errors.
        /// </summary>
        private static string GenerateTypeConversionGuidance(string jsonError, Type dtoType)
        {
            var parsedError = ParseJsonError(jsonError);
            var properties = dtoType.GetProperties();
            
            var guidance = $"{parsedError}\n\nExpected parameter types:";
            foreach (var prop in properties)
            {
                var friendlyType = GetFriendlyTypeName(prop.PropertyType.Name);
                guidance += $"\n- {prop.Name}: {friendlyType}";
            }
            
            return guidance;
        }

        protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            // Convert AIFunctionArguments to dictionary, then serialize to DTO
            var argumentsDict = new Dictionary<string, object?>();
            foreach (var kvp in arguments)
            {
                argumentsDict[kvp.Key] = kvp.Value;
            }
            
            var argumentsJson = JsonSerializer.Serialize(argumentsDict);
            
            TDto dto;
            try
            {
                dto = JsonSerializer.Deserialize<TDto>(argumentsJson, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                })!;

                if (dto == null)
                {
                    return new TypeConversionErrorResponse
                    {
                        success = false,
                        error_type = "deserialization_failed",
                        function_name = Name,
                        error_message = "Failed to deserialize function arguments to DTO",
                        json_error = "DTO deserialization returned null",
                        retry_guidance = "Please ensure all required parameters are provided with correct types"
                    };
                }
            }
            catch (JsonException jsonEx)
            {
                // Convert JSON deserialization errors to structured validation responses
                return new TypeConversionErrorResponse
                {
                    success = false,
                    error_type = "type_conversion_failed",
                    function_name = Name,
                    error_message = ParseJsonError(jsonEx.Message),
                    json_error = jsonEx.Message,
                    retry_guidance = GenerateTypeConversionGuidance(jsonEx.Message, typeof(TDto))
                };
            }

            // Validate DTO if validator is provided
            if (_validator != null)
            {
                var validationResult = await _validator.ValidateAsync(dto, cancellationToken);
                if (!validationResult.IsValid)
                {
                    // Return structured validation response instead of throwing
                    return new ValidationErrorResponse
                    {
                        success = false,
                        error_type = "validation_failed",
                        function_name = Name,
                        validation_errors = validationResult.Errors.Select(error => new ValidationError
                        {
                            property = error.PropertyName,
                            attempted_value = error.AttemptedValue,
                            error_message = error.ErrorMessage,
                            error_code = error.ErrorCode,
                            severity = error.Severity.ToString()
                        }).ToArray(),
                        retry_guidance = GenerateValidationRetryGuidance(validationResult.Errors)
                    };
                }
            }

            // Call the function with DTO as parameter
            if (_isAsync && _asyncFunction != null)
            {
                return await _asyncFunction(dto);
            }
            else if (!_isAsync && _syncFunction != null)
            {
                return _syncFunction(dto);
            }
            else
            {
                throw new InvalidOperationException("No function delegate available");
            }
        }

        private static object? ExtractTaskResult(Task task)
        {
            return task switch
            {
                Task<object> objTask => objTask.Result,
                Task<string> stringTask => stringTask.Result,
                Task<int> intTask => intTask.Result,
                Task<long> longTask => longTask.Result,
                Task<double> doubleTask => doubleTask.Result,
                Task<float> floatTask => floatTask.Result,
                Task<bool> boolTask => boolTask.Result,
                Task<decimal> decimalTask => decimalTask.Result,
                _ => null
            };
        }
    }
}

/// <summary>
/// Options for HPDAIFunctionFactory with enhanced metadata support.
/// </summary>
public class HPDAIFunctionFactoryOptions
{
    /// <summary>
    /// Optional name override for the function.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Optional description override for the function.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Optional JsonSerializerOptions for parameter marshalling.
    /// </summary>
    public JsonSerializerOptions? SerializerOptions { get; set; }

    /// <summary>
    /// Parameter descriptions mapped by parameter name.
    /// </summary>
    public Dictionary<string, string>? ParameterDescriptions { get; set; }
    
    /// <summary>
    /// Whether the function requires user permission before execution.
    /// </summary>
    public bool RequiresPermission { get; set; }
    
    /// <summary>
    /// Optional validator for function arguments.
    /// </summary>
    public IValidator? Validator { get; set; }
}

/// <summary>
/// Error response types for structured validation feedback
/// </summary>
public class ValidationErrorResponse
{
    public bool success { get; set; }
    public string error_type { get; set; } = "";
    public string function_name { get; set; } = "";
    public ValidationError[] validation_errors { get; set; } = Array.Empty<ValidationError>();
    public string retry_guidance { get; set; } = "";
}

public class TypeConversionErrorResponse  
{
    public bool success { get; set; }
    public string error_type { get; set; } = "";
    public string function_name { get; set; } = "";
    public string error_message { get; set; } = "";
    public string json_error { get; set; } = "";
    public string retry_guidance { get; set; } = "";
}

public class ValidationError
{
    public string property { get; set; } = "";
    public object? attempted_value { get; set; }
    public string error_message { get; set; } = "";
    public string error_code { get; set; } = "";
    public string severity { get; set; } = "";
}

/// <summary>
/// AOT-compatible JSON context for basic type serialization
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(Dictionary<string, object>))]
[JsonSerializable(typeof(Dictionary<string, object?>))]
[JsonSerializable(typeof(object))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(float))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(decimal))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(List<object>))]
[JsonSerializable(typeof(List<Dictionary<string, object>>))]
[JsonSerializable(typeof(List<Dictionary<string, object?>>))]
[JsonSerializable(typeof(ValidationErrorResponse))]
[JsonSerializable(typeof(TypeConversionErrorResponse))]
[JsonSerializable(typeof(ValidationError))]
[JsonSerializable(typeof(ValidationError[]))]
internal partial class AOTJsonContext : JsonSerializerContext
{
}