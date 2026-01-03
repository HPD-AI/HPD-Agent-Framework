# NuGet Package Migration Guide

## Problem

The HPD-Agent packages were initially published to NuGet.org with hyphenated names (`HPD-Agent.*`) but the correct naming convention uses dots (`HPD.Agent.*`).

## Current State

- **Old packages on NuGet.org**: `HPD-Agent.Providers.*` (with hyphens)
- **Correct packages**: `HPD.Agent.*` (with dots)
- **Version**: 0.1.1 is correctly named, ready to publish

## What You Need to Do

### 1. Unlist Old Hyphenated Packages on NuGet.org

You cannot delete packages from NuGet.org, but you can **unlist** them so they don't show in search:

1. Go to [nuget.org](https://www.nuget.org) and sign in
2. Click your account name (top right) → **Manage packages** → **Published packages**
3. For each `HPD-Agent.*` package (with hyphens):
   - Click **Manage package**
   - Expand the **Listing** section
   - Select the version (e.g., `0.1.1-alpha`)
   - Uncheck **"List in search results"**
   - Click **Save**

Old packages to unlist:
- `HPD-Agent.Providers.Anthropic`
- `HPD-Agent.Providers.AzureAIInference`
- `HPD-Agent.Providers.Bedrock`
- `HPD-Agent.Providers.GoogleAI`
- `HPD-Agent.Providers.HuggingFace`
- `HPD-Agent.Providers.Mistral`
- `HPD-Agent.Providers.Ollama`
- `HPD-Agent.Providers.OnnxRuntime`
- `HPD-Agent.Providers.OpenAI`
- `HPD-Agent.Providers.OpenRouter`
- `HPD.Agent.SourceGenerator` (version 0.1.1 - this is now embedded in HPD.Agent.Framework)

### 2. Optional: Deprecate Old Packages

After unlisting, consider deprecating them with a message pointing to the new package names:

1. In the package management page, find the **Deprecation** section
2. Mark as deprecated
3. Add message: "This package has been renamed to `HPD.Agent.Providers.{Name}` (with dots instead of hyphens). Please use the new package."
4. Set alternate package: `HPD.Agent.Providers.{Name}`

### 3. Publish New Packages

```bash
# Build and pack packages locally
./nuget-publish.sh pack 0.1.1

# Verify packages look correct (should all be HPD.Agent.*)
ls -la .nupkg/*.0.1.1.nupkg

# Push to NuGet.org
./nuget-publish.sh push 0.1.1 YOUR_API_KEY

# Or do both in one step
./nuget-publish.sh all 0.1.1 YOUR_API_KEY
```

## Using the New Publishing Script

The new `nuget-publish.sh` script replaces all the old scripts:

### Commands

```bash
# Pack packages locally
./nuget-publish.sh pack <version>

# Push packages to NuGet.org
./nuget-publish.sh push <version> <api-key>

# Pack and push in one step
./nuget-publish.sh all <version> <api-key>
```

### Examples

```bash
# Pack version 0.1.2 locally
./nuget-publish.sh pack 0.1.2

# Push to NuGet.org (method 1: pass API key)
./nuget-publish.sh push 0.1.2 oy2a...

# Push to NuGet.org (method 2: use environment variable)
export NUGET_API_KEY="oy2a..."
./nuget-publish.sh push 0.1.2

# Do everything in one step
./nuget-publish.sh all 0.1.2 oy2a...
```

## Testing Packages Locally

To test packages before publishing:

```bash
# 1. Pack the packages
./nuget-publish.sh pack 0.1.2

# 2. Create a test project
mkdir ~/test-project && cd ~/test-project
dotnet new console

# 3. Add package from local source
dotnet add package HPD.Agent.Framework --version 0.1.2 --source ~/Documents/HPD-Agent/.nupkg
```

**Important**: You must be IN a project directory (with a `.csproj` file) to use `dotnet add package`.

## Clean Up Old Scripts

Once you've verified the new script works, you can delete:

- `delete-packages.sh`
- `fix-packageids.sh`
- `publish-local.sh`
- `push-packages.sh`

Keep only:
- `nuget-publish.sh` (the new unified script)

## Package Naming Reference

All packages now use dots:

- `HPD.Agent.Framework` (includes embedded source generator)
- `HPD.Agent.FFI`
- `HPD.Agent.MCP`
- `HPD.Agent.Memory`
- `HPD.Agent.TextExtraction`
- `HPD.Agent.Toolkits.FileSystem`
- `HPD.Agent.Toolkits.WebSearch`
- `HPD.Agent.Providers.Anthropic`
- `HPD.Agent.Providers.AzureAIInference`
- `HPD.Agent.Providers.Bedrock`
- `HPD.Agent.Providers.GoogleAI`
- `HPD.Agent.Providers.HuggingFace`
- `HPD.Agent.Providers.Mistral`
- `HPD.Agent.Providers.Ollama`
- `HPD.Agent.Providers.OnnxRuntime`
- `HPD.Agent.Providers.OpenAI`
- `HPD.Agent.Providers.OpenRouter`

**Note**: `HPD.Agent.SourceGenerator` is NOT a separate package. It's embedded as an analyzer inside `HPD.Agent.Framework`. The standalone package (version 0.1.1) should be unlisted.
