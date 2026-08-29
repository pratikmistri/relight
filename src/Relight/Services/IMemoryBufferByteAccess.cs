using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Relight.Services;

/// <summary>Grants direct access to the bytes backing a WinRT memory buffer.</summary>
[ComImport]
[Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal unsafe interface IMemoryBufferByteAccess
{
    void GetBuffer(out byte* buffer, out uint capacity);
}
