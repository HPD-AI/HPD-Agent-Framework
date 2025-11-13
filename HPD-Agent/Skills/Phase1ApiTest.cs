namespace HPD_Agent.Skills;

/// <summary>
/// Phase 1 API Test - Validates the new type-safe skills API
/// This file demonstrates and tests all Phase 1 features without requiring source generator
/// </summary>
public class Phase1ApiTest
{
    // Mock plugin class for testing
    public static class MockFileSystemPlugin
    {
        public static string ReadFile(string path) => $"Reading {path}";
        public static void WriteFile(string path, string content) { }
        public static string[] ListFiles(string directory) => Array.Empty<string>();
    }

    public static class MockDebugPlugin
    {
        public static string GetStackTrace() => "Stack trace...";
        public static void LogMessage(string message) { }
    }

    /// <summary>
    /// Test 1: Simple skill without documents or options
    /// </summary>
    [Skill]
    public Skill SimpleSkill()
    {
        var skill = SkillFactory.Create(
            name: "SimpleDebugging",
            description: "Simple debugging workflow for basic issues",
            instructions: "1. Read log files\n2. Check stack trace\n3. Document findings",
            "MockFileSystemPlugin.ReadFile",
            "MockDebugPlugin.GetStackTrace"
        );

        // Validate
        ValidateSkill(skill, "SimpleDebugging", 2, 0, 0);
        return skill;
    }

    /// <summary>
    /// Test 2: Skill with options (no documents yet, just to test the API)
    /// </summary>
    [Skill]
    public Skill SkillWithOptions(SkillOptions? options = null)
    {
        var skill = SkillFactory.Create(
            name: "AdvancedDebugging",
            description: "Advanced debugging with comprehensive tooling",
            instructions: "Follow step-by-step methodology for complex issues",
            options: new SkillOptions
            {
                AutoExpand = false
            },
            "MockFileSystemPlugin.ReadFile",
            "MockFileSystemPlugin.WriteFile",
            "MockFileSystemPlugin.ListFiles",
            "MockDebugPlugin.GetStackTrace",
            "MockDebugPlugin.LogMessage"
        );

        // Validate
        ValidateSkill(skill, "AdvancedDebugging", 5, 0, 0);
        return skill;
    }

    /// <summary>
    /// Test 3: Skill with document references (using AddDocument)
    /// </summary>
    [Skill(Category = "Debugging", Priority = 10)]
    public Skill SkillWithDocumentReferences(SkillOptions? options = null)
    {
        var skill = SkillFactory.Create(
            name: "DocumentedDebugging",
            description: "Debugging with external documentation references",
            instructions: "Refer to documentation for detailed procedures",
            options: new SkillOptions()
                .AddDocument("debugging-guide")
                .AddDocument("error-codes-reference",
                    description: "Complete error code catalog with solutions"),
            "MockFileSystemPlugin.ReadFile",
            "MockDebugPlugin.GetStackTrace"
        );

        // Validate
        ValidateSkill(skill, "DocumentedDebugging", 2, 2, 0);
        ValidateDocumentReference(skill.Options.DocumentReferences[0],
            "debugging-guide", null);
        ValidateDocumentReference(skill.Options.DocumentReferences[1],
            "error-codes-reference", "Complete error code catalog with solutions");

        return skill;
    }

    /// <summary>
    /// Test 4: Skill with document uploads (using AddDocumentFromFile)
    /// </summary>
    [Skill(Category = "Debugging")]
    public Skill SkillWithDocumentUploads(SkillOptions? options = null)
    {
        var skill = SkillFactory.Create(
            name: "FullyDocumentedDebugging",
            description: "Debugging with auto-uploaded documentation",
            instructions: "Complete debugging workflow with all documentation",
            options: new SkillOptions()
                .AddDocumentFromFile(
                    filePath: "./docs/debugging-workflow.md",
                    description: "Step-by-step debugging methodology")
                .AddDocumentFromFile(
                    filePath: "./docs/api-reference.pdf",
                    description: "API reference for debugging operations",
                    documentId: "debug-api-ref"),
            "MockFileSystemPlugin.ReadFile",
            "MockFileSystemPlugin.WriteFile",
            "MockDebugPlugin.GetStackTrace"
        );

        // Validate
        ValidateSkill(skill, "FullyDocumentedDebugging", 3, 0, 2);
        ValidateDocumentUpload(skill.Options.DocumentUploads[0],
            "./docs/debugging-workflow.md",
            "debugging-workflow",  // Auto-derived
            "Step-by-step debugging methodology");
        ValidateDocumentUpload(skill.Options.DocumentUploads[1],
            "./docs/api-reference.pdf",
            "debug-api-ref",  // Explicit
            "API reference for debugging operations");

        return skill;
    }

