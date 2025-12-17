# Changelog

All notable changes to HPD-Agent will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

> **Note:** During the 0.x.y development phase, minor version bumps (0.1 → 0.2) may contain breaking changes as the API stabilizes before 1.0.

---

## [Unreleased] - 0.2.0

### ⚠️ BREAKING CHANGES

This release contains significant terminology changes to improve API clarity and consistency before the 1.0 release. **No backward compatibility aliases are provided** - you must update your code to use the new names.

See [MIGRATION.md](MIGRATION.md) for detailed upgrade instructions.

#### Summary of Breaking Changes

| Category | Old Term | New Term |
|----------|----------|----------|
| **Tool Classes** | `*Plugin` | `*Tools` |
| **Tool Metadata** | `IPluginMetadata` | `IToolMetadata` |
| **Client Tools** | `FrontendTool*` | `ClientTool*` |
| **Client Tool Groups** | `FrontendPluginDefinition` | `ClientToolGroupDefinition` |

#### C# API Changes

**Tool Class Naming Convention:**
- `WeatherPlugin` → `WeatherTools`
- `SearchPlugin` → `SearchTools`
- `FileSystemPlugin` → `FileSystemTools`

**Interfaces & Attributes:**
- `IPluginMetadata` → `IToolMetadata`
- `IPluginMetadataContext` → `IToolMetadataContext`
- `[PluginMetadata]` → `[ToolMetadata]`

**AgentBuilder Methods:**
- ` .WithTools<T>()` → `.WithTools<T>()`
- `.WithFrontendTools()` → `.WithClientTools()`

**Configuration:**
- `CollapseFrontendTools` → `CollapseClientTools`
- `FrontendToolsInstructions` → `ClientToolsInstructions`

**Source Generator:**
- `HPDPluginSourceGenerator` → `HPDToolSourceGenerator`
- `PluginInfo` → `ToolInfo`

#### TypeScript Client API Changes

**Types:**
- `FrontendToolDefinition` → `ClientToolDefinition`
- `FrontendPluginDefinition` → `ClientToolGroupDefinition`
- `FrontendSkillDefinition` → `ClientSkillDefinition`
- `FrontendSkillReference` → `ClientSkillReference`
- `FrontendSkillDocument` → `ClientSkillDocument`
- `FrontendToolAugmentation` → `ClientToolAugmentation`
- `FrontendToolInvokeRequest` → `ClientToolInvokeRequest`
- `FrontendToolInvokeResponse` → `ClientToolInvokeResponse`
- `FrontendStreamOptions` → `ClientStreamOptions`

**Events:**
- `FrontendToolInvokeRequestEvent` → `ClientToolInvokeRequestEvent`
- `FrontendPluginsRegisteredEvent` → `ClientToolGroupsRegisteredEvent`

**Event Type Constants:**
- `FRONTEND_TOOL_INVOKE_REQUEST` → `CLIENT_TOOL_INVOKE_REQUEST`
- `FRONTEND_TOOL_INVOKE_RESPONSE` → `CLIENT_TOOL_INVOKE_RESPONSE`
- `FRONTEND_PLUGINS_REGISTERED` → `CLIENT_TOOL_GROUPS_REGISTERED`

**Helper Functions:**
- `createCollapsedPlugin()` → `createCollapsedToolGroup()`
- `createExpandedPlugin()` → `createExpandedToolGroup()`

**AgentClient Methods:**
- `registerPlugin()` → `registerToolGroup()`
- `registerPlugins()` → `registerToolGroups()`
- `unregisterPlugin()` → `unregisterToolGroup()`
- `plugins` property → `toolGroups` property

**Event Handlers:**
- `onFrontendToolInvoke` → `onClientToolInvoke`
- `onFrontendPluginsRegistered` → `onClientToolGroupsRegistered`

**Stream Options:**
- `frontendPlugins` → `clientToolGroups`
- `resetFrontendState` → `resetClientState`

**Type Guards:**
- `isFrontendToolInvokeRequestEvent()` → `isClientToolInvokeRequestEvent()`
- `isFrontendPluginsRegisteredEvent()` → `isClientToolGroupsRegisteredEvent()`

#### Wire Protocol Changes

If you have custom integrations that parse events directly:

| Old Event Type | New Event Type |
|----------------|----------------|
| `FRONTEND_TOOL_INVOKE_REQUEST` | `CLIENT_TOOL_INVOKE_REQUEST` |
| `FRONTEND_TOOL_INVOKE_RESPONSE` | `CLIENT_TOOL_INVOKE_RESPONSE` |
| `FRONTEND_PLUGINS_REGISTERED` | `CLIENT_TOOL_GROUPS_REGISTERED` |

### Changed

- Renamed `FrontendTools/` directory to `ClientTools/` in C# codebase
- Renamed `frontend-tools.ts` to `client-tools.ts` in TypeScript client
- Updated all documentation to use new terminology
- Event wire format now uses `CLIENT_TOOL_*` instead of `FRONTEND_TOOL_*`

### Removed

- `HPD-Agent.Plugins.FileSystem` package (moved to `HPD-Agent.Tools.FileSystem`)
- `HPD-Agent.Plugins.WebSearch` package (moved to `HPD-Agent.Tools.WebSearch`)

### Migration

See [MIGRATION.md](MIGRATION.md) for step-by-step upgrade instructions with find/replace patterns.

---

## [0.1.4] - Previous Release

Last stable release before terminology changes.

### Features
- Plugin-based tool registration
- Frontend tools for client-side execution
- Conversation threading with branching support
- MCP server integration
- Skill-based workflows

---

## Versioning Policy

### Pre-1.0 (Current)
- **0.x.y** releases may contain breaking changes in minor versions
- Patch versions (0.x.Y) are backward compatible bug fixes
- Migration guides are provided for breaking changes

### Post-1.0 (Future)
- **Major (X.0.0)**: Breaking changes
- **Minor (x.Y.0)**: New features, backward compatible
- **Patch (x.y.Z)**: Bug fixes, backward compatible
