# HPD-Agent Plugin & Skills Documentation Index

## 📚 Documentation Files

### [1. Scoping System Documentation](./SCOPING_SYSTEM.md)
**For understanding how visibility works**

Topics covered:
- How plugins and skills are hidden/shown
- The `[Scope]` attribute
- Visibility rules and priority order
- Explicit vs implicit registration
- Test scenarios (6 comprehensive examples)
- Expansion behavior
- Function metadata
- Debugging tips

**Read this if you want to:**
- Understand why functions are hidden/shown
- Learn about plugin containers
- Learn about skill containers
- Create scoped plugins or skills
- Debug visibility issues

---

### [2. Skills Architecture Documentation](./SKILLS_ARCHITECTURE.md)
**For understanding how skills work**

Topics covered:
- What skills are and how they differ from functions
- Skill class definition and structure
- The `Skill` object class
- Skill visibility rules
- Skill registration and discovery
- Multi-step workflow patterns
- Metadata structure
- Source generator integration
- Execution flow
- Best practices
- Testing skills
- Troubleshooting

**Read this if you want to:**
- Create new skills
- Understand skill containers
- Learn how skills encapsulate workflows
- Reference plugin functions from skills
- Debug skill-related issues

---

### [3. Plugin Clarification](./PLUGIN_CLARIFICATION.md)
**IMPORTANT: Skill classes are plugins!**

Topics covered:
- Skill classes are technically plugins
- Both contain the same scoping mechanism
- Both registered identically
- Terminology is semantic, not architectural
- Unified plugin model
- Updated architecture diagram
- Impact on documentation

**Read this if you:**
- Are confused about "Plugin" vs "Skill Class"
- Wonder why they work the same
- Want to understand the unified architecture

---

### [4. Plugin & Skills Integration Guide](./PLUGIN_SKILLS_INTEGRATION.md)
**For understanding how everything works together**

Topics covered:
- High-level architecture overview
- How plugins relate to functions
- How skill classes relate to skills
- How skills relate to referenced functions
- Complete registration flow
- Visibility decision trees
- Common scenarios with examples
- Development workflow
- Performance tips
- Debugging tips
- Reference guide

**Read this if you want to:**
- See the "big picture" of how systems work together
- Understand common usage patterns
- Learn workflows for adding plugins and skills
- See real-world examples
- Get quick reference guides

---

## 🎯 Quick Start by Task

