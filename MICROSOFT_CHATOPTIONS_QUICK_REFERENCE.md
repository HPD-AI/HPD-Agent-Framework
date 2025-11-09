# Microsoft.Extensions.AI ChatOptions Modernization - Quick Reference

## At a Glance

HPD-Agent predates the modern ChatOptions properties (Instructions, ConversationId, ResponseFormat) and works around them using AdditionalProperties. The architecture is sound - it just needs strategic modernization to reduce friction and enable provider optimization.

## What to Change

### Priority 1: ConversationId (CRITICAL)
**Current:**
```csharp
options.AdditionalProperties["ConversationId"] = id;
```

**Target:**
```csharp
options.ConversationId = id;
```

**Why:** Unblocks provider-native thread tracking, type-safe, cleaner code

**File:** `/HPD-Agent/Agent/Agent.cs` lines 394-404

---

### Priority 2: Instructions Property (HIGH)
**Current:**
```csharp
private IEnumerable<ChatMessage> PrependSystemInstructions(messages)
{
    var systemMessage = new ChatMessage(ChatRole.System, _systemInstructions);
    return new[] { systemMessage }.Concat(messages);
}
```

**Target:**
```csharp
options.Instructions = _systemInstructions;
// Keep system message fallback for compatibility
```

**Why:** Enables provider optimization (Anthropic prompt caching = 90% savings), clearer intent

**File:** `/HPD-Agent/Agent/Agent.cs` lines 3594-3612

---

### Priority 3: ResponseFormat Support (MEDIUM)
**Current:** Not implemented (property exists but unused)

**Target:**
```csharp
// In AgentConfig:
public ChatResponseFormat? ResponseFormat { get; set; }

// In Agent:
options.ResponseFormat = Config?.ResponseFormat;
```

**Why:** Enable structured outputs, JSON schema validation

**File:** `/HPD-Agent/Agent/AgentConfig.cs`, `/HPD-Agent/Agent/Agent.cs`

---

### Priority 4: Project/Thread Context (KEEP IN ADDITIONAL)
**Keep As-Is:**
```csharp
options.AdditionalProperties["Project"] = project;
options.AdditionalProperties["Thread"] = thread;
```

**Better:** Add extension methods for clarity
```csharp
chatOptions.WithProject(project).WithThread(thread).WithConversationId(id);
```

**Why:** Domain-specific, no ChatOptions property exists

---

## Migration Phases

```
Phase 1 (Week 1-2):   ConversationId migration
  ├─ Read from ChatOptions.ConversationId first
  ├─ Fallback to AdditionalProperties
  └─ All existing tests pass

Phase 2 (Week 3-4):   Instructions property
  ├─ Populate ChatOptions.Instructions
  ├─ Keep system message fallback
  └─ Provider tests pass

Phase 3 (Week 5-6):   ResponseFormat support
  ├─ Extend AgentConfig
  ├─ Apply in ChatOptions
  └─ Validation tests pass

Phase 4 (Week 7-8):   Provider optimization
  ├─ Update OpenRouter, Anthropic, OpenAI
  ├─ Performance tests
  └─ Backward compatibility confirmed

Phase 5 (Week 9-10):  Cleanup
  ├─ Remove legacy paths (when safe)
  ├─ Update documentation
  └─ All tests pass
```

---

## Code Change Checklist

### ConversationId Migration
- [ ] Update Agent constructor to initialize from ChatOptions.ConversationId
- [ ] Update Agent.RunAgenticLoopInternal to read from chatOptions.ConversationId
- [ ] Update AGUI handler to set chatOptions.ConversationId
- [ ] Add fallback to AdditionalProperties for backward compat
- [ ] Tests verify both paths work

### Instructions Property
- [ ] Create BuildOptionsWithInstructions() method
- [ ] Populate options.Instructions with system context
- [ ] Keep system message prepending as fallback
- [ ] Update plan mode to use Instructions
- [ ] Tests verify instructions flow through

