# Release Scripts Guide

This document explains each release and build script and when to use them.

## Quick Start

For a complete .NET release:
```bash
./set-version.sh 0.2.0          # Set version
./release.sh 0.2.0              # Build, test, tag, and push
# GitHub Actions handles packing and publishing to NuGet
```

For a complete NPM release:
```bash
./release-client.sh 0.2.0       # Build, test, tag, and push
# GitHub Actions handles publishing to npm
```

---

## Scripts Overview

### `set-version.sh`
**Purpose:** Update version numbers across all .NET projects

**Usage:**
```bash
./set-version.sh <version> [suffix]
./set-version.sh 0.2.0              # Stable release
./set-version.sh 0.2.0 alpha        # Pre-release
./set-version.sh 0.2.0 preview      # Another pre-release variant
```

**What it does:**
- Updates `Directory.Build.props` with new `VersionPrefix`
- Sets `VersionSuffix` for pre-releases (alpha, beta, preview, etc.)
- Comments out suffix for stable releases

**When to use:**
- **First step** before any .NET release
- Before running `./release.sh` or `./nuget-publish.sh`
- Only needed once per version

**Output:** Updates committed to `Directory.Build.props`

---

### `release.sh`
**Purpose:** Complete .NET/NuGet release workflow

**Usage:**
```bash
./release.sh <version>
./release.sh 0.2.0
```

**What it does:**
1. Validates version format (semver)
2. Checks git tag doesn't already exist
3. Builds solution in Release mode
4. Runs all tests
5. Creates git tag (`v0.2.0`)
6. Pushes tag to GitHub
7. Triggers GitHub Actions for packing and publishing

**When to use:**
- When releasing a new version of the .NET packages
- After running `./set-version.sh`
- **Recommended flow** for production releases

**Next steps:** Monitor GitHub Actions to see packages published to NuGet

---

### `release-client.sh`
**Purpose:** Complete NPM release workflow for TypeScript client

**Usage:**
```bash
./release-client.sh <version>
./release-client.sh 0.2.0
```

**What it does:**
1. Validates version format (semver)
2. Checks git tag doesn't already exist
3. Installs dependencies (`npm ci`)
4. Runs tests (`npm run test`)
5. Builds package (`npm run build`)
6. Creates git tag (`client-v0.2.0`)
7. Pushes tag to GitHub
8. Triggers GitHub Actions for publishing to npm

**When to use:**
- When releasing a new version of the TypeScript client
- Independent of .NET releases (can release on different schedule)
- **Recommended flow** for NPM releases

**Next steps:** Monitor GitHub Actions to see package published to npm

---

### `nuget-publish.sh`
**Purpose:** Manual control over NuGet packing and publishing

**Usage:**
```bash
./nuget-publish.sh pack <version>                    # Pack locally
./nuget-publish.sh push <version> <api-key>          # Push to NuGet
./nuget-publish.sh all <version> <api-key>           # Pack and push

# Examples
./nuget-publish.sh pack 0.2.0
./nuget-publish.sh push 0.2.0 oy2a...
./nuget-publish.sh all 0.2.0 oy2a...

# Or use environment variable for API key
export NUGET_API_KEY="oy2a..."
./nuget-publish.sh push 0.2.0
```

**What it does:**
- **`pack`**: Builds solution and creates `.nupkg` files in `./.nupkg/`
- **`push`**: Publishes already-packed `.nupkg` files to NuGet.org
- **`all`**: Does both pack and push in one command

**When to use:**
- **Manual publishing**: When you don't want to use GitHub Actions
- **Testing packs locally**: Before publishing to NuGet
- **Troubleshooting**: Re-publish if something goes wrong
- **Development**: Testing with `--skip-duplicate` flag

**Note:** `push-packages.sh` is now redundant with this script's `push` command

---

### `publish-local.sh`
**Purpose:** Pack packages for local testing/development

**Usage:**
```bash
./publish-local.sh <version> [output-dir]
./publish-local.sh 0.2.0                 # Outputs to `./.nupkg`
./publish-local.sh 0.2.0 ./local-packages # Custom output directory
```

**What it does:**
1. Builds solution in Release mode
2. Packs all projects
3. Outputs `.nupkg` files to specified directory

**When to use:**
- Testing packages locally before publishing
- Creating a local NuGet source for development
- **Not** for publishing to NuGet.org

**After running:**
```bash
# Add local source
dotnet nuget add source "./local-packages" -n local

# Install from local source
dotnet add package HPD.Agent.Framework --version 0.2.0 -s local
```