### "I want to create a new plugin"
1. Start: [Scoping System - The [Scope] Attribute](./SCOPING_SYSTEM.md#the-scope-attribute)
2. Then: [Plugin & Skills Integration - Adding a New Plugin](./PLUGIN_SKILLS_INTEGRATION.md#development-workflow)

### "I want to understand why my plugin functions are hidden"
1. Start: [Scoping System - Visibility Rules](./SCOPING_SYSTEM.md#visibility-rules)
2. Reference: [Scoping System - Test Scenarios](./SCOPING_SYSTEM.md#test-scenarios)

### "I want to create skills for my plugin"
1. Start: [Skills Architecture - Skill Class Definition](./SKILLS_ARCHITECTURE.md#skill-class-definition)
2. Reference: [Skills Architecture - Patterns & Examples](./SKILLS_ARCHITECTURE.md#patterns--examples)

### "I want to organize my skills with [Scope]"
1. Start: [Skills Architecture - With Scope (Grouped Skills)](./SKILLS_ARCHITECTURE.md#with-scope-grouped-skills)
2. Then: [Scoping System - Skill Visibility Rules](./SCOPING_SYSTEM.md#skill-visibility-rules)

### "My skills/functions aren't visible - debug!"
1. Start: [Scoping System - Debugging](./SCOPING_SYSTEM.md#debugging)
2. Reference: [Scoping System - Visibility Rules](./SCOPING_SYSTEM.md#visibility-rules)
3. Also see: [Skills Architecture - Troubleshooting](./SKILLS_ARCHITECTURE.md#troubleshooting)

### "I want to see example code"
1. See: [Plugin & Skills Integration - Common Scenarios](./PLUGIN_SKILLS_INTEGRATION.md#common-scenarios)
2. Reference: [Skills Architecture - Patterns & Examples](./SKILLS_ARCHITECTURE.md#patterns--examples)

---

## 🔑 Key Concepts Quick Reference

### CRITICAL CLARIFICATION: Unified Plugin Model

**Skill classes ARE plugins.** There is no architectural distinction.

| Aspect | "Plugin" | "Skill Class" |
|--------|----------|---------------|
| **What It Is** | A plugin containing functions | A plugin containing skills |
| **Registration** | `builder.WithPlugin<T>()` | `builder.WithPlugin<T>()` |
| **Scoping** | `[Scope]` attribute | `[Scope]` attribute |
| **Container Type** | `[AIFunction]` methods | `[Skill]` methods |
| **Architecturally** | Same as skill class | Same as plugin |

**The difference is semantic, not structural.**

See: [Plugin Clarification](./PLUGIN_CLARIFICATION.md)

---

### Scoping (Display Organization)

| Term | Definition | File |
|------|-----------|------|
| **Plugin Container** | Function that groups plugin's functions under `[Scope]` | [Scoping System](./SCOPING_SYSTEM.md#the-scope-attribute) |
| **Skill Container** | Function that represents a single skill | [Skills Architecture](./SKILLS_ARCHITECTURE.md#skill-object-definition) |
| **Scope Container** | Function that groups skills when class has `[Scope]` | [Scoping System](./SCOPING_SYSTEM.md#the-scope-attribute) |
| **[Scope] Attribute** | Marks plugins or skill classes for hierarchical display | [Scoping System](./SCOPING_SYSTEM.md#the-scope-attribute) |
| **Explicit Registration** | Plugin registered via `.WithPlugin<T>()` | [Scoping System](./SCOPING_SYSTEM.md#explicit-vs-implicit-plugin-registration) |
| **Implicit Registration** | Plugin auto-registered when skills reference it | [Scoping System](./SCOPING_SYSTEM.md#explicit-vs-implicit-plugin-registration) |

### Skills (Workflow Organization)

| Term | Definition | File |
|------|-----------|------|
| **Skill** | Multi-step workflow that references plugin functions | [Skills Architecture](./SKILLS_ARCHITECTURE.md#what-is-a-skill) |
| **Skill Class** | Class that contains multiple `[Skill]` methods | [Skills Architecture](./SKILLS_ARCHITECTURE.md#skill-class-definition) |
| **[Skill] Attribute** | Marks a method as a skill | [Skills Architecture](./SKILLS_ARCHITECTURE.md#skill-class-definition) |
| **ReferencedFunctions** | List of plugin functions a skill uses | [Skills Architecture](./SKILLS_ARCHITECTURE.md#skill-object-definition) |
| **Instructions** | Step-by-step guidance for executing a skill | [Skills Architecture](./SKILLS_ARCHITECTURE.md#skill-object-definition) |
| **UsageContext** | When to use a skill | [Skills Architecture](./SKILLS_ARCHITECTURE.md#skill-object-definition) |

---

## 📋 Reference Tables

### Visibility Decision Matrix

| Scenario | Plugin [Scope]? | Explicit? | Result | Documentation |
|----------|-----------------|-----------|--------|-----------------|
| Simple plugin | ❌ | ✅ | All functions visible | [Scoping System Scenario 2](./SCOPING_SYSTEM.md#scenario-2-plugin-not-scoped-skills-scoped) |
| Organized plugin | ✅ | ✅ | Container + functions hidden until expanded | [Scoping System Scenario 6](./SCOPING_SYSTEM.md#scenario-6-scoped-plugin-explicit-no-skills) |
| Auto-registered plugin | ❌ | ❌ | Orphan functions hidden | [Scoping System Scenario 4](./SCOPING_SYSTEM.md#scenario-4-only-skills-registered-no-explicit-plugin-skills-without-scope) |
| Simple skills | ❌ | N/A | Skills visible | [Skills Architecture](./SKILLS_ARCHITECTURE.md#skill-class-without-scope) |
| Organized skills | ✅ | N/A | Skills hidden until scope expanded | [Skills Architecture](./SKILLS_ARCHITECTURE.md#skill-class-with-scope) |

### [Scope] Attribute Usage

| Use Case | Attribute Location | Effect | Documentation |
|----------|-------------------|--------|-----------------|
| Organize plugin functions | On plugin class | Creates container, hides functions | [Scoping System](./SCOPING_SYSTEM.md#plugin-scoping) |
| Organize skills | On skill class | Creates scope, hides individual skills | [Scoping System](./SCOPING_SYSTEM.md#skill-visibility-rules) |
| No organization | Omit attribute | All items immediately visible | [Skills Architecture](./SKILLS_ARCHITECTURE.md#skill-class-without-scope) |

---

## 🧪 Test Coverage

All scenarios documented in this guide are covered by tests:

**Location:** `test/HPD-Agent.Tests/Scoping/ToolVisibilityManagerTests.cs`

**Test Count:** 8 comprehensive scenarios
- ✅ Both scoped & both explicit
- ✅ Plugin not scoped, skills scoped
- ✅ Plugin scoped, skills not scoped
- ✅ Only skills explicit (orphan hiding)
- ✅ Only skills explicit (with scope)
- ✅ Plugin scoped only
- ✅ Expansion of skill scope containers
- ✅ Expansion of individual skills

**Running Tests:**
```bash
dotnet test --filter "FullyQualifiedName~ToolVisibilityManagerTests"
```

---

## 🛠️ Development References

### Source Files

| File | Purpose |
|------|---------|
| `HPD-Agent/Scoping/ToolVisibilityManager.cs` | Main visibility orchestrator |
| `HPD-Agent/Agent/AgentBuilder.cs` | Plugin registration (WithPlugin) |
| `HPD-Agent/Plugins/Attributes/ScopeAttribute.cs` | [Scope] attribute definition |
| `HPD-Agent.SourceGenerator/` | Code generation for containers |

### Test Files

| File | Tests |
|------|-------|
| `test/HPD-Agent.Tests/Scoping/ToolVisibilityManagerTests.cs` | All visibility scenarios |
| `test/HPD-Agent.Tests/Scoping/SkillScopingTests.cs` | Skill-specific tests |
| `test/HPD-Agent.Tests/Infrastructure/ScopedPluginTestHelper.cs` | Test utilities |

---

## 📝 Most Common Questions

**Q: Why are my plugin functions hidden?**  
A: See [Scoping System - Visibility Rules](./SCOPING_SYSTEM.md#visibility-rules). Usually because plugin has `[Scope]` and you haven't expanded it.

**Q: How do I make my functions always visible?**  
A: Don't add `[Scope]` attribute to your plugin. See [Plugin & Skills Integration Scenario A](./PLUGIN_SKILLS_INTEGRATION.md#scenario-a-simple-plugin-no-scoping).

**Q: How do I organize functions under a container?**  
A: Add `[Scope]` attribute to your plugin class. See [Plugin & Skills Integration Scenario B](./PLUGIN_SKILLS_INTEGRATION.md#scenario-b-organized-plugin-with-scoping).

**Q: How do I create skills?**  
A: Create a class with `[Skill]` methods returning `Skill` objects. See [Skills Architecture - Skill Class Definition](./SKILLS_ARCHITECTURE.md#skill-class-definition).

**Q: How do I organize skills?**  
A: Add `[Scope]` attribute to your skill class. See [Skills Architecture - With Scope](./SKILLS_ARCHITECTURE.md#with-scope-grouped-skills).

**Q: What functions should a skill reference?**  
A: Only functions the skill actually uses in its workflow. See [Skills Architecture - ReferencedFunctions](./SKILLS_ARCHITECTURE.md#property-details).

---

## 📚 Additional Resources

- **HPD-Agent Main README:** [README.md](../README.md)
- **Architecture Overview:** [HPD_AGENT_CONTEXT_FLOW_MAP.md](../HPD_AGENT_CONTEXT_FLOW_MAP.md)
- **System Instructions:** [SYSTEM_INSTRUCTIONS_ARCHITECTURE.md](../SYSTEM_INSTRUCTIONS_ARCHITECTURE.md)

---

## 🎓 Learning Path

### For New Contributors
1. Read: [Plugin & Skills Integration - The Complete Picture](./PLUGIN_SKILLS_INTEGRATION.md#the-complete-picture)
2. Skim: [Scoping System - Visibility Rules](./SCOPING_SYSTEM.md#visibility-rules)
3. Study: [Skills Architecture - Skill Class Definition](./SKILLS_ARCHITECTURE.md#skill-class-definition)
4. Reference: Use quick start guides as needed

### For Implementing New Features
1. Start: [Development Workflow](./PLUGIN_SKILLS_INTEGRATION.md#development-workflow)
2. Reference: [Common Scenarios](./PLUGIN_SKILLS_INTEGRATION.md#common-scenarios)
3. Test: [Scoping System - Test Scenarios](./SCOPING_SYSTEM.md#test-scenarios)
4. Debug: [Debugging Strategies](./SCOPING_SYSTEM.md#debugging)

### For Troubleshooting
1. Symptoms: Check [Scoping System - Debugging](./SCOPING_SYSTEM.md#debugging) or [Skills Architecture - Troubleshooting](./SKILLS_ARCHITECTURE.md#troubleshooting)
2. Root Cause: Review [Visibility Rules](./SCOPING_SYSTEM.md#visibility-rules)
3. Fix: Apply solution from documentation
4. Verify: Run tests to confirm

---

Last Updated: November 12, 2025  
Version: v0  
Status: Complete & Tested ✅