    /// <summary>
    /// Test 5: Skill mixing both AddDocument and AddDocumentFromFile
    /// </summary>
    [Skill]
    public Skill MixedDocumentSkill(SkillOptions? options = null)
    {
        var skill = SkillFactory.Create(
            name: "ComprehensiveDebugging",
            description: "Complete debugging solution with mixed documentation",
            instructions: "Use all available resources for debugging",
            options: new SkillOptions()
                // Upload new documents
                .AddDocumentFromFile(
                    "./docs/local-guide.md",
                    "Local debugging guide")
                // Reference existing documents
                .AddDocument("company-standards")
                .AddDocument("shared-troubleshooting",
                    description: "Shared troubleshooting procedures"),
            "MockFileSystemPlugin.ReadFile",
            "MockDebugPlugin.GetStackTrace"
        );

        // Validate
        ValidateSkill(skill, "ComprehensiveDebugging", 2, 2, 1);
        return skill;
    }

    /// <summary>
    /// Test 6: Validate auto-derived document IDs
    /// </summary>
    public static void TestDocumentIdDerivation()
    {
        var testCases = new[]
        {
            ("./docs/debugging-workflow.md", "debugging-workflow"),
            ("./docs/API_Reference.pdf", "api-reference"),
            ("./docs/Error Codes.docx", "error-codes"),
            ("guide.txt", "guide"),
            ("./deep/nested/path/document.md", "document")
        };

        foreach (var (filePath, expectedId) in testCases)
        {
            var skill = SkillFactory.Create(
                "Test",
                "Test",
                "Test",
                options: new SkillOptions().AddDocumentFromFile(filePath, "Test description"),
                "MockFileSystemPlugin.ReadFile"
            );

            var actualId = skill.Options.DocumentUploads[0].DocumentId;
            if (actualId != expectedId)
            {
                throw new Exception(
                    $"Document ID derivation failed for '{filePath}': " +
                    $"expected '{expectedId}', got '{actualId}'");
            }
        }

        Console.WriteLine("✅ All document ID derivation tests passed!");
    }

    /// <summary>
    /// Test 7: Validate error handling
    /// </summary>
    public static void TestErrorHandling()
    {
        // Test empty name
        try
        {
            SkillFactory.Create("", "Description", "Instructions");
            throw new Exception("Should have thrown ArgumentException for empty name");
        }
        catch (ArgumentException) { /* Expected */ }

        // Test empty description
        try
        {
            SkillFactory.Create("Name", "", "Instructions");
            throw new Exception("Should have thrown ArgumentException for empty description");
        }
        catch (ArgumentException) { /* Expected */ }

        // Test empty document ID
        try
        {
            new SkillOptions().AddDocument("");
            throw new Exception("Should have thrown ArgumentException for empty document ID");
        }
        catch (ArgumentException) { /* Expected */ }

        // Test empty file path
        try
        {
            new SkillOptions().AddDocumentFromFile("", "Description");
            throw new Exception("Should have thrown ArgumentException for empty file path");
        }
        catch (ArgumentException) { /* Expected */ }

        // Test empty description in AddDocumentFromFile
        try
        {
            new SkillOptions().AddDocumentFromFile("./file.md", "");
            throw new Exception("Should have thrown ArgumentException for empty description");
        }
        catch (ArgumentException) { /* Expected */ }

        // Test whitespace-only description override in AddDocument
        try
        {
            new SkillOptions().AddDocument("doc-id", "   ");
            throw new Exception("Should have thrown ArgumentException for whitespace description");
        }
        catch (ArgumentException) { /* Expected */ }

        Console.WriteLine("✅ All error handling tests passed!");
    }

    /// <summary>
    /// Test 8: Validate fluent API (method chaining)
    /// </summary>
    public static void TestFluentApi()
    {
        var options = new SkillOptions()
            .AddDocument("doc1")
            .AddDocument("doc2", "Description 2")
            .AddDocumentFromFile("./file1.md", "File 1")
            .AddDocumentFromFile("./file2.md", "File 2", "custom-id");

        if (options.DocumentReferences.Count != 2)
            throw new Exception("Expected 2 document references");

        if (options.DocumentUploads.Count != 2)
            throw new Exception("Expected 2 document uploads");

        Console.WriteLine("✅ Fluent API test passed!");
    }

