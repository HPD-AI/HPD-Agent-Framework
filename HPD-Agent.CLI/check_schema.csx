#r "nuget: Microsoft.Extensions.AI, 9.10.0-preview.1.25115.3"
using System.Reflection;
using Microsoft.Extensions.AI;

// Load the assembly
var asm = Assembly.LoadFrom("bin/Debug/net10.0/AgentConsoleTest.dll");
var codingType = asm.GetType("CodingToolkit");

if (codingType != null) {
    var instance = Activator.CreateInstance(codingType);
    var methods = codingType.GetMethods(BindingFlags.Public | BindingFlags.Instance);
    
    int index = 0;
    foreach (var method in methods.Where(m => m.GetCustomAttribute(asm.GetType("HPD.Agent.AIFunctionAttribute")) != null)) {
        Console.WriteLine($"\n[{index}] {method.Name}");
        index++;
    }
}