### ResponseFormat
- [ ] Add ResponseFormat property to AgentConfig
- [ ] Add ApplyResponseFormat() method to Agent
- [ ] Call during message preparation
- [ ] Provider tests verify handling

### Extension Methods
- [ ] Create ChatOptionsContextExtensions.cs
- [ ] Add WithProject() method
- [ ] Add WithThread() method
- [ ] Add WithConversationId() method

---

## Key Files to Modify

| File | Change | Priority | Lines |
|------|--------|----------|-------|
| Agent.cs | ConversationId: read from ChatOptions first | CRITICAL | 394-404, 1156 |
| Agent.cs | Instructions: use Options.Instructions | HIGH | 3594-3612 |
| Agent.cs | ResponseFormat: apply during prep | MEDIUM | 418-430 |
| AgentConfig.cs | Add ResponseFormat property | MEDIUM | NEW |
| MessageProcessor.cs | Update PrependSystemInstructions | MEDIUM | 3462 |
| ChatOptionsContextExtensions.cs | Add helper methods | LOW | NEW |
| All providers | Update for new properties | MEDIUM | Various |

---

## Risk Assessment

| Risk | Severity | Mitigation |
|------|----------|-----------|
| Provider incompatibility | MEDIUM | Fallback paths, gradual rollout |
| Breaking existing code | LOW | Dual-write during transition |
| Performance regression | LOW | Profile before/after |
| Filter complexity | MEDIUM | FilterContextBuilder helper |

---

## Testing Strategy

### Unit Tests
```csharp
[Test]
public void ConversationId_ReadsFromChatOptions()
{
    var options = new ChatOptions { ConversationId = "test-id" };
    var agent = CreateAgent();
    var id = agent.ExtractConversationId(options);
    Assert.AreEqual("test-id", id);
}

[Test]
public void ConversationId_FallsBackToAdditionalProperties()
{
    var options = new ChatOptions();
    options.AdditionalProperties["ConversationId"] = "legacy-id";
    var agent = CreateAgent();
    var id = agent.ExtractConversationId(options);
    Assert.AreEqual("legacy-id", id);
}
```

### Integration Tests
```csharp
[Test]
public async Task SystemInstructions_SentViaInstructionsProperty()
{
    var agent = CreateAgent();
    var messages = await agent.GetResponseAsync(...);
    // Verify instructions property was used (provider-specific)
}

[Test]
public async Task ResponseFormat_AppliedToOptions()
{
    var config = new AgentConfig { ResponseFormat = ChatResponseFormat.Json };
    var agent = CreateAgent(config);
    // Verify ResponseFormat is set during execution
}
```

---

## Documentation Updates

### Update These Docs
- [ ] System instructions architecture (show Instructions property)
- [ ] ChatOptions usage (add ConversationId example)
- [ ] Provider implementation guide (explain new properties)
- [ ] Migration guide (Phase 1-5 instructions)
- [ ] Code examples (before/after snippets)

---

## Rollback Plan

If issues arise:
1. Keep both old and new code paths during transition
2. Add feature flag to use legacy approach if needed
3. Document rollback procedure for each phase
4. Have clean commit history for easy reversion

---

## Success Criteria

- All existing tests pass without modification
- ConversationId works with both ChatOptions and AdditionalProperties
- System instructions optimizable by providers
- ResponseFormat enables structured outputs
- No performance regression detected
- Provider tests confirm compatibility

---

## Quick Decision Tree

**Is it a Microsoft.Extensions.AI property?**
- YES → Migrate to use the property (ConversationId, ResponseFormat, Instructions)
- NO → Keep in AdditionalProperties (Project, Thread, provider-specific config)

**Is it critical for operation?**
- YES → Phase 1-2 (ConversationId, Instructions)
- NO → Phase 3-4 (ResponseFormat, provider optimizations)

**Will it break existing code?**
- YES → Dual-path during transition, then deprecate
- NO → Direct migration

**Can providers optimize it?**
- YES → Migrate to property (Instructions, ResponseFormat)
- NO → Keep current approach (Project context)

