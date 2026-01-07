# LegacySupport

This folder will contain polyfill source files for legacy framework support.

## Current Status

**Not implemented** - HPD-Agent currently targets modern .NET only (net8.0+).

## Enabling Legacy Support

To enable legacy support in the future:

1. Add polyfill files following `InternalDocs/HPD.Agent/Proposals/Compatibility/04-PolyfillSources.md`
2. Update `eng/MSBuild/LegacySupport.props` following `03-LegacySupportProps.md`
3. Set `<EnableLegacySupport>true</EnableLegacySupport>` in `Directory.Build.props`
4. Follow the API replacement guide in `09-GapAnalysis.md`

## Polyfills Needed (When Enabling)

| Polyfill | Purpose |
|----------|---------|
| IsExternalInit | `init` keyword |
| RequiredMemberAttribute | `required` keyword |
| CompilerFeatureRequiredAttribute | Required for `required` |
| SystemIndex | `^` and `..` operators |
| DiagnosticAttributes | Nullable annotations |
| TrimAttributes | AOT/Trimming attributes |
| CallerArgumentExpressionAttribute | ThrowHelper support |