---

### `delete-packages.sh`
**Purpose:** Emergency cleanup - remove published packages from NuGet

**Usage:**
```bash
./delete-packages.sh <version> [api-key]
./delete-packages.sh 0.1.1-alpha
./delete-packages.sh 0.1.1-alpha oy2a...

# Or use environment variable
export NUGET_API_KEY="oy2a..."
./delete-packages.sh 0.1.1-alpha
```

**What it does:**
- Deletes specific version of all packages from NuGet.org
- Requires valid NuGet API key

**When to use:**
- Only in emergencies (broken release, security issue, etc.)
- After deletion, increment version and re-release

**Warning:** This action is permanent and cannot be undone!

---

### `push-packages.sh`
**Purpose:** Simple push to NuGet (legacy - prefer `nuget-publish.sh push`)

**Status:** REDUNDANT - Use `./nuget-publish.sh push` instead

**Usage:**
```bash
./push-packages.sh <version> <api-key>
./push-packages.sh 0.2.0 oy2a...
```

---

## Release Workflows

### Standard .NET Release
```bash
# 1. Set version
./set-version.sh 0.2.0

# 2. Commit version change
git add Directory.Build.props
git commit -m "Bump version to 0.2.0"

# 3. Release (creates tag, triggers GitHub Actions)
./release.sh 0.2.0

# 4. Monitor GitHub Actions
# https://github.com/HPD-AI/HPD-Agent/actions
```

### Standard NPM Release
```bash
# 1. Release client (creates tag, triggers GitHub Actions)
./release-client.sh 0.2.0

# 2. Monitor GitHub Actions
# https://github.com/HPD-AI/HPD-Agent/actions
```

### Manual NuGet Publishing (without GitHub Actions)
```bash
# 1. Set version
./set-version.sh 0.2.0

# 2. Manually pack and push
./nuget-publish.sh all 0.2.0 <api-key>

# 3. Verify packages published
# https://www.nuget.org/packages?q=HPD.Agent
```

### Local Testing Before Release
```bash
# 1. Set version
./set-version.sh 0.2.0-test

# 2. Pack locally
./publish-local.sh 0.2.0-test ./test-packages

# 3. Test in your project
cd ~/my-test-project
dotnet nuget add source "~/HPD-Agent/test-packages" -n test
dotnet add package HPD.Agent.Framework --version 0.2.0-test -s test

# 4. After testing, reset version
./set-version.sh 0.1.9  # Back to previous
```

---

## API Key Configuration

### Getting Your NuGet API Key
1. Visit https://www.nuget.org/account/apikeys
2. Create or copy your API key
3. Keep it secret! Never commit to git.

### Using API Key Safely
```bash
# Option 1: Pass as argument (not recommended for shell history)
./nuget-publish.sh push 0.2.0 oy2a...

# Option 2: Environment variable (better - not in shell history)
export NUGET_API_KEY="oy2a..."
./nuget-publish.sh push 0.2.0

# Option 3: GitHub Actions (best - use Actions secrets)
# Configured in: Settings > Secrets and variables > Actions > NUGET_API_KEY
```

---

## Troubleshooting

### "Tag already exists"
- The version was already released
- Increment version and try again: `./release.sh 0.2.1`

### "No packages found"
- Run `./nuget-publish.sh pack 0.2.0` first
- Or check `./.nupkg/` directory exists and has `.nupkg` files

### "Authentication failed" (NuGet push)
- Verify API key is correct
- Check key has permission to push packages
- Generate new key from https://www.nuget.org/account/apikeys

### Build or test failures
- Run `dotnet build` and `dotnet test` manually to diagnose
- Fix issues, then retry release script

---

## Summary Table

| Script | Purpose | Key Action | When to Use |
|--------|---------|-----------|-----------|
| `set-version.sh` | Update version | Modifies `Directory.Build.props` | Before any .NET release |
| `release.sh` | Full .NET release | Tag & push, triggers CI/CD | Standard .NET release |
| `release-client.sh` | Full NPM release | Tag & push, triggers CI/CD | Standard NPM release |
| `nuget-publish.sh` | Manual NuGet control | Pack/Push separately or together | Manual publishing, troubleshooting |
| `publish-local.sh` | Local testing | Creates local `.nupkg` files | Pre-release testing |
| `delete-packages.sh` | Emergency cleanup | Remove from NuGet | Broken release, security issues |
| `push-packages.sh` | Simple push | Push to NuGet | **DEPRECATED** - use `nuget-publish.sh` |
