#if NET462 || NETCOREAPP3_1
// ReSharper disable once CheckNamespace
namespace System.Runtime.CompilerServices;

// ReSharper disable once RedundantNameQualifier
using System.ComponentModel;

// Allows using init properties in netstandard2.0
[EditorBrowsable(EditorBrowsableState.Never)]
internal static class IsExternalInit
{
}
#endif