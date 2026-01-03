# Release Guide

## Overview

This project uses GitHub Actions to automate building, testing, and publishing:
- **NuGet packages** (.NET libraries) - Triggered by `v*.*.*` tags
- **NPM package** (TypeScript client) - Triggered by `client-v*.*.*` tags

Both releases are automated and triggered by git tags.

## Release Process

### .NET Libraries (NuGet)

**Tag Format:** `v0.2.0`

Use the release script:

```bash
./release.sh 0.2.0
```

This will:
- Build and test locally
- Create git tag `v0.2.0`
- Push to GitHub
- GitHub Actions packs & publishes 15 packages to NuGet.org

### TypeScript Client (NPM)

**Tag Format:** `client-v0.2.0`

Use the client release script:

```bash
./release-client.sh 0.2.0
```

This will:
- Install dependencies
- Run tests
- Build the package
- Create git tag `client-v0.2.0`
- Push to GitHub
- GitHub Actions publishes to npm

---

## NuGet Release Details

## Configuration

### NuGet API Key

The NuGet workflow requires a GitHub secret with your NuGet API key.

**Setup:**
1. Get your API key from https://www.nuget.org/account/apikeys
2. Add to GitHub: `Settings → Secrets → NUGET_API_KEY`

### NPM Token

The NPM workflow requires a GitHub secret with your NPM authentication token.

**Setup:**
1. Get your token from https://www.npmjs.com/settings/tokens
2. Add to GitHub: `Settings → Secrets → NPM_TOKEN`

### Project Configuration

Each NuGet package is configured in its `.csproj` file:

```xml
<PropertyGroup>
  <PackageId>HPD.Agent.Framework</PackageId>
  <Version>0.2.0</Version>
  <Authors>Einstein Essibu</Authors>
  <Description>...</Description>
  <PackageTags>ai;agent;llm</PackageTags>
</PropertyGroup>
```

The workflow overrides the version at pack time to ensure consistency.

## Pre-release Versions

For pre-release versions, use semantic versioning:

**NuGet:**
```bash
./release.sh 0.2.0-alpha
./release.sh 0.2.0-beta.1
./release.sh 0.2.0-rc.1
```

**NPM:**
```bash
./release-client.sh 0.2.0-alpha
./release-client.sh 0.2.0-beta.1
./release-client.sh 0.2.0-rc.1
```

## Manual Release (Without Tag)

You can also use the workflow dispatch to release without creating a tag:

```bash
gh workflow run publish-nuget.yml
```

And provide the NuGet API key when prompted.

## Packages Included

### .NET - Core Framework
- **HPD.Agent.Framework** - Main agentic AI framework with middleware pipeline

### Toolkits
- **HPD.Agent.Toolkits.FileSystem** - File system operations Toolkit
- **HPD.Agent.Toolkits.WebSearch** - Web search capabilities Toolkit

### Extensions
- **HPD.Agent.Memory** - Dynamic memory system for agents
- **HPD.Agent.MCP** - Model Context Protocol support

### Providers (11 total)
- **HPD-Agent.Providers.OpenAI** - OpenAI and Azure OpenAI
- **HPD-Agent.Providers.Anthropic** - Claude (Anthropic)
- **HPD-Agent.Providers.GoogleAI** - Google AI
- **HPD-Agent.Providers.Mistral** - Mistral AI
- **HPD-Agent.Providers.OpenRouter** - OpenRouter multi-model gateway
- **HPD-Agent.Providers.Ollama** - Local Ollama models
- **HPD-Agent.Providers.HuggingFace** - HuggingFace models
- **HPD-Agent.Providers.Bedrock** - AWS Bedrock
- **HPD-Agent.Providers.AzureAIInference** - Azure AI Inference
- **HPD-Agent.Providers.OnnxRuntime** - ONNX Runtime (local inference)

### NPM - TypeScript Client
- **hpd-agent-client** - TypeScript SDK for HPD-Agent with SSE and WebSocket transports

## Troubleshooting

### NuGet API Key Not Found
Error: "NUGET_API_KEY is not set"

**Solution:** Add the secret to GitHub:
1. Go to repo → Settings → Secrets
2. Create `NUGET_API_KEY` secret

### NPM Token Not Found
Error: "npm ERR! 401 Unauthorized"

**Solution:** Add the secret to GitHub:
1. Go to repo → Settings → Secrets
2. Create `NPM_TOKEN` secret

### Build Fails
Check the workflow logs at GitHub Actions to see the build error.

### Package Already Exists
The workflow uses `--skip-duplicate` to avoid errors if a package version already exists.

To re-release, increment the version number.

## Links

- **NuGet Packages:** https://www.nuget.org/packages?q=HPD.Agent
- **NPM Package:** https://www.npmjs.com/package/hpd-agent-client
- **GitHub Actions:** https://github.com/HPD-AI/HPD-Agent/actions
- **GitHub Releases:** https://github.com/HPD-AI/HPD-Agent/releases
- **Semantic Versioning:** https://semver.org/
