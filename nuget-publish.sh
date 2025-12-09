#!/bin/bash
#
# HPD-Agent Unified NuGet Publishing Script
# Usage: ./nuget-publish.sh <command> <version> [options]
#
# Commands:
#   pack <version>              - Build and pack packages locally
#   push <version> <api-key>    - Push packages to NuGet.org
#   all <version> <api-key>     - Pack and push in one step
#
# Examples:
#   ./nuget-publish.sh pack 0.1.2
#   ./nuget-publish.sh push 0.1.2 your-api-key
#   ./nuget-publish.sh all 0.1.2 your-api-key
#

set -e

NUPKG_DIR="./.nupkg"

# All projects to pack (with correct paths)
# Note: HPD-Agent.SourceGenerator is NOT included - it's embedded in HPD.Agent.Framework
PROJECTS=(
    "HPD-Agent/HPD-Agent.csproj"
    "HPD-Agent.FFI/HPD-Agent.FFI.csproj"
    "HPD-Agent.MCP/HPD-Agent.MCP.csproj"
    "HPD-Agent.Memory/HPD-Agent.Memory.csproj"
    "HPD-Agent.TextExtraction/HPD-Agent.TextExtraction.csproj"
    "HPD-Agent.Plugins/HPD-Agent.Plugins.FileSystem/HPD-Agent.Plugins.FileSystem.csproj"
    "HPD-Agent.Plugins/HPD-Agent.Plugins.WebSearch/HPD-Agent.Plugins.WebSearch.csproj"
    "HPD-Agent.Providers/HPD-Agent.Providers.Anthropic/HPD-Agent.Providers.Anthropic.csproj"
    "HPD-Agent.Providers/HPD-Agent.Providers.AzureAIInference/HPD-Agent.Providers.AzureAIInference.csproj"
    "HPD-Agent.Providers/HPD-Agent.Providers.Bedrock/HPD-Agent.Providers.Bedrock.csproj"
    "HPD-Agent.Providers/HPD-Agent.Providers.GoogleAI/HPD-Agent.Providers.GoogleAI.csproj"
    "HPD-Agent.Providers/HPD-Agent.Providers.HuggingFace/HPD-Agent.Providers.HuggingFace.csproj"
    "HPD-Agent.Providers/HPD-Agent.Providers.Mistral/HPD-Agent.Providers.Mistral.csproj"
    "HPD-Agent.Providers/HPD-Agent.Providers.Ollama/HPD-Agent.Providers.Ollama.csproj"
    "HPD-Agent.Providers/HPD-Agent.Providers.OnnxRuntime/HPD-Agent.Providers.OnnxRuntime.csproj"
    "HPD-Agent.Providers/HPD-Agent.Providers.OpenAI/HPD-Agent.Providers.OpenAI.csproj"
    "HPD-Agent.Providers/HPD-Agent.Providers.OpenRouter/HPD-Agent.Providers.OpenRouter.csproj"
)

# Expected package IDs (with dots, not hyphens)
# Note: HPD.Agent.SourceGenerator is NOT a separate package - it's embedded in HPD.Agent.Framework
PACKAGE_IDS=(
    "HPD.Agent.Framework"
    "HPD.Agent.FFI"
    "HPD.Agent.MCP"
    "HPD.Agent.Memory"
    "HPD.Agent.TextExtraction"
    "HPD.Agent.Plugins.FileSystem"
    "HPD.Agent.Plugins.WebSearch"
    "HPD.Agent.Providers.Anthropic"
    "HPD.Agent.Providers.AzureAIInference"
    "HPD.Agent.Providers.Bedrock"
    "HPD.Agent.Providers.GoogleAI"
    "HPD.Agent.Providers.HuggingFace"
    "HPD.Agent.Providers.Mistral"
    "HPD.Agent.Providers.Ollama"
    "HPD.Agent.Providers.OnnxRuntime"
    "HPD.Agent.Providers.OpenAI"
    "HPD.Agent.Providers.OpenRouter"
)

function show_usage() {
    echo "Usage: $0 <command> <version> [options]"
    echo ""
    echo "Commands:"
    echo "  pack <version>              - Build and pack packages locally"
    echo "  push <version> <api-key>    - Push packages to NuGet.org"
    echo "  all <version> <api-key>     - Pack and push in one step"
    echo ""
    echo "Examples:"
    echo "  $0 pack 0.1.2"
    echo "  $0 push 0.1.2 oy2a..."
    echo "  $0 all 0.1.2 oy2a..."
    echo "  export NUGET_API_KEY='oy2a...' && $0 push 0.1.2"
}

