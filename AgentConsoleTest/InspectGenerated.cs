using System;
using System.Reflection;
using System.Linq;

namespace AgentConsoleTest;

public class InspectGenerated
{
    public static void Main(string[] args)
    {
        var asm = Assembly.GetExecutingAssembly();

        // Find the FinancialAnalysisSkillsRegistration class
        var registryType = asm.GetType("AgentConsoleTest.Skills.FinancialAnalysisSkillsRegistration");

        if (registryType != null)
        {
            Console.WriteLine($"Found registry type: {registryType.FullName}");
            Console.WriteLine($"\nMethods in this type:");
            foreach (var method in registryType.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                Console.WriteLine($"  - {method.Name}");
            }

            // Try to call CreateQuickLiquidityAnalysisSkill directly
            var skillMethod = registryType.GetMethod("CreateQuickLiquidityAnalysisSkill",
                BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);

            if (skillMethod != null)
            {
                Console.WriteLine("\n=== CreateQuickLiquidityAnalysisSkill Method Found ===");
                Console.WriteLine($"Parameters: {string.Join(", ", skillMethod.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"))}");

                // Try to invoke it to see what it produces
                try
                {
                    var result = skillMethod.Invoke(null, null);

                    if (result != null)
                    {
                        var skillType = result.GetType();
                        Console.WriteLine($"\nReturned skill type: {skillType.Name}");

                        // Check AdditionalProperties
                        var additionalProps = skillType.GetProperty("AdditionalProperties")?.GetValue(result) as System.Collections.Generic.IDictionary<string, object>;

                        if (additionalProps != null && additionalProps.Count > 0)
                        {
                            Console.WriteLine("\n=== AdditionalProperties ===");
                            foreach (var kvp in additionalProps)
                            {
                                Console.WriteLine($"  {kvp.Key}: {kvp.Value?.GetType().Name ?? "null"}");

                                if (kvp.Key == "DocumentUploads" && kvp.Value is Array arr)
                                {
                                    Console.WriteLine($"    Array length: {arr.Length}");
                                    for (int i = 0; i < arr.Length; i++)
                                    {
                                        var item = arr.GetValue(i);
                                        Console.WriteLine($"    [{i}]: {item?.GetType().Name ?? "null"}");

                                        if (item != null)
                                        {
                                            if (item is System.Collections.IDictionary dict)
                                            {
                                                foreach (System.Collections.DictionaryEntry entry in dict)
                                                {
                                                    Console.WriteLine($"      {entry.Key} = {entry.Value}");
                                                }
                                            }
                                            else
                                            {
                                                var itemType = item.GetType();
                                                foreach (var prop in itemType.GetProperties())
                                                {
                                                    var val = prop.GetValue(item);
                                                    Console.WriteLine($"      {prop.Name} = {val}");
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                        else
                        {
                            Console.WriteLine("\n❌ AdditionalProperties is empty or null");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"\n❌ Error invoking skill method: {ex.Message}");
                    if (ex.InnerException != null)
                        Console.WriteLine($"   Inner: {ex.InnerException.Message}");
                }
            }
            else
            {
                Console.WriteLine("❌ CreateQuickLiquidityAnalysisSkill method not found");
            }
        }
        else
        {
            Console.WriteLine("❌ Registry type not found");
            Console.WriteLine("\nAll types in assembly:");
            foreach (var t in asm.GetTypes().Where(t => !t.Name.StartsWith("<")))
            {
                Console.WriteLine($"  - {t.FullName}");
            }
        }
    }
}
