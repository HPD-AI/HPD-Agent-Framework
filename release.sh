#!/bin/bash
#
# HPD-Agent NuGet Release Script
# Usage: ./release.sh <version>
# Example: ./release.sh 0.2.0
#

set -e

if [ -z "$1" ]; then
    echo "Usage: $0 <version>"
    echo "Example: $0 0.2.0"
    exit 1
fi

VERSION=$1
TAG="v$VERSION"

echo "=========================================="
echo "HPD-Agent NuGet Release Script"
echo "=========================================="
echo "Version: $VERSION"
echo "Tag: $TAG"
echo ""

# Validate version format
if ! [[ $VERSION =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[a-zA-Z0-9.]+)?$ ]]; then
    echo "Error: Invalid version format. Use semver (e.g., 0.2.0 or 0.2.0-alpha)"
    exit 1
fi

# Check if tag already exists
if git rev-parse "refs/tags/$TAG" > /dev/null 2>&1; then
    echo "Error: Tag $TAG already exists"
    exit 1
fi

# Build and test
echo ""
echo "1/4 Building project..."
dotnet build --configuration Release

echo ""
echo "2/4 Running tests..."
dotnet test --configuration Release --no-build

echo ""
echo "3/4 Creating git tag..."
git tag -a "$TAG" -m "Release $VERSION"
echo "   Created tag: $TAG"

echo ""
echo "4/4 Pushing tag..."
git push origin "$TAG"

echo ""
echo "=========================================="
echo "✓ Release prepared: $TAG"
echo "=========================================="
echo ""
echo "Next steps:"
echo "1. GitHub Actions will automatically:"
echo "   - Pack all NuGet packages"
echo "   - Run final tests"
echo "   - Push to NuGet.org"
echo "   - Create GitHub release with artifacts"
echo ""
echo "2. Monitor the workflow at:"
echo "   https://github.com/HPD-AI/HPD-Agent/actions"
echo ""
echo "3. Verify packages at:"
echo "   https://www.nuget.org/packages?q=HPD.Agent"