    // Validation Helpers

    private static void ValidateSkill(
        Skill skill,
        string expectedName,
        int expectedRefCount,
        int expectedDocRefs,
        int expectedDocUploads)
    {
        if (skill.Name != expectedName)
            throw new Exception($"Name mismatch: expected '{expectedName}', got '{skill.Name}'");

        if (skill.References.Length != expectedRefCount)
            throw new Exception($"Reference count mismatch: expected {expectedRefCount}, got {skill.References.Length}");

        if (skill.Options.DocumentReferences.Count != expectedDocRefs)
            throw new Exception($"Document reference count mismatch: expected {expectedDocRefs}, got {skill.Options.DocumentReferences.Count}");

        if (skill.Options.DocumentUploads.Count != expectedDocUploads)
            throw new Exception($"Document upload count mismatch: expected {expectedDocUploads}, got {skill.Options.DocumentUploads.Count}");
    }

    private static void ValidateDocumentReference(
        DocumentReference docRef,
        string expectedId,
        string? expectedDescription)
    {
        if (docRef.DocumentId != expectedId)
            throw new Exception($"Document ID mismatch: expected '{expectedId}', got '{docRef.DocumentId}'");

        if (docRef.DescriptionOverride != expectedDescription)
            throw new Exception($"Description override mismatch: expected '{expectedDescription}', got '{docRef.DescriptionOverride}'");
    }

    private static void ValidateDocumentUpload(
        DocumentUpload upload,
        string expectedFilePath,
        string expectedId,
        string expectedDescription)
    {
        if (upload.FilePath != expectedFilePath)
            throw new Exception($"File path mismatch: expected '{expectedFilePath}', got '{upload.FilePath}'");

        if (upload.DocumentId != expectedId)
            throw new Exception($"Document ID mismatch: expected '{expectedId}', got '{upload.DocumentId}'");

        if (upload.Description != expectedDescription)
            throw new Exception($"Description mismatch: expected '{expectedDescription}', got '{upload.Description}'");
    }

    /// <summary>
    /// Run all tests
    /// </summary>
    public static void RunAllTests()
    {
        Console.WriteLine("=== Phase 1 API Tests ===\n");

        var test = new Phase1ApiTest();

        // Test 1: Simple skill
        Console.WriteLine("Running Test 1: Simple skill without options...");
        var skill1 = test.SimpleSkill();
        Console.WriteLine($"✅ Created skill: {skill1.Name} with {skill1.References.Length} references\n");

        // Test 2: Skill with options
        Console.WriteLine("Running Test 2: Skill with options...");
        var skill2 = test.SkillWithOptions();
        Console.WriteLine($"✅ Created skill: {skill2.Name} with {skill2.References.Length} references\n");

        // Test 3: Skill with document references
        Console.WriteLine("Running Test 3: Skill with document references...");
        var skill3 = test.SkillWithDocumentReferences();
        Console.WriteLine($"✅ Created skill: {skill3.Name} with {skill3.Options.DocumentReferences.Count} document references\n");

        // Test 4: Skill with document uploads
        Console.WriteLine("Running Test 4: Skill with document uploads...");
        var skill4 = test.SkillWithDocumentUploads();
        Console.WriteLine($"✅ Created skill: {skill4.Name} with {skill4.Options.DocumentUploads.Count} document uploads\n");

        // Test 5: Mixed documents
        Console.WriteLine("Running Test 5: Skill with mixed documents...");
        var skill5 = test.MixedDocumentSkill();
        Console.WriteLine($"✅ Created skill: {skill5.Name} with mixed documentation\n");

        // Test 6: Document ID derivation
        Console.WriteLine("Running Test 6: Document ID derivation...");
        TestDocumentIdDerivation();
        Console.WriteLine();

        // Test 7: Error handling
        Console.WriteLine("Running Test 7: Error handling...");
        TestErrorHandling();
        Console.WriteLine();

        // Test 8: Fluent API
        Console.WriteLine("Running Test 8: Fluent API...");
        TestFluentApi();
        Console.WriteLine();

        Console.WriteLine("=== All Phase 1 API Tests Passed! ✅ ===");
    }
}
