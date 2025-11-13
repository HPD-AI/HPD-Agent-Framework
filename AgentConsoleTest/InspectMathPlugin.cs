using System;
using System.Reflection;
using System.Linq;
using System.Collections.Generic;

namespace AgentConsoleTest;

/// <summary>
/// Inspects the generated MathPlugin container to verify it includes both AI functions and skills.
/// </summary>
public class InspectMathPlugin
{
    public static void Main(string[] args)
    {
        var asm = Assembly.GetExecutingAssembly();

        // Find the MathPluginRegistration class
        var registryType = asm.GetType("MathPluginRegistration");

        if (registryType != null)
        {
            Console.WriteLine($"✓ Found registry type: {registryType.FullName}");
            Console.WriteLine($"\nMethods in this type:");
            foreach (var method in registryType.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                Console.WriteLine($"  - {method.Name}");
            }

            // Try to find the CreateMathPluginContainer method
            var containerMethod = registryType.GetMethod("CreateMathPluginContainer",
                BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);

            if (containerMethod != null)
            {
                Console.WriteLine("\n=== CreateMathPluginContainer Method Found ===");
                Console.WriteLine($"Parameters: {string.Join(", ", containerMethod.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"))}");

                // Try to invoke it to see what it produces
                try
                {
                    var result = containerMethod.Invoke(null, null);

                    if (result != null)
                    {
                        var containerType = result.GetType();
                        Console.WriteLine($"\n✓ Returned container type: {containerType.Name}");

                        // Get Name and Description
                        var nameProperty = containerType.GetProperty("Name");
                        var descProperty = containerType.GetProperty("Description");

                        if (nameProperty != null && descProperty != null)
                        {
                            var name = nameProperty.GetValue(result)?.ToString();
                            var desc = descProperty.GetValue(result)?.ToString();

                            Console.WriteLine($"\n=== Container Details ===");
                            Console.WriteLine($"Name: {name}");
                            Console.WriteLine($"Description: {desc}");

                            // Check if description includes skills
                            if (desc?.Contains("SolveQuadratic") == true)
                            {
                                Console.WriteLine("\n✓ SUCCESS: Description includes skill 'SolveQuadratic'");
                            }
                            else
                            {
                                Console.WriteLine("\n✗ FAILURE: Description does NOT include skill 'SolveQuadratic'");
                            }

                            // Count functions mentioned
                            var functionsFound = new List<string>();
                            var expectedFunctions = new[] { "Add", "Multiply", "Abs", "Square", "Subtract", "Min" };
                            foreach (var func in expectedFunctions)
                            {
                                if (desc?.Contains(func) == true)
                                {
                                    functionsFound.Add(func);
                                }
                            }

                            Console.WriteLine($"\nFunctions found in description: {string.Join(", ", functionsFound)} ({functionsFound.Count}/6)");
                        }

                        // Check AdditionalProperties for FunctionNames and FunctionCount
                        var additionalProps = containerType.GetProperty("AdditionalProperties")?.GetValue(result) as IDictionary<string, object>;

                        if (additionalProps != null && additionalProps.Count > 0)
                        {
                            Console.WriteLine("\n=== AdditionalProperties ===");
                            foreach (var kvp in additionalProps)
                            {
                                Console.WriteLine($"  {kvp.Key}: {kvp.Value?.GetType().Name ?? "null"}");

                                if (kvp.Key == "FunctionNames" && kvp.Value is Array arr)
                                {
                                    Console.WriteLine($"    Array length: {arr.Length}");
                                    var names = new List<string>();
                                    for (int i = 0; i < arr.Length; i++)
                                    {
                                        var item = arr.GetValue(i);
                                        if (item != null)
                                        {
                                            names.Add(item.ToString()!);
                                        }
                                    }
                                    Console.WriteLine($"    Names: {string.Join(", ", names)}");

                                    // Check if SolveQuadratic is included
                                    if (names.Contains("SolveQuadratic"))
                                    {
                                        Console.WriteLine($"\n✓ SUCCESS: FunctionNames array includes 'SolveQuadratic' skill");
                                    }
                                    else
                                    {
                                        Console.WriteLine($"\n✗ FAILURE: FunctionNames array does NOT include 'SolveQuadratic' skill");
                                    }
                                }
                                else if (kvp.Key == "FunctionCount")
                                {
                                    Console.WriteLine($"    Value: {kvp.Value}");
                                    var count = Convert.ToInt32(kvp.Value);
                                    // Should be 6 AI functions + 1 skill = 7 total
                                    if (count == 7)
                                    {
                                        Console.WriteLine($"    ✓ SUCCESS: FunctionCount is 7 (6 functions + 1 skill)");
                                    }
                                    else
                                    {
                                        Console.WriteLine($"    ✗ FAILURE: FunctionCount is {count}, expected 7 (6 functions + 1 skill)");
                                    }
                                }
                            }
                        }
                        else
                        {
                            Console.WriteLine("\n✗ AdditionalProperties is empty or null");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"\n✗ Error invoking container method: {ex.Message}");
                    if (ex.InnerException != null)
                        Console.WriteLine($"   Inner: {ex.InnerException.Message}");
                    Console.WriteLine($"   Stack: {ex.StackTrace}");
                }
            }
            else
            {
                Console.WriteLine("✗ CreateMathPluginContainer method not found");
            }
        }
        else
        {
            Console.WriteLine("✗ Registry type not found");
            Console.WriteLine("\nAll types in assembly:");
            foreach (var t in asm.GetTypes().Where(t => !t.Name.StartsWith("<") && !t.Name.Contains("+")))
            {
                Console.WriteLine($"  - {t.FullName}");
            }
        }
    }
}
