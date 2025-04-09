#if NETFRAMEWORK
using System.ComponentModel;

#pragma warning disable IDE0130 // Namespace does not match folder structure
namespace System.Runtime.CompilerServices;

/// <summary>
/// Reserved to be used by the compiler for tracking metadata.
/// This class should not be used by developers in source code.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
internal static class IsExternalInit
{
}
#endif