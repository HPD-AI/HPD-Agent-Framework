# HPD-Agent 0.2.1 Release Notes (In Progress)

## 🎉 New Features

### HPD-Agent.MAUI - Feature Parity Achieved
- ✅ **Asset Management**: Added `UploadAsset()`, `ListAssets()`, and `DeleteAsset()` methods to `HybridWebViewAgentProxy`
- ✅ **Middleware Responses**: Implemented `RespondToPermission()` and `RespondToClientTool()` for full middleware integration
- ✅ **TypeScript Transport**: Updated `maui.ts` transport to properly serialize middleware requests with session tracking
- 🎯 **Full Feature Parity**: MAUI now has 100% feature parity with ASP.NET Core hosting

### HPD-Agent.Hosting - DTO Enhancements
- Added `SessionId` field to `PermissionResponseRequest` and `ClientToolResponseRequest` to support MAUI transport pattern
- Maintains backward compatibility with ASP.NET Core (SessionId is optional when using route parameters)

## 📚 Documentation
- Added `MAUI_COMPLETION_SUMMARY.md` with detailed implementation guide

---

# HPD-Agent 0.2.0 Release Notes

## 🚨 Breaking Changes

This is a major release with significant architectural improvements. **All provider packages have breaking changes.**

### Package Renames
- `HPD-Agent.Plugins.FileSystem` → `HPD-Agent.Toolkit.FileSystem`
- `HPD-Agent.Plugins.WebSearch` → `HPD-Agent.Toolkit.WebSearch`
- `FileSystemPlugin` class → `FileSystemToolkit`

### Core Framework Changes
- **Session Management**: Added asset management system with `IAssetStore` interface
- **Middleware**: Removed `ContainerErrorRecoveryMiddleware`, refactored `ContainerMiddleware`
- **Provider Discovery**: Changed from auto-discovery to explicit module pattern
- **Namespace Changes**: All `Plugins` → `Toolkit`

### Audio Architecture Overhaul
- Deleted `AudioPipelineConfig` → replaced with modular `AudioConfig`
- Split audio providers into STT/TTS/VAD modules
- Deleted monolithic audio provider classes
- New provider factories for each audio capability

### Provider Module Pattern (ALL PROVIDERS)
All provider packages now use a standardized module pattern:
- Explicit configuration via `*ProviderConfig`
- JSON serialization contexts
- Enhanced error handling
- Builder extensions for fluent API

**Affected packages:**
- HPD-Agent.Providers.Anthropic
- HPD-Agent.Providers.OpenAI
- HPD-Agent.Providers.AzureAIInference (deprecated - see below)
- HPD-Agent.Providers.Bedrock
- HPD-Agent.Providers.GoogleAI
- HPD-Agent.Providers.HuggingFace
- HPD-Agent.Providers.Mistral
- HPD-Agent.Providers.Ollama
- HPD-Agent.Providers.OnnxRuntime
- HPD-Agent.Providers.OpenRouter
- HPD-Agent.AudioProviders.OpenAI
- HPD-Agent.AudioProviders.ElevenLabs

## ✨ New Features

### New Packages
- **HPD.Events** (0.1.0): Standalone event architecture library
- **HPD.Graph.Abstractions** (0.1.0): Graph workflow abstractions
- **HPD.Graph.Core** (0.1.0): Graph workflow implementation
- **HPD.MultiAgent** (0.1.0): Multi-agent coordination and workflows
- **HPD-Agent.Providers.AzureAI** (0.1.0): New Azure AI provider (replaces AzureAIInference)

### Asset Management System
- `IAssetStore` interface for session asset storage
- `InMemoryAssetStore` for in-memory asset management
- `LocalFileAssetStore` for file-based asset storage
- `AssetUploadMiddleware` for automatic asset handling
- `SessionStoreExtensions` for enhanced session operations

### Multi-Target Framework Support
- Now supports .NET 8.0, 9.0, and 10.0
- Improved CI pipeline with matrix builds for Linux/Windows/macOS
- Code coverage reporting

### Enhanced Provider Features
- **Anthropic**: Added `AnthropicSchemaFixingChatClient` for schema compatibility
- **All Providers**: Enhanced error handlers with detailed error mapping
- **All Providers**: Provider auto-discovery for easier configuration

### Multi-Agent & Graph Workflows
- Subagent and multiagent chat client inheritance
- Event bubbling across agent hierarchies
- Hierarchical execution context with AgentId/ParentAgentId chains
- Deferred agent building for runtime configuration
- Workflow events (WorkflowStartedEvent, WorkflowCompletedEvent, etc.)

### Audio Improvements
- Modular STT/TTS/VAD architecture
- Builder pattern for AudioRunConfig
- Validation system for audio configurations
- Provider-specific factories for audio capabilities

### Middleware Enhancements
- Automated middleware state saving
- Improved scoping system
- Tool visibility management
- Enhanced error tracking
- Circuit breaker improvements

##  Improvements

### Developer Experience
- VitePress documentation setup
- Provider-specific READMEs with examples
- Improved error messages across all providers
- Better type safety with JSON contexts

### Build & CI
- Multi-framework build support
- Automated code coverage checks
- Path filtering for workflows
- Merge queue support

### Performance
- Parallel package publishing in CI
- Optimized middleware pipeline
- Improved session serialization

## 📦 Deprecated

- **HPD-Agent.Providers.AzureAIInference**: Use `HPD-Agent.Providers.AzureAI` instead
  - See `DEPRECATION_NOTICE.md` for migration guide

## 🐛 Bug Fixes

- Fixed container middleware scoping issues
- Fixed audio provider registration
- Fixed session serialization edge cases
- Improved error recovery in middleware pipeline

## 📋 Migration Guide

See the full migration guide in `/tmp/breaking_changes_analysis.md` or visit our documentation.

### Quick Start
1. Update package names (`Plugins` → `Tools`)
2. Update namespaces in your code
3. Update provider configuration to use explicit configs
4. Update audio configuration to use modular structure
5. Test thoroughly - all providers have breaking changes

## 📊 Statistics

- **172 files changed**: 7,221 insertions, 10,036 deletions
- **20+ commits** since v0.1.1
- **5 new packages**
- **9 packages with breaking changes**

## 🙏 Contributors

- Einstein Essibu (@einsteinessibu)

## 📝 Full Changelog

For the complete list of changes, see: `git log v0.1.1..v0.2.0`

---

**Release Date**: January 2026
**Previous Version**: 0.1.1 (December 2025)
