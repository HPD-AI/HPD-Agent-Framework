# Problem Space Analysis: Skill Instruction Documents

**Date:** 2025-11-02
**Status:** Analysis
**Context:** Unified Skill Architecture Proposal

---

## Executive Summary

This document analyzes the problem space for instruction document management in the HPD-Agent skill system. The core challenge is bridging the **compile-time world** (where skill code lives) with the **runtime world** (where documents can be stored flexibly), while maintaining simplicity for common cases and power for edge cases.

**Critical Discovery:** Unlike memory (1 agent → 1 store, query-based) or plugins (self-contained, type-safe), instruction documents have a **shared store architecture** (1 store → N skills with hard-coded string references) that creates unique coordination, serialization, and namespace challenges not present in other framework components.

**Key Findings:** Every approach to instruction document management creates tension between:
- Type safety vs. storage flexibility
- Plugin autonomy vs. centralized control
- Zero-config simplicity vs. multi-environment power
- Compile-time knowledge vs. runtime resolution
- **Serialized references vs. runtime-configured stores** (NEW)
- **Shared infrastructure vs. plugin isolation** (NEW)

---

## Table of Contents

1. [The Actors](#the-actors)
2. [The Constraints](#the-constraints)
3. [The Scenarios](#the-scenarios)
4. [The Tensions](#the-tensions)
5. [The Data Flow Problem](#the-data-flow-problem)
6. [The Core Questions](#the-core-questions)
7. [The Dependencies](#the-dependencies)
8. [The Design Space](#the-design-space)
9. [The Scale Considerations](#the-scale-considerations)
10. [The Paradoxes](#the-paradoxes)
11. [The Real Problem](#the-real-problem)
12. [**The Missing Dimension: Shared Store Architecture**](#the-missing-dimension-shared-store-architecture) ⭐ **NEW**

---

## The Actors

### 1. Plugin Developer (Library Author)
**Role:** Creates reusable plugins with skills

**Needs:**
- Write `[Skill]` methods with comprehensive instructions
- Ship instructions as part of the plugin package
- Have things "just work" when users install plugin
- Not know user's deployment environment in advance

**Constraints:**
- Cannot predict where documents will be stored at runtime
- Cannot access user's infrastructure (DB, S3, etc.)
- Must work across multiple user environments

**Example:**
```csharp
// Plugin developer writes this:
[Skill]
public Skill FileDebugging(SkillOptions? options = null)
{
    return SkillFactory.Create(
        "FileDebugging",
        "Debug by analyzing log files",
        "????",  // ← What reference do I put here?
        FileSystemPlugin.ReadFile,
        DebugPlugin.GetStackTrace
    );
}
```

---

### 2. Library Author (Framework Provider - You)

**Role:** Provides the skill framework infrastructure

**Needs:**
- Support multiple storage backends (filesystem, DB, S3, GitHub)
- Balance flexibility vs. simplicity
- Maintain Native AOT compatibility
- Provide sensible defaults that work out-of-the-box
- Cannot predict all use cases

**Constraints:**
- Cannot mandate specific storage solution
- Must work for both small and large deployments
- Must maintain backward compatibility
- Cannot use runtime reflection (AOT requirement)

**Questions:**
- What abstraction to provide?
- Where to draw the line between framework and user responsibility?
- How to make simple cases simple without making complex cases impossible?

---

### 3. Application Developer (Library User)

**Role:** Consumes plugins and builds agents

**Needs:**
- Minimal configuration for simple cases
- Flexibility for complex enterprise scenarios
- Work across multiple environments (dev, staging, prod)
- Control where sensitive documents are stored

**Constraints:**
- May have specific infrastructure requirements
- May have compliance/security requirements
- May need per-tenant customization
- May have existing document management systems

**Example Environments:**
```
Dev:     Filesystem (./skills/documents/)
Staging: Database (shared across instances)
Prod:    S3 (CDN, versioned, audit trail)
```

---

### 4. End User / Ops Team

**Role:** Deploys and operates agents

**Needs:**
- Customize instructions per environment
- Hot-reload capability (change docs without redeployment)
- Audit trails for document changes
- Version control and rollback

**Constraints:**
- Cannot redeploy application for every doc change
- May have compliance requirements (e.g., audit all changes)
- May need per-tenant customization
- Limited technical knowledge of skill system internals

---

## Current Implementation (Filesystem-Only)

HPD-Agent currently has instruction document support built into the skill system. However, the current implementation is **filesystem-only** and does not satisfy the requirements described in this problem space.

### Existing Infrastructure

#### SkillDefinition.cs (Runtime Skills)

The runtime skill system (`SkillDefinition.cs`) currently supports instruction documents via:

```csharp
/// <summary>
/// Optional inline post-expansion instructions (shown after skill is activated).
/// </summary>
public string? PostExpansionInstructions { get; set; }

/// <summary>
/// Optional file paths to markdown documents containing post-expansion instructions.
/// Documents are loaded at Build() time and merged with PostExpansionInstructions.
/// Paths are validated for security (must be within approved base directory).
/// </summary>
public string[]? PostExpansionInstructionDocuments { get; set; }

/// <summary>
/// Base directory for instruction documents (defaults to "skills/documents/").
/// All document paths are resolved relative to this directory.
/// </summary>
public string InstructionDocumentBaseDirectory { get; set; } = "skills/documents/";
```

**Document Loading Implementation** ([SkillDefinition.cs:190-218](HPD-Agent/Skills/SkillDefinition.cs#L190-L218)):

```csharp
private string LoadInstructions()
{
    var instructions = new StringBuilder();

    // Add inline instructions first
    if (!string.IsNullOrEmpty(PostExpansionInstructions))
    {
        instructions.AppendLine(PostExpansionInstructions);
    }

    // Load and append document instructions
    if (PostExpansionInstructionDocuments != null && PostExpansionInstructionDocuments.Length > 0)
    {
        foreach (var documentPath in PostExpansionInstructionDocuments)
        {
            var content = LoadDocument(documentPath);
            if (!string.IsNullOrEmpty(content))
            {
                if (instructions.Length > 0)
                {
                    instructions.AppendLine(); // Separator between documents
                }
                instructions.AppendLine(content);
            }
        }
    }

    return instructions.Length > 0 ? instructions.ToString().Trim() : null;
}
```

**Document Resolution with Security Validation** ([SkillDefinition.cs:220-263](HPD-Agent/Skills/SkillDefinition.cs#L220-L263)):

```csharp
private string LoadDocument(string documentPath)
{
    // Resolve path relative to base directory
    var baseDirectory = Path.GetFullPath(InstructionDocumentBaseDirectory);
    var fullPath = Path.GetFullPath(Path.Combine(baseDirectory, documentPath));

    // Security: Validate path is within base directory (prevent path traversal)
    if (!fullPath.StartsWith(baseDirectory, StringComparison.OrdinalIgnoreCase))
    {
        throw new SecurityException(
            $"Skill '{Name}' document path '{documentPath}' is outside allowed directory '{baseDirectory}'. " +
            $"Resolved path: '{fullPath}'");
    }

    // Check file exists
    if (!File.Exists(fullPath))
    {
        throw new FileNotFoundException(
            $"Skill '{Name}' instruction document not found: '{documentPath}' (resolved to '{fullPath}')");
    }

    // Security: Validate file size (1MB limit)
    var fileInfo = new FileInfo(fullPath);
    if (fileInfo.Length > MAX_DOCUMENT_SIZE)
    {
        throw new InvalidOperationException(
            $"Skill '{Name}' document '{documentPath}' exceeds maximum size of {MAX_DOCUMENT_SIZE:N0} bytes " +
            $"(actual: {fileInfo.Length:N0} bytes)");
    }

    // Load document content
    return File.ReadAllText(fullPath);
}
```

---

#### SkillOptions.cs (Compile-Time Skills)

The compile-time skill system (`SkillOptions.cs`) has parallel support:

```csharp
/// <summary>
/// Optional file paths to markdown documents containing post-expansion instructions.
/// Documents are loaded at Build() time and merged with PostExpansionInstructions.
/// </summary>
public string[]? InstructionDocuments { get; set; }

/// <summary>
/// Base directory for instruction documents (defaults to "skills/documents/").
/// All document paths are resolved relative to this directory.
/// </summary>
public string InstructionDocumentBaseDirectory { get; set; } = "skills/documents/";
```

---

### Usage Example (Current System)

**Runtime Skills:**
```csharp
var skill = new SkillDefinition
{
    Name = "FileDebugging",
    Description = "Debug by analyzing log files",
    PluginReferences = new[] { "FileSystemPlugin", "DebugPlugin" },
    PostExpansionInstructionDocuments = new[]
    {
        "file-debugging-workflow.md",
        "troubleshooting-tips.md"
    },
    InstructionDocumentBaseDirectory = "skills/documents/"
};
```

**Compile-Time Skills:**
```csharp
[Skill]
public Skill FileDebugging(SkillOptions? options = null)
{
    return SkillFactory.Create(
        "FileDebugging",
        "Debug by analyzing log files",
        FileSystemPlugin.ReadFile,
        DebugPlugin.GetStackTrace,
        options ?? new SkillOptions
        {
            InstructionDocuments = new[]
            {
                "file-debugging-workflow.md",
                "troubleshooting-tips.md"
            },
            InstructionDocumentBaseDirectory = "skills/documents/"
        }
    );
}
```

---

### What Works (Current Implementation)

✅ **Security**: Path traversal protection and file size limits
✅ **Multiple Documents**: Can reference multiple document files
✅ **Inline + File Hybrid**: Supports both inline strings and file references
✅ **Base Directory Configuration**: Configurable base path for documents
✅ **Build-Time Loading**: Documents loaded at agent build time (fail-fast)

---

### Limitations and Why This Is Insufficient

#### 1. **Hardcoded to Filesystem Only**

The current implementation is **tightly coupled to the filesystem** via `File.ReadAllText()`:

```csharp
// ❌ This only works with local filesystem
return File.ReadAllText(fullPath);
```

**Problems:**
- ❌ **Doesn't work in cloud deployments** (Azure, AWS Lambda) without persistent storage
- ❌ **Doesn't work in containerized environments** with ephemeral filesystems
- ❌ **Cannot use database storage** (required for multi-instance deployments)
- ❌ **Cannot use S3/Blob storage** (required for CDN, versioning, compliance)
- ❌ **Cannot use GitHub as document source** (popular for documentation)
- ❌ **Cannot support user-created documents** stored in application database

**Real-World Impact:**
```
Dev Environment:     ✅ Works (local filesystem)
Staging (Database):  ❌ Breaks (no filesystem)
Production (S3):     ❌ Breaks (no filesystem)
```

---

#### 2. **No Multi-Environment Support**

Different environments require different storage backends, but current implementation doesn't support this:

| Environment | Required Storage | Current Support |
|------------|------------------|-----------------|
| Development | Filesystem (`./skills/documents/`) | ✅ Works |
| Staging | Shared Database (consistency across instances) | ❌ Not supported |
| Production | S3/Blob (CDN, audit trail, compliance) | ❌ Not supported |

**Problem Scenario:**
```csharp
// Same code must work in all environments, but can't:
var skill = new SkillDefinition
{
    InstructionDocumentBaseDirectory = "skills/documents/"  // ← Hardcoded to filesystem
};

// In production, this fails because there is no filesystem
```

---

#### 3. **No Storage Abstraction Layer**

The current implementation has **no abstraction** between "document reference" and "document storage":

```csharp
// Current: Direct coupling to File I/O
private string LoadDocument(string documentPath)
{
    var fullPath = Path.Combine(baseDirectory, documentPath);
    return File.ReadAllText(fullPath);  // ← Tightly coupled
}

// What we need: Abstraction over storage
private async Task<string> LoadDocument(string documentKey)
{
    return await documentStore.ResolveDocumentAsync(documentKey);  // ← Storage-agnostic
}
```

**Missing Abstractions:**
- ❌ No `IInstructionDocumentStore` interface
- ❌ No pluggable storage backends
- ❌ No storage-agnostic document keys
- ❌ No resolution strategy pattern

---

#### 4. **No Hot-Reload Capability**

Documents are loaded **once at build time** and never refreshed:

```csharp
public void Build(Dictionary<string, AIFunction> allFunctions)
{
    // Load and merge all instruction documents
    ResolvedInstructions = LoadInstructions();  // ← Loaded once, cached forever
}
```

**Problems:**
- ❌ **Cannot update instructions without restarting** agent
- ❌ **Cannot fix compliance issues in real-time** (ops team requirement)
- ❌ **Cannot A/B test different instruction sets**
- ❌ **No cache invalidation mechanism**

**Real-World Impact:**
```
Ops Team:
1. Discovers critical error in instruction document
2. Fixes document in store
3. ??? Agent still uses old cached version
4. Must restart all agent instances (downtime, coordination)
```

---

#### 5. **No Centralized Management or Audit Trail**

Each skill independently loads its own documents with no central oversight:

**Problems:**
- ❌ **No visibility into which documents are loaded**
- ❌ **No audit trail of document changes**
- ❌ **No versioning of document content**
- ❌ **No rollback capability**
- ❌ **No compliance reporting** ("which agents use which instructions?")

**Enterprise Requirements Not Met:**
- Compliance: "Show me all instruction changes in the last 30 days"
- Security: "Who has permission to modify skill instructions?"
- Operations: "Which agents are using the old version of this document?"

---

#### 6. **No Universal Document References**

Documents are referenced by **filesystem paths**, not abstract keys:

```csharp
// Current: Filesystem-specific paths
PostExpansionInstructionDocuments = new[]
{
    "file-debugging-workflow.md",      // ← Relative file path
    "troubleshooting-tips.md"
};

// What we need: Storage-agnostic keys
InstructionDocumentReferences = new[]
{
    "doc://file-debugging-workflow",   // ← Abstract key, resolved by store
    "doc://troubleshooting-tips"
};
```

**Problems:**
- ❌ **Keys are tied to filesystem structure** (can't reorganize without breaking references)
- ❌ **No namespace management** (risk of collisions across plugins)
- ❌ **Plugin developers must know directory structure** (breaks abstraction)
- ❌ **Cannot reference documents in database/S3** (no URI scheme)

---

#### 7. **No Discovery or Registration Mechanism**

There is **no standard way** for plugins to ship documents and have them automatically registered:

**Current Problem Flow:**
```
1. Plugin Developer writes skill + documents
2. Plugin Developer packages as NuGet
3. Application Developer installs package
4. ??? How do documents get into "skills/documents/" directory?
5. Manual copy? Build script? Content files?
6. Easy to forget, easy to misconfigure
```

**Missing Capabilities:**
- ❌ No automatic document discovery from plugins
- ❌ No registration API (`documentStore.RegisterDocument(key, content)`)
- ❌ No plugin-to-store bridge
- ❌ No versioning or conflict resolution

---

#### 8. **Limited Error Handling**

Current implementation throws exceptions for missing documents:

```csharp
if (!File.Exists(fullPath))
{
    throw new FileNotFoundException(...);  // ← Hard failure
}
```

**Problems:**
- ❌ **No fallback strategy** (inline default, warning + continue, etc.)
- ❌ **No graceful degradation** (skill becomes completely unusable)
- ❌ **No retry mechanism** (version variants, alternate sources)
- ❌ **Errors discovered at runtime**, not compile-time

---

### Why This Matters (Real-World Scenarios Broken)

#### Scenario 1: Multi-Instance Web Application
```
Application deployed across 3 instances:
- Instance 1: ✅ Loads documents from filesystem
- Instance 2: ✅ Loads documents from filesystem
- Ops updates document on Instance 1 only
- Instance 3: ❌ Has stale version

Problem: No shared document store, no cache invalidation
```

#### Scenario 2: Containerized Deployment (Docker/Kubernetes)
```
Container starts with ephemeral filesystem:
- skills/documents/ exists in container
- Container restarts → filesystem wiped
- Documents lost unless rebuilt into image

Problem: No persistent storage backend
```

#### Scenario 3: Plugin Distribution via NuGet
```
Plugin Developer ships MyPlugin.nupkg:
- Contains Skill code ✅
- Contains document files in package ✅
- User installs package ✅
- ??? Documents don't end up in skills/documents/ automatically
- User must manually copy files or configure build

Problem: No automatic registration mechanism
```

#### Scenario 4: Compliance-Driven Enterprise
```
Enterprise requirements:
- All instruction changes must be audited
- Documents must be versioned
- Rollback capability required
- Hot-reload for critical fixes

Problem: Current implementation supports none of these
```

---

### Summary: Current vs. Required

| Capability | Current Implementation | Required |
|-----------|----------------------|----------|
| **Storage Backend** | Filesystem only | Pluggable (filesystem, DB, S3, GitHub) |
| **Multi-Environment** | Single environment | Dev/Staging/Prod with different backends |
| **Document References** | Filesystem paths | Abstract keys (`doc://`) |
| **Loading Strategy** | Build-time, cached forever | Build-time + hot-reload option |
| **Discovery** | Manual file management | Automatic from plugin packages |
| **Centralization** | Each skill loads independently | Optional centralized store |
| **Audit Trail** | None | Full history of changes |
| **Versioning** | None | Document versioning + rollback |
| **Access Control** | File permissions only | Store-level permissions |
| **Error Handling** | Hard failure (exception) | Graceful degradation + fallbacks |

---

**Conclusion**: The current implementation provides a **solid foundation** with security validation and multi-document support, but is **fundamentally limited** by its tight coupling to the filesystem. The requirements identified in this problem space (multi-environment support, storage abstraction, hot-reload, centralized management) **cannot be satisfied** without introducing a storage abstraction layer similar to the memory store pattern.

---

## The Constraints

### Technical Constraints

#### 1. Native AOT Compatibility
**Limitation:** Cannot use runtime reflection for document discovery

**Implications:**
- All file paths must be deterministic at compile time
- Cannot scan assemblies for embedded resources dynamically
- Cannot use `Assembly.GetManifestResourceNames()` at runtime
- Source generator must know paths at compile time

**Example Problem:**
```csharp
// ❌ Cannot do this (reflection):
var resources = Assembly.GetExecutingAssembly().GetManifestResourceNames();
var docs = resources.Where(r => r.StartsWith("Skills.Documents."));

// ✅ Must do this (deterministic):
var doc1 = LoadEmbeddedResource("Skills.Documents.FileDebugging.md");
var doc2 = LoadEmbeddedResource("Skills.Documents.DatabaseOps.md");
```

---

#### 2. NuGet Package Limitations

**Content Files:**
- Copied to user's project output directory
- Increases project clutter
- User can accidentally modify/delete them
- Not suitable for large documents

**Embedded Resources:**
- Increases assembly size
- Cannot be updated without recompiling
- Difficult to discover/enumerate (AOT limitation)
- No hot-reload capability

**Example:**
```xml
<!-- NuGet package structure -->
<files>
  <!-- Option A: Content files (copied to output) -->
  <file src="skills\documents\*.md" target="content\skills\documents" />

  <!-- Option B: Embedded resources (in assembly) -->
  <file src="skills\documents\*.md" target="lib\net9.0" />
</files>
```

---

#### 3. Multi-Environment Reality

**Different storage per environment:**

| Environment | Storage | Reason | Current Support |
|------------|---------|--------|-----------------|
| Development | Filesystem | Fast iteration, easy debugging | ✅ Supported |
| Staging | Database | Shared across instances, testing prod-like setup | ❌ Not supported |
| Production | S3/Blob | CDN, versioning, audit trail, compliance | ❌ Not supported |

**Problem:** Same plugin code must work in all environments without changes.

**Current Limitation:** The existing implementation only supports filesystem storage (see [No Multi-Environment Support](#2-no-multi-environment-support)).

---

#### 4. Version Skew Problem

**Scenario:**
```
Time 0: Plugin v1.0 shipped
        - References "doc://file-debugging-v1"
        - Document uploaded to store

Time 1: Plugin v2.0 released
        - References "doc://file-debugging-v2"
        - New document content

Time 2: User upgrades code to v2.0
        - But document store still has v1 content
        - OR has both v1 and v2
        - Which one to load?
```

**Coordination Problems:**
- How to version documents?
- How to migrate document content when upgrading plugins?
- What if users roll back code but not documents?
- How to handle breaking changes in instructions?

---

### Conceptual Constraints

#### 1. The Reference Problem

**Question:** What should skill code contain?

```csharp
[Skill]
public Skill MySkill(...)
{
    return SkillFactory.Create(
        "MySkill",
        "Description",
        "????",  // ← What goes here?
        MyPlugin.MyFunction
    );
}
```

**Current Implementation:** Uses filesystem paths via `SkillOptions.InstructionDocuments` (see [Current Implementation](#current-implementation-filesystem-only) section).

**Options:**

**Option A: Inline String**
```csharp
"Follow these steps: 1. Read file 2. Analyze content 3. Report findings"
```
- ✅ Self-contained
- ✅ Works anywhere
- ❌ Not maintainable for long instructions
- ❌ No reuse across skills
- ❌ No hot-reload

**Option B: Relative File Path** ← **Current Implementation**
```csharp
"skills/documents/file-debugging.md"
```
- ✅ External file (maintainable)
- ✅ Works for simple filesystem deployments
- ❌ Relative to what? (DLL location? Working directory?)
- ❌ Breaks in cloud deployments
- ❌ No abstraction over storage
- ❌ Limited to filesystem only (see [Limitations](#limitations-and-why-this-is-insufficient))

**Option C: Absolute File Path**
```csharp
"/app/skills/documents/file-debugging.md"
```
- ✅ Explicit
- ❌ Hardcoded to filesystem
- ❌ Different per environment
- ❌ Not portable

**Option D: URI Scheme**
```csharp
"file://skills/documents/file-debugging.md"
"https://docs.example.com/skills/file-debugging.md"
```
- ✅ Protocol-specific
- ✅ Can support multiple sources
- ❌ Who resolves these?
- ❌ How to handle auth?

**Option E: Abstract Key**
```csharp
"doc://file-debugging-workflow"
```
- ✅ Storage-agnostic
- ✅ Can point to any backend
- ❌ Who manages key-to-content mapping?
- ❌ When is content registered?
- ❌ No compile-time validation

---

#### 2. The Discovery Problem

**Question:** When plugin ships with documents, how does the store know about them?

**Timeline:**
```
1. Plugin Developer writes skill + documents
2. Plugin Developer packages as NuGet
3. Application Developer installs package
4. Application Developer registers plugin: .WithPlugin<MyPlugin>()
5. ??? Documents need to be in store somehow
6. Agent runs
7. Skill activated
8. Document content needed
```

**The Gap:** Steps 4-5. How do documents get from the package into the store?

**Sub-questions:**
- Does plugin registration automatically upload documents?
- Does app developer manually upload them?
- Are they discovered on-demand?
- What if store already has a document with that key?
- What if store is read-only (e.g., in prod)?

---

#### 3. The Lifecycle Problem

**Document Lifecycle:**
```
Creation → Packaging → Distribution → Registration → Storage → Resolution → Caching → Updates
```

**Each stage raises questions:**

**Creation:**
- Who creates documents? (Plugin dev, ops team, users)
- In what format? (Markdown, plain text, HTML)

**Packaging:**
- How are documents included? (Embedded, content files, separate package)
- How are they versioned?

**Distribution:**
- How do documents travel with code? (NuGet, Docker image, Git)
- Can documents be updated independently?

**Registration:**
- When are documents registered to store? (Install time, startup, lazy)
- What if registration fails?

**Storage:**
- Where are documents stored? (Filesystem, DB, S3, multiple)
- How are they organized? (Flat, hierarchical, by plugin, by version)

**Resolution:**
- When is content loaded? (Startup, first use, every use)
- What if document not found?

**Caching:**
- Should content be cached? (Memory, disk)
- When to invalidate cache?

**Updates:**
- How to update documents? (Redeploy, API, UI)
- How to propagate updates? (Restart, hot-reload)

---

## The Scenarios

### Scenario 1: Simple Plugin with Instructions (90% of cases)

**Actors:** Plugin Developer → Application Developer

**Story:**
```
Plugin Developer:
1. Writes [Skill] method
2. Writes comprehensive instructions (3-page markdown doc)
3. Ships as NuGet package

Application Developer:
1. Installs package: dotnet add package MyPlugin
2. Registers plugin: .WithPlugin<MyPlugin>()
3. Runs agent
4. Expects everything to work
```

**Reality Check Questions:**
- ❓ Where are the instruction documents?
- ❓ How did they get there?
- ❓ What if app is deployed to Azure (no local filesystem)?
- ❓ What if app runs in container (ephemeral filesystem)?
- ❓ What if multiple instances share data (need centralized store)?

**Current Pain:**
- No standard way to ship documents with plugins
- No automatic registration mechanism
- User must manually set up document infrastructure

---

### Scenario 2: Multi-Environment Deployment (7% of cases)

**Actors:** Application Developer + Ops Team

**Story:**
```
Application Developer:
1. Develops with filesystem (./skills/documents/)
2. Tests in staging with database (shared across instances)
3. Deploys to prod with S3 (CDN, compliance, audit)

Requirements:
- Same plugin code in all environments
- No environment-specific code
- Documents automatically available in each environment
```

**Reality Check Questions:**
- ❓ How to avoid "document not found" errors across environments?
- ❓ Who manages document synchronization?
- ❓ How to test document changes before prod?
- ❓ What if dev uses v1 docs but prod has v2?

**Current Pain:**
- Must configure different stores per environment
- Document sync between environments is manual
- Easy to have version skew

---

### Scenario 3: Dynamic/User-Created Skills (2% of cases)

**Actors:** End User (via UI)

**Story:**
```
End User:
1. Opens skill management UI
2. Creates custom skill named "MyWorkflow"
3. Writes custom instructions in UI
4. Saves to database
5. Starts using skill in conversations
```

**Reality Check Questions:**
- ❓ How to reference user-created documents?
- ❓ Different namespace from plugin documents?
- ❓ How to prevent key collisions with plugin docs?
- ❓ How to handle permissions (user A can't see user B's docs)?

**Current Pain:**
- No standard key namespace convention
- User-created docs mixed with system docs
- No access control on documents

---

### Scenario 4: Hot-Reload / Live Updates (1% of cases)

**Actors:** Ops Team

**Story:**
```
Ops Team:
1. Discovers compliance issue in instruction document
2. Updates document in store
3. Wants all agents to use new version immediately
4. No redeployment, no restart
```

**Reality Check Questions:**
- ❓ How to invalidate cached copies?
- ❓ How to ensure version compatibility with skill code?
- ❓ What if update breaks existing workflows?
- ❓ Rollback strategy if bad update?

**Current Pain:**
- Caching makes hot-reload difficult
- No versioning of document updates
- No rollback mechanism

---

## The Tensions

### Tension 1: Compile-Time vs Runtime

**The Problem:**
```csharp
// Compile-Time (skill code written by plugin developer):
[Skill]
public Skill MySkill(...) {
    return SkillFactory.Create(..., "instructions-reference-here", ...);
}

// Runtime (document storage determined by app developer):
builder.WithInstructionDocumentStore(new S3InstructionDocumentStore(...));
```

**The Tension:**
- Instructions reference is **written at compile-time**
- Storage location is **chosen at runtime**
- How to bridge this gap?

**Implications:**
- Can't hardcode storage location in skill code
- Can't validate document exists at compile-time
- Can't use type-safe references to documents
- Must use string-based references (fragile)

---

### Tension 2: Type Safety vs Flexibility

**The Dilemma:**

**Type-Safe Reference (compile-time validation):**
```csharp
MyPlugin.Documents.FileDebuggingWorkflow  // ← Doesn't exist (documents aren't code)
```
- ✅ Refactoring-safe
- ✅ IDE autocomplete
- ✅ Compile errors if document removed
- ❌ Documents must be known at compile-time
- ❌ Can't load documents from DB/S3
- ❌ Can't have user-created documents

**Flexible Reference (runtime validation):**
```csharp
"doc://file-debugging-workflow"  // ← String (no compile-time checking)
```
- ✅ Can point to any storage backend
- ✅ Can be user-created
- ✅ Can be updated without recompile
- ❌ No compile-time validation
- ❌ Typos discovered at runtime
- ❌ Refactoring doesn't update references

**The Tension:** Can't have both. Must choose one or find a middle ground.

---

### Tension 3: Simplicity vs Power

**Simple Approach:**
```csharp
[Skill]
public Skill MySkill(...) {
    return SkillFactory.Create(...,
        "See documentation at docs/readme.md",  // ← Inline reference
        ...);
}
```
- ✅ Zero configuration
- ✅ Works everywhere
- ✅ No external dependencies
- ❌ No abstraction over storage
- ❌ No multi-environment support
- ❌ No hot-reload capability
- ❌ Hardcoded to filesystem

**Powerful Approach:**
```csharp
[Skill]
public Skill MySkill(...) {
    return SkillFactory.Create(...,
        "doc://my-skill-instructions",  // ← Abstract key
        ...);
}

// Configuration required:
builder
    .WithInstructionDocumentStore(new S3InstructionDocumentStore(...))
    .WithDocumentRegistration(...)
```
- ✅ Storage abstraction
- ✅ Multi-environment support
- ✅ Hot-reload capable
- ✅ Centralized management
- ❌ Requires setup and configuration
- ❌ More moving parts
- ❌ Key management overhead
- ❌ Additional infrastructure

**The Tension:** Simple approach insufficient for production; powerful approach too complex for getting started.

---

### Tension 4: Plugin Autonomy vs Centralized Control

**Plugin Autonomy Model:**
```
Plugin ships with everything it needs:
- Code (skills, functions)
- Documents (instructions)
- Configuration (defaults)

Benefits:
✅ Self-contained
✅ Works out of the box
✅ No external dependencies
✅ Easy distribution (single NuGet package)

Drawbacks:
❌ Organization can't control document content
❌ Can't enforce formatting standards
❌ Can't audit document changes
❌ Can't apply compliance updates globally
```

**Centralized Control Model:**
```
Organization manages all documents:
- Documents stored in central store
- Plugins reference by key
- Ops team controls content

Benefits:
✅ Consistent formatting
✅ Compliance enforcement
✅ Audit trails
✅ Global updates

Drawbacks:
❌ Plugins not self-sufficient
❌ Additional infrastructure required
❌ Manual document upload needed
❌ Version coordination complex
```

**The Tension:** Can't fully satisfy both. Need to choose primary model and provide escape hatches.

---

## The Data Flow Problem

### Flow 1: Plugin → User (Code Distribution)

**The Journey:**
```
1. Plugin Developer writes skill code
   ↓
2. Plugin Developer writes instruction documents
   ↓
3. Plugin Developer creates NuGet package
   ↓
4. Application Developer installs package (dotnet add package MyPlugin)
   ↓
5. ??? HOW DO DOCUMENTS GET FROM PACKAGE TO STORE ???
   ↓
6. Application Developer registers plugin (.WithPlugin<MyPlugin>())
   ↓
7. Agent runs
```

**The Gap:** Step 5. No clear mechanism for documents to flow from package to store.

**Questions:**
- When should documents be uploaded to store?
  - At package install time? (How? NuGet doesn't have hooks)
  - At first app run? (Requires discovery mechanism)
  - At plugin registration? (Requires store to be configured first)
  - Manually by user? (Defeats "batteries included" goal)

- Where should documents go?
  - Copied to output directory? (Works for filesystem only)
  - Embedded in assembly? (Increases size, no hot-reload)
  - Separate package? (Deployment complexity)

- What if store already has documents?
  - Overwrite? (Might lose user customizations)
  - Skip? (Might keep stale versions)
  - Merge? (Complex logic)
  - Version? (Requires versioning scheme)

---

### Flow 2: Compile-Time → Runtime (Reference Resolution)

**The Journey:**
```
1. [Skill] method references "doc://my-doc" (compile-time)
   ↓
2. Source generator processes the reference (compile-time)
   ↓
3. ??? WHAT CAN SOURCE GENERATOR DO WITH IT ???
   ↓
4. Generated code includes reference (compile-time)
   ↓
5. Agent built with plugin (runtime startup)
   ↓
6. Agent runs, skill activated (runtime)
   ↓
7. Need to resolve "doc://my-doc" → actual content (runtime)
   ↓
8. ??? WHO RESOLVES? WHEN? FROM WHERE ???
```

**Source Generator Limitations:**
- Cannot access runtime store at compile-time
- Cannot validate document exists
- Cannot embed document content (might be in DB/S3)
- Can only pass reference through to runtime

**Runtime Resolution Questions:**
- Who resolves document keys?
  - SkillManager?
  - InstructionDocumentStore?
  - Custom resolver?

- When to resolve?
  - At agent build time (eager)?
  - At first skill use (lazy)?
  - Every skill activation (always fresh)?

- What if resolution fails?
  - Throw exception?
  - Return empty instructions?
  - Fall back to inline string?
  - Log warning and continue?

---

### Flow 3: Update → Propagation (Document Updates)

**The Journey:**
```
1. Ops team updates document in store
   ↓
2. ??? DOES UPDATE APPLY IMMEDIATELY ???
   ↓
3. Running agents with cached copies
   ↓
4. ??? HOW TO INVALIDATE CACHE ???
   ↓
5. Multiple agent instances
   ↓
6. ??? HOW TO PROPAGATE UPDATE ???
   ↓
7. Skill code expects certain instruction format
   ↓
8. ??? VERSION COMPATIBILITY CHECK ???
```

**Caching Challenges:**
- Memory cache per agent instance
- Distributed cache across instances
- CDN cache (if documents served via HTTP)
- Browser cache (if agent has web UI)

**Invalidation Strategies:**
- Time-based (expires after N minutes)
- Event-based (store notifies on change)
- Version-based (check version on each use)
- Manual (require restart to pick up changes)

**Propagation Problem:**
```
Agent Instance 1: Has cached v1 of document
Agent Instance 2: Has cached v1 of document
Agent Instance 3: Just started, loads v2 of document

Result: Inconsistent behavior across instances
```

---

## The Core Questions

### Question 1: Reference Format

**What should skill code contain?**

**Options:**

**A. Inline String**
```csharp
return SkillFactory.Create(...,
    "Follow these steps:\n1. Read file\n2. Analyze\n3. Report",
    ...);
```
- Use case: Short instructions (< 200 chars)
- Pros: Self-contained, always available
- Cons: Not maintainable, no reuse, no hot-reload

**B. Relative File Path**
```csharp
return SkillFactory.Create(...,
    "skills/documents/file-debugging.md",
    ...);
```
- Use case: Filesystem-based deployments
- Pros: External file, easy to edit
- Cons: Relative to what? Breaks in cloud

**C. Absolute File Path**
```csharp
return SkillFactory.Create(...,
    "/app/skills/documents/file-debugging.md",
    ...);
```
- Use case: Controlled environments
- Pros: Explicit, no ambiguity
- Cons: Not portable, hardcoded

**D. URI with Scheme**
```csharp
return SkillFactory.Create(...,
    "file://skills/documents/file-debugging.md",
    ...);
```
- Use case: Mixed storage (file, http, etc.)
- Pros: Protocol-specific, extensible
- Cons: Who resolves? Complex

**E. Abstract Key**
```csharp
return SkillFactory.Create(...,
    "doc://file-debugging-workflow",
    ...);
```
- Use case: Storage-agnostic
- Pros: Most flexible, works with any backend
- Cons: Key management, no validation

**F. Multiple References**
```csharp
return SkillFactory.Create(...,
    new[] {
        "doc://overview",
        "doc://detailed-steps",
        "doc://troubleshooting"
    },
    ...);
```
- Use case: Modular instructions
- Pros: Composable, reusable chunks
- Cons: More complex, ordering matters

---

### Question 2: Discovery Timing

**When are documents discovered/registered?**

**Option A: Package Install Time**
```bash
dotnet add package MyPlugin
# → NuGet hook uploads documents to store
```
- Pros: Automatic, user doesn't think about it
- Cons: NuGet doesn't have post-install hooks in .NET, requires custom tooling

**Option B: Application Startup**
```csharp
// On app startup, scan for document files
var docs = Directory.GetFiles("skills/documents/", "*.md");
foreach (var doc in docs) {
    await store.UploadDocumentAsync(doc);
}
```
- Pros: Automatic, happens once
- Cons: Startup time, assumes filesystem, what about updates?

**Option C: Plugin Registration**
```csharp
.WithPlugin<MyPlugin>()
// → Discovers and uploads MyPlugin's documents
```
- Pros: Automatic, tied to plugin lifecycle
- Cons: Requires plugin to know about its documents, stores must be configured first

**Option D: First Skill Activation**
```csharp
// First time skill used, load its documents
await agent.RunAsync("Use FileDebugging skill");
// → Lazy load documents on-demand
```
- Pros: Lazy, only loads what's needed
- Cons: First use is slow, failure happens late

**Option E: Manual Upload**
```csharp
// User explicitly uploads documents
await documentStore.UploadDocumentAsync(
    "file-debugging-workflow",
    File.ReadAllText("docs/file-debugging.md")
);
```
- Pros: Explicit control, user knows what's happening
- Cons: Manual, error-prone, defeats "batteries included"

---

### Question 3: Responsibility Assignment

**Who handles document management?**

**Model A: Plugin Developer**
```
Plugin Developer:
- Ships documents with code
- Documents are part of the plugin package
- User gets everything automatically

Implications:
✅ Self-contained plugins
✅ Works out of box
❌ Org can't control content
❌ Requires standard packaging
```

**Model B: Library (Framework)**
```
Library:
- Provides infrastructure (store abstraction)
- Handles registration and resolution
- User just configures backend

Implications:
✅ Consistent experience
✅ Powerful abstractions
❌ Complex API surface
❌ More framework responsibility
```

**Model C: Application Developer**
```
Application Developer:
- Wires everything up
- Uploads documents manually
- Configures stores

Implications:
✅ Full control
✅ Flexible
❌ Manual work
❌ Easy to misconfigure
```

**Model D: Ops Team**
```
Ops Team:
- Manages documents in production
- Updates via UI/API
- Controls content policy

Implications:
✅ Centralized management
✅ Compliance enforcement
❌ Disconnect from code
❌ Version coordination
```

**Model E: Hybrid**
```
Plugin Developer: Ships defaults
Library: Provides infrastructure
App Developer: Configures for environment
Ops Team: Updates in production

Implications:
✅ Balances concerns
✅ Flexible
❌ Complex mental model
❌ More moving parts
```

---

### Question 4: Storage Location

**Where do documents live?**

**Option A: In Plugin Assembly (Embedded Resources)**
```csharp
[assembly: EmbeddedResource("Skills.Documents.FileDebugging.md")]
```
- Pros: Always available, travels with code, no external files
- Cons: Increases assembly size, no hot-reload, difficult to enumerate (AOT)

**Option B: Next to Plugin DLL (Content Files)**
```
MyPlugin.dll
skills/
  └── documents/
      ├── file-debugging.md
      └── database-ops.md
```
- Pros: Easy to edit, external files, no assembly bloat
- Cons: Can be separated from DLL, not cloud-friendly

**Option C: In Application Directory (Copied at Build)**
```
MyApp.exe
skills/
  └── documents/
      ├── file-debugging.md  (from Plugin A)
      └── api-debugging.md   (from Plugin B)
```
- Pros: Centralized, easy to find
- Cons: Collisions, managed by build process, not portable

**Option D: In External Store (DB/S3/GitHub)**
```
Database table: InstructionDocuments
- Id: "file-debugging-workflow"
- Content: "..."
- Version: 2
- UpdatedAt: 2025-11-02
```
- Pros: Centralized, hot-reload, versioned, multi-instance safe
- Cons: Requires infrastructure, manual upload, network dependency

**Option E: Mixed (Different Environments)**
```
Dev:     Filesystem (fast iteration)
Staging: Database (prod-like)
Prod:    S3 (CDN, compliance)
```
- Pros: Optimal for each environment
- Cons: Complexity, sync between environments

---

### Question 5: Key Namespace

**How to avoid collisions?**

**Approach A: Flat Global Namespace**
```
"file-debugging"
"database-ops"
"api-troubleshooting"
```
- Pros: Simple
- Cons: Collisions likely, no organization

**Approach B: Plugin-Scoped Keys**
```
"MyPlugin/file-debugging"
"MyPlugin/database-ops"
"AnotherPlugin/file-debugging"  // ← No collision
```
- Pros: Prevents collisions, clear ownership
- Cons: Longer keys, plugin name in every reference

**Approach C: Versioned Keys**
```
"MyPlugin/v1/file-debugging"
"MyPlugin/v2/file-debugging"
```
- Pros: Can keep multiple versions, explicit
- Cons: Manual version coordination, key proliferation

**Approach D: User-Scoped Keys**
```
"tenant-123/custom-workflow"
"tenant-456/custom-workflow"
```
- Pros: Multi-tenancy support, isolation
- Cons: More complex, needs user context

**Approach E: Hierarchical Namespace**
```
"skills/debugging/file-operations"
"skills/debugging/api-operations"
"skills/database/migrations"
```
- Pros: Organized, discoverable
- Cons: Deep nesting, longer keys

---

### Question 6: Failure Handling

**What if document not found?**

**Strategy A: Fail Skill Activation**
```csharp
if (!await store.DocumentExistsAsync(key)) {
    throw new DocumentNotFoundException($"Document '{key}' not found");
}
```
- Pros: Explicit failure, user knows there's a problem
- Cons: Skill becomes unusable, hard errors

**Strategy B: Return Empty Instructions**
```csharp
var content = await store.ResolveDocumentAsync(key) ?? "";
return SkillFactory.Create(..., content, ...);
```
- Pros: Graceful degradation, skill still works
- Cons: Silent failure, agent has no guidance

**Strategy C: Fall Back to Inline Default**
```csharp
var content = await store.ResolveDocumentAsync(key)
    ?? "Default instructions: Use functions as needed";
```
- Pros: Best of both worlds, always has instructions
- Cons: Might be misleading, masks configuration issues

**Strategy D: Log Warning and Continue**
```csharp
if (!await store.DocumentExistsAsync(key)) {
    logger.LogWarning("Document '{Key}' not found, using empty instructions", key);
}
```
- Pros: Visible in logs, doesn't break workflow
- Cons: Easy to miss, accumulates tech debt

**Strategy E: Retry with Variants**
```csharp
// Try: "doc://file-debugging-v2"
// Then: "doc://file-debugging-v1"
// Then: "doc://file-debugging"
// Then: inline default
```
- Pros: Resilient, handles version mismatches
- Cons: Complex logic, multiple roundtrips

---

## The Dependencies

**The Dependency Chain:**

```
Skill Code
    ↓ (references)
Document Reference
    ↓ (resolved by)
Document Resolver
    ↓ (queries)
Document Store
    ↓ (contains)
Actual Document Content
```

**Each arrow introduces:**
- **Indirection:** Another layer to debug
- **Configuration point:** Another thing to set up
- **Failure mode:** Another place it can break
- **Performance consideration:** Another network call or I/O operation

**Example Failure Modes:**

```
Skill Code: ✅ Compiles
  ↓
Document Reference: ❌ Typo in key ("doc://file-debuggin" instead of "file-debugging")
  ↓
Document Resolver: ❌ Not configured (null reference exception)
  ↓
Document Store: ❌ Database connection failed
  ↓
Actual Content: ❌ Document was deleted

Result: Skill fails at runtime, far from where code was written
```

---

## The Design Space

### Axis 1: Reference Approach

**Spectrum:**
```
Direct (inline) ←→ Abstract (key-based)

Inline String:
  "Use ReadFile to examine logs"

File Path:
  "skills/documents/file-debugging.md"

URI:
  "file://skills/documents/file-debugging.md"

Key:
  "doc://file-debugging-workflow"
```

**Trade-off:** Simplicity vs. Flexibility

---

### Axis 2: Storage Strategy

**Spectrum:**
```
Static (embedded) ←→ Dynamic (external)

Embedded Resource:
  - In assembly
  - Always available
  - No updates

Filesystem:
  - Next to DLL
  - Easy to edit
  - Not cloud-native

External Store:
  - Database/S3
  - Centralized
  - Requires infrastructure
```

**Trade-off:** Self-Contained vs. Manageable

---

### Axis 3: Resolution Time

**Spectrum:**
```
Compile-time ←→ Runtime

Compile-time:
  - Source generator embeds content
  - Zero runtime overhead
  - No flexibility

Startup-time:
  - Loaded once at app start
  - Cached in memory
  - Restart required for updates

Runtime:
  - Loaded on-demand
  - Always fresh
  - Network latency
```

**Trade-off:** Performance vs. Freshness

---

### Axis 4: Responsibility Model

**Spectrum:**
```
Plugin-owned ←→ Centrally-managed

Plugin-owned:
  - Ships complete
  - Self-sufficient
  - Org has no control

Hybrid:
  - Plugin ships defaults
  - Org can override
  - Coordination needed

Centrally-managed:
  - Plugin references by key
  - Org uploads content
  - Plugins not autonomous
```

**Trade-off:** Autonomy vs. Control

---

## The Scale Considerations

### Small Scale (1-5 plugins)

**Characteristics:**
- Single developer or small team
- Simple deployment (filesystem)
- Infrequent updates
- No multi-tenancy

**What Works:**
- ✅ Filesystem storage
- ✅ Documents next to DLL
- ✅ Simple file paths
- ✅ Manual document management
- ✅ No versioning needed

**What Doesn't Matter:**
- ⚪ Hot-reload (just restart)
- ⚪ Centralized management (few docs)
- ⚪ Multi-environment (dev = prod)
- ⚪ Audit trails (trust team)

---

### Medium Scale (10-50 plugins)

**Characteristics:**
- Multiple teams
- Multiple environments (dev/staging/prod)
- Frequent updates
- Some customization needs

**What Works:**
- ✅ Organized directory structure
- ✅ Namespaced keys (plugin/doc)
- ✅ Simple database or filesystem
- ✅ Environment-specific configs

**What Becomes Important:**
- ⚠️ Collision avoidance (namespacing)
- ⚠️ Document organization (folders)
- ⚠️ Sync across environments
- ⚠️ Basic versioning

**What Still Doesn't Matter:**
- ⚪ Per-tenant customization
- ⚪ Advanced versioning
- ⚪ Compliance audit trails

---

### Large Scale (100+ plugins, multi-tenant)

**Characteristics:**
- Many teams, many plugins
- Complex infrastructure
- Multi-tenant SaaS
- Compliance requirements
- Frequent updates, must be fast

**What's Required:**
- ✅ Centralized management (database or S3)
- ✅ Hot-reload capability
- ✅ Versioning and rollback
- ✅ Per-tenant customization
- ✅ Audit trails
- ✅ CDN for performance
- ✅ Access control
- ✅ Automated sync

**Challenges:**
- 🔥 Key namespace management
- 🔥 Version coordination
- 🔥 Performance (1000s of documents)
- 🔥 Consistency across instances
- 🔥 Compliance and security

---

## The Paradoxes

### Paradox 1: Simplicity for Common vs Flexibility for Edge Cases

**The Paradox:**
- 90% of users want zero configuration (just works)
- 10% of users need full control (complex requirements)
- Same API must serve both
- Every flexibility feature adds complexity
- Complexity hurts the 90%

**Example:**
```csharp
// What 90% want:
.WithPlugin<MyPlugin>()  // ← Documents just work

// What 10% need:
.WithPlugin<MyPlugin>()
.WithInstructionDocumentStore(customStore)
.WithDocumentResolver(customResolver)
.WithDocumentCache(customCache)
.WithDocumentVersioning(versionStrategy)
// ← Every option makes API harder to understand
```

**The Tension:** Can't add power without adding surface area.

---

### Paradox 2: Type Safety vs Storage Abstraction

**The Paradox:**
- Type safety requires compile-time knowledge
- Storage abstraction requires runtime flexibility
- Document storage location unknown at compile-time
- But reference must be written at compile-time

**Example:**
```csharp
// Compile-time (plugin developer writes):
[Skill]
public Skill MySkill(...) {
    return SkillFactory.Create(...,
        "???",  // ← What to put here?
        ...);
}

// Runtime (app developer chooses):
.WithInstructionDocumentStore(new S3InstructionDocumentStore(...))
// ← Storage backend decided here

// How can plugin dev write type-safe reference
// when storage is chosen later?
```

**The Impossibility:** Can't validate at compile-time what doesn't exist until runtime.

---

### Paradox 3: Plugin Autonomy vs Centralized Management

**The Paradox:**
- Plugin should "just work" (autonomy)
- Organization needs control over content (centralized)
- Can't satisfy both fully
- Either plugins are self-sufficient OR org has control

**Scenario:**
```
Plugin ships with instruction document:
  "Always check production database before migrations"

Organization wants different instruction:
  "Always check staging database first, then production"

Who wins?
```

**Options:**
- Plugin instructions override org? (Plugin autonomy)
- Org instructions override plugin? (Centralized control)
- Merge both? (Complex, who's responsible?)
- Org can customize per-plugin? (Management overhead)

**The Tension:** Authority conflict between plugin author and organization.

---

### Paradox 4: Version Coordination

**The Paradox:**
- Plugin code and documents should be versioned together
- But documents can be updated independently
- Document updates should work without code changes
- But code might expect specific document structure

**Scenario:**
```
Plugin v1.0:
  - Code expects document with "## Prerequisites" section
  - Ships document with that section

Document updated:
  - Ops renames section to "## Requirements"
  - Document now missing "## Prerequisites"

Plugin code breaks:
  - Looks for "## Prerequisites"
  - Can't find it
  - Error or unexpected behavior
```

**The Dilemma:**
- Tight coupling: Code and docs must be updated together (defeats hot-reload)
- Loose coupling: Code and docs can drift (defeats reliability)

---

## The Real Problem

### The Fundamental Issue

**Two Worlds That Must Communicate:**

```
┌─────────────────────────────────────────┐
│        COMPILE-TIME WORLD               │
│                                         │
│  - Type-safe                            │
│  - Immutable                            │
│  - Distributed as DLLs                  │
│  - Known at build time                  │
│  - Plugin developer's domain            │
│                                         │
│  Skill Code:                            │
│  [Skill]                                │
│  public Skill MySkill(...) {            │
│      return SkillFactory.Create(...,    │
│          "????",  ← The Bridge          │
│          ...);                          │
│  }                                      │
└─────────────────────────────────────────┘
                    │
                    │ What goes here?
                    │ How to reference?
                    │
┌─────────────────────────────────────────┐
│         RUNTIME WORLD                   │
│                                         │
│  - Flexible                             │
│  - Mutable                              │
│  - Stored in various backends           │
│  - Determined at runtime                │
│  - App developer's / ops team's domain  │
│                                         │
│  Document Storage:                      │
│  - Filesystem                           │
│  - Database                             │
│  - S3 / Blob Storage                    │
│  - GitHub                               │
│  - User-created                         │
└─────────────────────────────────────────┘
```

**The Bridge Problem:**
- Need a reference format that works at compile-time
- But resolves to flexible storage at runtime
- Must validate what can be validated at compile-time
- Must defer what can only be known at runtime
- Must fail gracefully when expectations don't match reality

---

### What We Need

**Requirements:**
1. ✅ **Zero config for simple cases** - Plugin developers can ship complete packages
2. ✅ **Full flexibility for complex cases** - Enterprise users can customize everything
3. ✅ **Native AOT compatibility** - No runtime reflection
4. ✅ **Type safety where possible** - Catch errors at compile-time when we can
5. ✅ **Clear responsibility boundaries** - Everyone knows their role
6. ✅ **Graceful failure modes** - Degrade gracefully when things go wrong
7. ✅ **Performance** - Don't load everything upfront, cache intelligently
8. ✅ **Consistency** - Same patterns as memory system, plugin system

**The Challenge:**
- Every bridge mechanism introduces complexity
- Every abstraction layer is a failure point
- Every configuration option is cognitive overhead
- Every flexibility feature hurts simplicity

**The Balancing Act:**
```
Simplicity ←────────────────────→ Power
     ↑                                ↑
     │                                │
 90% of users                    10% of users
  need this                       need this
     │                                │
     └────────── Same API ───────────┘
```

---

## The Missing Dimension: Shared Store Architecture

**CRITICAL INSIGHT:** The problems described above are compounded by a fundamental architectural difference between instruction documents and other systems (memory, plugins) that was not initially apparent.

### The 1:N Problem (One Store, Many Skills)

Unlike other framework components, instruction documents have a **shared store architecture**:

| System | Architecture | Relationship |
|--------|-------------|--------------|
| **Memory** | 1 Agent → 1 Memory Store | 1:1 (isolated) |
| **Plugins** | 1 Agent → N Plugins | 1:N (but plugins are independent) |
| **Instructions** | **1 Store → N Skills with different references** | **1:N (shared, interdependent)** |

```csharp
// Memory: Each agent has isolated store
builder
    .WithMemory(new MemoryStore(...))  // ← Agent-specific

// Instructions: ALL skills share ONE store
builder
    .WithInstructionDocumentStore(new FileSystemDocumentStore("./docs"))  // ← SHARED
    .WithPlugin<PluginA>()  // Has Skill1 referencing ["doc1.md", "doc2.md"]
    .WithPlugin<PluginB>()  // Has Skill2 referencing ["doc2.md", "doc3.md"]  ← Overlap!
    .WithPlugin<PluginC>()  // Has Skill3 referencing ["doc3.md", "doc4.md"]
```

**The Problem:** Multiple skills from different plugins, different authors, all referencing documents in the same shared store.

---

### The Reference Fragmentation Problem

Each skill has its own document references, but all resolve through the same store:

```csharp
// Skill A (from FileSystemPlugin)
[Skill]
public Skill FileDebugging(...) {
    return SkillFactory.Create(...,
        options: new SkillOptions {
            InstructionDocumentReferences = new[] {
                "file-debugging-workflow.md",
                "troubleshooting.md"  // ← Shared with Skill B!
            }
        }
    );
}

// Skill B (from ApiPlugin)
[Skill]
public Skill ApiDebugging(...) {
    return SkillFactory.Create(...,
        options: new SkillOptions {
            InstructionDocumentReferences = new[] {
                "api-debugging-workflow.md",
                "troubleshooting.md"  // ← Same reference, different context!
            }
        }
    );
}

// Global store must resolve both:
store.ResolveAsync("file-debugging-workflow.md")  // ← Skill A only
store.ResolveAsync("api-debugging-workflow.md")   // ← Skill B only
store.ResolveAsync("troubleshooting.md")          // ← Both skills! Same doc or different?
```

**Questions:**
- Is `"troubleshooting.md"` the same document for both skills?
- If different plugins ship different versions, which one goes in the store?
- How to prevent key collisions? Namespace by plugin? By skill?
- Who decides the canonical version when conflicts occur?

---

### The Serialization Disconnection Problem

Skills can be serialized (for config files, storage, transmission), but the store configuration is separate:

```csharp
// Skill definition serializes to JSON:
{
  "name": "FileDebugging",
  "description": "Debug files",
  "instructionDocumentReferences": ["file-debugging-workflow.md", "troubleshooting.md"]
  // ⬆️ These are just strings - no link to the store!
}

// Store is configured separately:
builder.WithInstructionDocumentStore(new S3DocumentStore("prod-bucket"));
//      ^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
//      This configuration is NOT serialized with the skill

// When deserializing the skill in a different environment:
// - How do we know which store to use?
// - How do we reconnect the references to the store?
// - What if the store doesn't have those documents?
```

**The Disconnect:**
```
Skill Definition (serializable)
    ↓
    InstructionDocumentReferences: ["doc.md"]
    ↓
    ??? NO CONNECTION ???
    ↓
IInstructionDocumentStore (configured separately, not serializable)
```

**Unlike other systems:**
- Plugins: Serializable, self-contained (functions are in the assembly)
- Memory: Not serialized with agent config (retrieval is query-based, not reference-based)
- Instructions: **Serialized references pointing to non-serialized store** ← The problem!

---

### The Multi-Skill Coordination Problem

Because all skills share one store, coordination problems emerge:

#### Problem 1: Duplicate References
```csharp
Skill A: ["getting-started.md", "advanced.md"]
Skill B: ["getting-started.md", "api-reference.md"]

// Both reference "getting-started.md"
// Questions:
// - Is this the same document or different?
// - If different plugins ship different content, which wins?
// - Does the store deduplicate or keep both?
```

#### Problem 2: Discovery Conflicts
```csharp
// Plugin A discovers and uploads:
await store.UploadAsync("getting-started.md", contentFromPluginA);

// Plugin B also discovers and uploads:
await store.UploadAsync("getting-started.md", contentFromPluginB);  // ← Overwrites? Fails? Merges?

// Who wins? What's the strategy?
```

#### Problem 3: Namespace Management
```csharp
// Without namespacing:
PluginA.Skill1: ["guide.md"]  // ← Collision!
PluginB.Skill1: ["guide.md"]  // ← Collision!

// With namespacing:
PluginA.Skill1: ["PluginA/guide.md"]  // ← Verbose, plugin name in every reference
PluginB.Skill1: ["PluginB/guide.md"]

// Or:
PluginA.Skill1: ["guide.md"]  // ← Store automatically namespaces to "PluginA/guide.md"?
PluginB.Skill1: ["guide.md"]  // ← Store automatically namespaces to "PluginB/guide.md"?

// But how does store know which plugin a reference belongs to?
```

---

### Why This Is Different From Memory

**Memory System (Simple, Works):**
```csharp
Agent → Memory Store → Semantic retrieval ("find relevant context for X")
        (isolated)     (query-based, no explicit references)

// No fragmentation because:
✅ Each agent has its own store (no sharing)
✅ Retrieval is query-based (no hard references)
✅ No serialization of memory references (queries are dynamic)
✅ No coordination between agents needed
```

**Instruction Document System (Complex, Problematic):**
```csharp
                    ┌→ Skill A ["doc1.md", "doc2.md"]  ← Hard references
                    │
Global Store ──────┼→ Skill B ["doc2.md", "doc3.md"]  ← Shared reference "doc2.md"!
(SHARED!)           │
                    └→ Skill C ["doc3.md", "doc4.md"]

Skills are serializable: { "skill": "A", "refs": ["doc1.md"] }
                         ^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
Store is NOT serializable (configured at runtime)

// Fragmentation because:
❌ All skills share one store (coordination required)
❌ References are hard-coded strings (not queries)
❌ Skills serialize with references, but store is separate
❌ Multiple skills can reference the same document key
❌ Namespace collisions possible across plugins
```

---

### The Store as Shared Infrastructure

Unlike memory (which is per-agent and isolated), the instruction store is **shared infrastructure** that all skills depend on:

```
┌─────────────────────────────────────────────────────────┐
│               Shared Instruction Store                  │
│                                                         │
│  "file-debugging-workflow.md"    → Content A           │
│  "api-debugging-workflow.md"     → Content B           │
│  "troubleshooting.md"            → Content C (shared!) │
│  "getting-started.md"            → Content D (conflict?)│
│                                                         │
└─────────────────────────────────────────────────────────┘
          ↑              ↑              ↑
          │              │              │
    ┌─────┴──┐     ┌─────┴──┐     ┌─────┴──┐
    │ Skill A│     │ Skill B│     │ Skill C│
    │ (Plugin│     │ (Plugin│     │ (Plugin│
    │   A)   │     │   B)   │     │   C)   │
    └────────┘     └────────┘     └────────┘

    Different authors, different plugins, same store!
```

**Implications:**
1. **No isolation** - Plugin A's documents visible to Plugin B
2. **No ownership** - Who "owns" `"troubleshooting.md"`?
3. **Coordination required** - Namespace conflicts, versioning, updates
4. **Discovery complexity** - How do documents from different plugins get into the store?
5. **Testing challenges** - Mock store must contain documents from all plugins

---

### The Reference-Store Disconnection

The critical insight: **References are written at compile-time, but the store they point to is configured at runtime, and they're serialized separately.**

```
COMPILE TIME (Plugin Developer):
┌──────────────────────────────────┐
│  [Skill]                         │
│  public Skill MySkill(...) {     │
│    return SkillFactory.Create(   │
│      ...,                        │
│      options: new SkillOptions { │
│        InstructionDocument       │
│        References = [            │
│          "my-doc.md"  ←──────────┼─┐ Reference written here
│        ]                         │ │
│      }                           │ │
│    );                            │ │
│  }                               │ │
└──────────────────────────────────┘ │
                                     │
SERIALIZATION TIME:                  │
┌──────────────────────────────────┐ │
│ {                                │ │
│   "name": "MySkill",             │ │
│   "refs": ["my-doc.md"]  ←───────┼─┤ Reference serialized here
│ }                                │ │ (as plain string)
└──────────────────────────────────┘ │
                                     │
RUNTIME (Application Developer):     │
┌──────────────────────────────────┐ │
│ builder                          │ │
│   .WithInstructionDocumentStore( │ │
│     new S3DocumentStore(...)     │ │ ← Store configured here
│   )                              │ │   (SEPARATE from reference)
│   .WithPlugin<MyPlugin>()        │ │
└──────────────────────────────────┘ │
                                     │
RESOLUTION TIME:                     │
┌──────────────────────────────────┐ │
│ store.ResolveAsync("my-doc.md")  │←┘ How does this connect?
│                                  │   String reference → Store instance?
└──────────────────────────────────┘   No explicit link!
```

**The Problem:**
- Reference (`"my-doc.md"`) is a compile-time string
- Store (S3, Database, Filesystem) is a runtime instance
- Skill serialization captures the reference but NOT the store
- When deserializing, how does `"my-doc.md"` know which store to query?

**Unlike Functions:**
- Function references are type-safe: `FileSystemPlugin.ReadFile`
- Compiler ensures function exists
- Runtime resolves from registered plugins
- No serialization disconnect (function is in the assembly)

**Unlike Memory:**
- Memory doesn't serialize references
- Retrieval is query-based at runtime
- No hard-coded "keys" in skill code
- Store is always local to the agent

---

### Summary: The Unique Challenge

Instruction documents face a **unique combination of constraints** not present in other systems:

| Constraint | Memory | Plugins | Instructions |
|-----------|--------|---------|--------------|
| Shared store across multiple consumers | ❌ No (per-agent) | ❌ No (self-contained) | ✅ **Yes (shared!)** |
| Hard-coded references in code | ❌ No (query-based) | ✅ Yes (type-safe) | ✅ **Yes (strings)** |
| Serialization of references | ❌ No | ✅ Yes (in assembly) | ✅ **Yes (disconnected from store)** |
| Runtime store configuration | ✅ Yes | ❌ No | ✅ **Yes** |
| Namespace collision risk | ❌ No | ❌ No | ✅ **Yes** |
| Multi-author coordination | ❌ No | ⚠️ Minimal | ✅ **Yes (high)** |

**This is why instruction documents are fundamentally harder** than memory or plugins. They combine:
1. Shared infrastructure (like a database)
2. With compile-time references (like type-safe function calls)
3. That serialize separately from their resolution mechanism (like config files)
4. Across multiple plugins from different authors (like a package ecosystem)

No other system in the framework has this combination of challenges.

---

## Conclusion

This problem space is **inherently complex** because it bridges two fundamentally different worlds (compile-time and runtime) while trying to satisfy multiple actors with conflicting needs (plugin developers want autonomy, organizations want control, users want simplicity, enterprises want power).

**No perfect solution exists.** Any approach will make trade-offs. The goal is to find the trade-offs that:
1. Make the common case simple (90% of users happy)
2. Make the complex case possible (10% of users not blocked)
3. Maintain consistency with existing patterns (memory system, plugin system)
4. Stay true to framework principles (Native AOT, Configuration-First, Batteries Included)

The next step is to evaluate potential solutions against this problem space to find the best set of trade-offs.
