// V3.0 DSL Code Generator - Clean implementation with conditional parameter support
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Linq;

internal static class DSLCodeGenerator
{
    private static readonly Regex PropertyExtractor = new(@"context\.([a-zA-Z_][a-zA-Z0-9_]*)", RegexOptions.Compiled);
    
    /// <summary>
    /// Generates a description resolver for dynamic templates.
    /// </summary>
    public static string GenerateDescriptionResolver(string name, string template, string contextTypeName)
    {
        var expressions = ExtractDSLExpressions(template);
        var replacements = new List<string>();

        foreach (var expression in expressions)
        {
            var cleaned = expression.Trim().TrimStart('{').TrimEnd('}');
            var match = PropertyExtractor.Match(cleaned);
            if (match.Success)
            {
                var propertyName = match.Groups[1].Value;
                replacements.Add($"template = template.Replace(\"{expression}\", typedContext.{propertyName}?.ToString() ?? \"\");");
            }
        }
        
        return $$"""
        /// <summary>
        /// Resolves dynamic description template for {{name}}.
        /// </summary>
        private static string Resolve{{name}}Description(IPluginMetadataContext? context)
        {
            var template = @"{{template}}";

            if (context is not {{contextTypeName}} typedContext)
            {
                return template; 
            }

            {{string.Join("\n            ", replacements)}}
            return template;
        }
        """;
    }

    /// <summary>
    /// UPDATED: Add validation info to generated conditional evaluators.
    /// </summary>
    public static string GenerateConditionalEvaluator(string functionName, string propertyExpression, string contextTypeName)
    {
        var conditionCode = ConvertPropertyExpressionToCode(propertyExpression);

        return $$"""
        /// <summary>
        /// Evaluates whether function {{functionName}} should be included.
        /// Expression: {{propertyExpression}}
        /// </summary>
        private static bool Evaluate{{functionName}}Condition(IPluginMetadataContext? context)
        {
            if (context == null) return true;
            
            if (context is not {{contextTypeName}} typedContext) return false;
            
            try
            {
                // Evaluating: {{propertyExpression}}
                return {{conditionCode.Replace("context.", "typedContext.")}};
            }
            catch (Exception ex)
            {
                // Log error for debugging: Expression '{{propertyExpression}}' failed: {ex.Message}
                return false;
            }
        }
        """;
    }
    
    /// <summary>
    /// UPDATED: Add validation info to parameter evaluators.
    /// </summary>
    public static string GenerateParameterConditionalEvaluator(string functionName, string parameterName, string propertyExpression, string contextTypeName)
    {
        var conditionCode = ConvertPropertyExpressionToCode(propertyExpression);

        return $$"""
        /// <summary>
        /// Evaluates whether parameter '{{parameterName}}' should be visible for {{functionName}}.
        /// Expression: {{propertyExpression}}
        /// </summary>
        private static bool Evaluate{{functionName}}Parameter{{parameterName}}Condition(IPluginMetadataContext? context)
        {
            if (context == null) return true;
            
            if (context is not {{contextTypeName}} typedContext) return false;
            
            try
            {
                // Evaluating: {{propertyExpression}}
                return {{conditionCode.Replace("context.", "typedContext.")}};
            }
            catch (Exception ex)
            {
                // Log error for debugging: Parameter condition '{{propertyExpression}}' failed: {ex.Message}
                return false;
            }
        }
        """;
    }

    /// <summary>
    /// Extracts template expressions like {context.PropertyName} from templates.
    /// </summary>
    private static List<string> ExtractDSLExpressions(string template)
    {
        var expressions = new List<string>();
        int start = 0;
        while ((start = template.IndexOf('{', start)) != -1)
        {
            int end = template.IndexOf('}', start + 1);
            if (end == -1) break;
            expressions.Add(template.Substring(start, end - start + 1));
            start = end + 1;
        }
        return expressions;
    }

    /// <summary>
    /// Converts property expressions to typed context code.
    /// Examples: "HasProvider" → "context.HasProvider", "Count > 1" → "context.Count > 1"
    /// </summary>
    private static string ConvertPropertyExpressionToCode(string propertyExpression)
    {
        var identifierRegex = new Regex(@"\b[A-Za-z_][A-Za-z0-9_]*\b");
        var keywords = new HashSet<string> { "true", "false", "null", "&&", "||", "!" };

        var result = identifierRegex.Replace(propertyExpression, match =>
        {
            var identifier = match.Value;
            return !keywords.Contains(identifier.ToLower()) ? $"context.{identifier}" : identifier;
        });

        return result;
    }

    /// <summary>
    /// Generates a parameter description resolver for dynamic templates.
    /// </summary>
    public static string GenerateParameterDescriptionResolver(string functionName, string parameterName, string template, string contextTypeName)
    {
        var expressions = ExtractDSLExpressions(template);
        var replacements = new List<string>();

        foreach (var expression in expressions)
        {
            var cleaned = expression.Trim().TrimStart('{').TrimEnd('}');
            var match = PropertyExtractor.Match(cleaned);
            if (match.Success)
            {
                var propertyName = match.Groups[1].Value;
                replacements.Add($"template = template.Replace(\"{expression}\", typedContext.{propertyName}?.ToString() ?? \"\");");
            }
        }
        
        return $$"""
        /// <summary>
        /// Resolves dynamic parameter description for {{functionName}}.{{parameterName}}.
        /// </summary>
        private static string Resolve{{functionName}}Parameter{{parameterName}}Description(IPluginMetadataContext? context)
        {
            var template = @"{{template}}";

            if (context is not {{contextTypeName}} typedContext)
            {
                return template; 
            }

            {{string.Join("\n            ", replacements)}}
            return template;
        }
        """;
    }
}