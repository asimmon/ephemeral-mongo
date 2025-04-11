// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// This was adapted from https://github.com/dotnet/runtime/blob/v9.0.4/src/libraries/System.Private.CoreLib/src/System/Diagnostics/CodeAnalysis/ExperimentalAttribute.cs

#pragma warning disable

#if !NET8_0_OR_GREATER

namespace System.Diagnostics.CodeAnalysis
{
    [AttributeUsage(AttributeTargets.Assembly |
                    AttributeTargets.Module |
                    AttributeTargets.Class |
                    AttributeTargets.Struct |
                    AttributeTargets.Enum |
                    AttributeTargets.Constructor |
                    AttributeTargets.Method |
                    AttributeTargets.Property |
                    AttributeTargets.Field |
                    AttributeTargets.Event |
                    AttributeTargets.Interface |
                    AttributeTargets.Delegate, Inherited = false)]
    internal sealed class ExperimentalAttribute : Attribute
    {
        public ExperimentalAttribute(string diagnosticId)
        {
            // ReSharper disable once ArrangeThisQualifier
            DiagnosticId = diagnosticId;
        }

        public string DiagnosticId { get; }

        public string? UrlFormat { get; set; }
    }
}

#endif // !NET8_0_OR_LATER