// Copyright (c) 2025 Einstein Essibu. All rights reserved.

// Polyfill for C# 9 record types in .NET Standard 2.0
// This type is required by the compiler to support init-only properties and records

namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}
