using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.AI;

/// <summary>
/// Validates JSON schemas against Anthropic's requirements before sending to API.
/// </summary>
public static class SchemaValidator
{
    /// <summary>
    /// Validates all tools' schemas and reports any issues.
    /// </summary>
    public static void ValidateTools(IList<AIFunction> tools)
    {
        Console.WriteLine($"\n=== SCHEMA VALIDATION ({tools.Count} tools) ===\n");

        for (int i = 0; i < tools.Count; i++)
        {
            var tool = tools[i];
            Console.WriteLine($"[{i}] {tool.Name}");

            try
            {
                var schema = tool.JsonSchema;
                var errors = ValidateSchema(schema, tool.Name);

                if (errors.Any())
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"  INVALID SCHEMA:");
                    foreach (var error in errors)
                    {
                        Console.WriteLine($"     - {error}");
                    }
                    Console.ResetColor();

                    // Print the schema for inspection
                    Console.WriteLine($"  Schema:");
                    Console.WriteLine(JsonSerializer.Serialize(schema, new JsonSerializerOptions { WriteIndented = true }));
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"  ✓ Valid");
                    Console.ResetColor();
                }
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"  ERROR: {ex.Message}");
                Console.ResetColor();
            }

            Console.WriteLine();
        }
    }

    /// <summary>
    /// Validates a single schema against JSON Schema Draft 2020-12 requirements.
    /// </summary>
    private static List<string> ValidateSchema(JsonElement schema, string toolName)
    {
        var errors = new List<string>();

        // Check if it's an object
        if (schema.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"Schema must be an object, got {schema.ValueKind}");
            return errors;
        }

        // Required: must have "type" property
        if (!schema.TryGetProperty("type", out var typeElement))
        {
            errors.Add("Missing required 'type' property");
        }

        // Check for properties
        if (schema.TryGetProperty("properties", out var properties))
        {
            if (properties.ValueKind != JsonValueKind.Object)
            {
                errors.Add($"'properties' must be an object, got {properties.ValueKind}");
            }
            else
            {
                // Validate each property
                foreach (var prop in properties.EnumerateObject())
                {
                    var propErrors = ValidatePropertySchema(prop.Value, $"{toolName}.{prop.Name}");
                    errors.AddRange(propErrors);
                }
            }
        }

        // Check additionalProperties
        if (schema.TryGetProperty("additionalProperties", out var additionalProps))
        {
            if (additionalProps.ValueKind != JsonValueKind.True &&
                additionalProps.ValueKind != JsonValueKind.False &&
                additionalProps.ValueKind != JsonValueKind.Object)
            {
                errors.Add($"'additionalProperties' must be boolean or object, got {additionalProps.ValueKind}");
            }
        }

        // Check required array
        if (schema.TryGetProperty("required", out var required))
        {
            if (required.ValueKind != JsonValueKind.Array)
            {
                errors.Add($"'required' must be an array, got {required.ValueKind}");
            }
        }

        // Check for invalid $schema keyword (Anthropic doesn't like this in tools)
        if (schema.TryGetProperty("$schema", out _))
        {
            errors.Add("'$schema' keyword is not allowed in tool schemas (this is the likely issue!)");
        }

        return errors;
    }

    /// <summary>
    /// Validates a property schema.
    /// </summary>
    private static List<string> ValidatePropertySchema(JsonElement propSchema, string propertyPath)
    {
        var errors = new List<string>();

        if (propSchema.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"{propertyPath}: Property schema must be an object");
            return errors;
        }

        // Must have type
        if (!propSchema.TryGetProperty("type", out var typeElement))
        {
            errors.Add($"{propertyPath}: Missing 'type' property");
        }

        // Validate nested objects
        if (propSchema.TryGetProperty("properties", out var nestedProps))
        {
            foreach (var nested in nestedProps.EnumerateObject())
            {
                var nestedErrors = ValidatePropertySchema(nested.Value, $"{propertyPath}.{nested.Name}");
                errors.AddRange(nestedErrors);
            }
        }

        // Validate array items
        if (propSchema.TryGetProperty("items", out var items))
        {
            if (items.ValueKind == JsonValueKind.Object)
            {
                var itemErrors = ValidatePropertySchema(items, $"{propertyPath}[]");
                errors.AddRange(itemErrors);
            }
        }

        return errors;
    }
}
