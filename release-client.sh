#!/bin/bash
#
# HPD-Agent Client NPM Release Script
# Usage: ./release-client.sh <version>
# Example: ./release-client.sh 0.2.0
#

set -e

if [ -z "$1" ]; then
    echo "Usage: $0 <version>"
    echo "Example: $0 0.2.0"
    exit 1
fi

VERSION=$1
TAG="client-v$VERSION"

echo "=========================================="
echo "HPD-Agent Client NPM Release Script"
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
echo "1/4 Installing dependencies..."
cd hpd-agent-client
npm ci

echo ""
echo "2/4 Running tests..."
npm run test

echo ""
echo "3/4 Building..."
npm run build

cd ..

echo ""
echo "4/4 Creating git tag..."
git tag -a "$TAG" -m "Release client $VERSION"
echo "   Created tag: $TAG"

echo ""
echo "5/5 Pushing tag..."
git push origin "$TAG"

echo ""
echo "=========================================="
echo "✓ NPM Release prepared: $TAG"
echo "=========================================="
echo ""
echo "Next steps:"
echo "1. GitHub Actions will automatically:"
echo "   - Run tests"
echo "   - Build the package"
echo "   - Publish to npm"
echo "   - Create GitHub release with artifacts"
echo ""
echo "2. Monitor the workflow at:"
echo "   https://github.com/HPD-AI/HPD-Agent/actions"
echo ""
echo "3. Verify package at:"
echo "   https://www.npmjs.com/package/hpd-agent-client"
