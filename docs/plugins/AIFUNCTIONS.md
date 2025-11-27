# AI Functions Guide

**Status:** Placeholder - Full documentation coming soon

---

## Overview

AI Functions are the atomic operations that an agent can call. They are methods marked with the `[AIFunction]` attribute.

```csharp
[AIFunction]
[AIDescription("Adds two numbers together")]
public int Add(int a, int b) => a + b;
```

---

## Quick Reference

### Basic AI Function

```csharp
[AIFunction]
[AIDescription("Description of what the function does")]
public ReturnType MethodName(
    [Description("Parameter description")] ParamType param)
{
    // Implementation
}
```

### Async AI Function

```csharp
[AIFunction]
[AIDescription("Fetches data from a URL")]
public async Task<string> FetchUrl(string url)
{
    using var client = new HttpClient();
    return await client.GetStringAsync(url);
}
```

### Context-Aware AI Function

```csharp
[AIFunction<MyContext>]
[AIDescription("Conditional function based on context")]
[Conditional("context.IsEnabled")]
public string ConditionalOperation(string input)
{
    // Only available when context.IsEnabled is true
}
```

---

## Topics to Cover (Coming Soon)

- [ ] Parameter descriptions and types
- [ ] Return type handling
- [ ] Async patterns
- [ ] Error handling
- [ ] Context-aware functions with `AIFunction<TContext>`
- [ ] Conditional functions with `[Conditional]`
- [ ] Dynamic descriptions with template syntax
- [ ] Permission requirements with `[RequiresPermission]`
- [ ] Best practices for function design
- [ ] Testing AI functions

---

## See Also

- [Plugin User Guide](./USER_GUIDE.md)
- [Plugin API Reference](./API_REFERENCE.md)
- [Skills Guide](../skills/SKILLS_GUIDE.md)
- [SubAgents Guide](../SubAgents/USER_GUIDE.md)
