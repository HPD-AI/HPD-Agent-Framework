using Spectre.Console;
using AgentConsoleTest;

/// <summary>
/// Demo program to showcase DiffPlex-powered diff rendering
/// Run with: dotnet run --project test/AgentConsoleTest DiffDemo.cs
/// </summary>
public static class DiffDemo
{
    public static void Run()
    {
        var oldCode = @"public class Calculator
{
    public int Add(int a, int b)
    {
        return a + b;
    }

    public int Multiply(int x, int y)
    {
        return x * y;
    }
}";

        var newCode = @"public class Calculator
{
    // Updated parameter names for clarity
    public int Add(int x, int y)
    {
        return x + y;
    }

    public int Multiply(int x, int y)
    {
        // Added logging
        Console.WriteLine($""Multiplying {x} * {y}"");
        return x * y;
    }

    public int Subtract(int x, int y)
    {
        return x - y;
    }
}";

        AnsiConsole.MarkupLine("[bold cyan]Demo 1: Inline Diff (Default)[/]");
        AnsiConsole.WriteLine();

        var inlineDiff = new DiffRenderer
        {
            OldContent = oldCode,
            NewContent = newCode,
            Filename = "Calculator.cs",
            IgnoreWhitespace = true,
            MaxLines = 100
        };

        AnsiConsole.Write(inlineDiff.Render());
        AnsiConsole.WriteLine();
        AnsiConsole.WriteLine();

        AnsiConsole.MarkupLine("[bold cyan]Demo 2: Side-by-Side Diff[/]");
        AnsiConsole.WriteLine();

        var sideBySideDiff = new DiffRenderer
        {
            OldContent = oldCode,
            NewContent = newCode,
            Filename = "Calculator.cs",
            ShowSideBySide = true,
            IgnoreWhitespace = true,
            MaxLines = 100
        };

        AnsiConsole.Write(sideBySideDiff.Render());
        AnsiConsole.WriteLine();
        AnsiConsole.WriteLine();

        AnsiConsole.MarkupLine("[bold cyan]Demo 3: Unified Diff Format (Git-style)[/]");
        AnsiConsole.WriteLine();

        var unifiedDiffString = @"--- Calculator.cs
+++ Calculator.cs
@@ -1,10 +1,16 @@
 public class Calculator
 {
-    public int Add(int a, int b)
+    // Updated parameter names for clarity
+    public int Add(int x, int y)
     {
-        return a + b;
+        return x + y;
     }

     public int Multiply(int x, int y)
     {
+        // Added logging
+        Console.WriteLine($""Multiplying {x} * {y}"");
         return x * y;
     }
+
+    public int Subtract(int x, int y)
+    {
+        return x - y;
+    }
 }";

        var unifiedDiff = new DiffRenderer
        {
            DiffContent = unifiedDiffString,
            Filename = "Calculator.cs"
        };

        AnsiConsole.Write(unifiedDiff.Render());
        AnsiConsole.WriteLine();

        AnsiConsole.MarkupLine("[dim]Press any key to exit...[/]");
    }
}