function pack_packages() {
    local VERSION=$1

    echo "=========================================="
    echo "HPD-Agent NuGet Pack"
    echo "=========================================="
    echo "Version: $VERSION"
    echo "Output: $NUPKG_DIR"
    echo ""

    # Create output directory
    mkdir -p "$NUPKG_DIR"

    # Clean previous packages for this version
    echo "Cleaning previous packages for version $VERSION..."
    rm -f "$NUPKG_DIR"/*."$VERSION".nupkg "$NUPKG_DIR"/*."$VERSION".snupkg

    # Build solution first
    echo ""
    echo "Building solution..."
    dotnet build --configuration Release

    # Pack each project
    echo ""
    echo "Packing packages..."
    local PACKED=0
    local FAILED=0

    for PROJECT in "${PROJECTS[@]}"; do
        if [ -f "$PROJECT" ]; then
            echo "  Packing $PROJECT..."
            if dotnet pack "$PROJECT" -c Release -o "$NUPKG_DIR" -p:Version="$VERSION" --no-build > /dev/null 2>&1; then
                ((PACKED++))
                echo "    ✓ Success"
            else
                ((FAILED++))
                echo "    ✗ Failed"
            fi
        else
            echo "  ⚠ Skipping $PROJECT (not found)"
        fi
    done

    echo ""
    echo "=========================================="
    echo "Pack Results:"
    echo "  Packed: $PACKED"
    echo "  Failed: $FAILED"
    echo "=========================================="
    echo ""

    # Verify PackageIds (accept both HPD-Agent.* and HPD.Agent.* formats)
    echo "Verifying package names..."
    local INVALID_NAMES=0
    for nupkg in "$NUPKG_DIR"/*."$VERSION".nupkg; do
        if [ -f "$nupkg" ]; then
            filename=$(basename "$nupkg")
            # Accept either HPD-Agent.* or HPD.Agent.* format
            if ! [[ "$filename" =~ ^(HPD-Agent|HPD\.Agent)\. ]]; then
                echo "  ⚠ WARNING: Found invalid package: $filename"
                ((INVALID_NAMES++))
            fi
        fi
    done

    if [ $INVALID_NAMES -gt 0 ]; then
        echo ""
        echo "ERROR: Found $INVALID_NAMES packages with invalid naming!"
        exit 1
    fi

    echo ""
    echo "✓ All packages have correct naming (HPD-Agent.* or HPD.Agent.*)"
    echo ""
    echo "Packages location: $NUPKG_DIR"
    echo ""
    echo "To test locally:"
    echo "  cd <your-test-project>"
    echo "  dotnet add package HPD.Agent.Framework --version $VERSION --source $PWD/$NUPKG_DIR"
}

function push_packages() {
    local VERSION=$1
    local API_KEY="${2:-$NUGET_API_KEY}"

    if [ -z "$API_KEY" ]; then
        echo "Error: NUGET_API_KEY not provided"
        echo ""
        echo "Options:"
        echo "  1. Pass as argument: $0 push $VERSION your-api-key"
        echo "  2. Set environment: export NUGET_API_KEY='your-key' && $0 push $VERSION"
        exit 1
    fi

    echo "=========================================="
    echo "HPD-Agent NuGet Push"
    echo "=========================================="
    echo "Version: $VERSION"
    echo ""

    # Find packages
    local PACKAGES=$(find "$NUPKG_DIR" -name "*.$VERSION.nupkg" ! -name "*.snupkg" 2>/dev/null | wc -l)

    if [ "$PACKAGES" -eq 0 ]; then
        echo "Error: No packages found for version $VERSION in $NUPKG_DIR"
        echo "Run: $0 pack $VERSION"
        exit 1
    fi

    echo "Found $PACKAGES packages to publish"
    echo ""

    local PUBLISHED=0
    local FAILED=0

    for nupkg in "$NUPKG_DIR"/*."$VERSION".nupkg; do
        if [ -f "$nupkg" ] && [[ "$nupkg" != *.snupkg ]]; then
            filename=$(basename "$nupkg")
            echo "Publishing $filename..."

            if dotnet nuget push "$nupkg" -k "$API_KEY" -s https://api.nuget.org/v3/index.json --skip-duplicate; then
                echo "  ✓ Published"
                ((PUBLISHED++))
            else
                echo "  ✗ Failed"
                ((FAILED++))
            fi
        fi
    done

    echo ""
    echo "=========================================="
    echo "Push Results:"
    echo "  Published: $PUBLISHED"
    echo "  Failed: $FAILED"
    echo "=========================================="
    echo ""
    echo "Verify at: https://www.nuget.org/packages?q=HPD.Agent"
}

# Main script
COMMAND=$1
VERSION=$2

if [ -z "$COMMAND" ] || [ -z "$VERSION" ]; then
    show_usage
    exit 1
fi

case "$COMMAND" in
    pack)
        pack_packages "$VERSION"
        ;;
    push)
        API_KEY=$3
        push_packages "$VERSION" "$API_KEY"
        ;;
    all)
        API_KEY=$3
        pack_packages "$VERSION"
        push_packages "$VERSION" "$API_KEY"
        ;;
    *)
        echo "Error: Unknown command '$COMMAND'"
        echo ""
        show_usage
        exit 1
        ;;
esac
