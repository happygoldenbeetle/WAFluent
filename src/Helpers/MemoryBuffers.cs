using System.Runtime.InteropServices;
using Windows.Foundation;

namespace WhatsAppNative.Helpers;

/// <summary>
/// The bytes behind one of Windows' media buffers (an audio frame, a camera picture). WinRT only
/// hands them out through IMemoryBufferByteAccess, a plain COM interface.
/// </summary>
public static unsafe class MemoryBuffers
{
    private static readonly Guid ByteAccess = new("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D");

    /// <summary>The buffer's memory, valid until <paramref name="reference"/> is disposed.</summary>
    public static Span<byte> Bytes(IMemoryBufferReference reference)
    {
        var unknown = WinRT.MarshalInterface<IMemoryBufferReference>.FromManaged(reference);
        try
        {
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in ByteAccess, out var access));
            try
            {
                byte* data;
                uint capacity;
                // IMemoryBufferByteAccess::GetBuffer, the first method after IUnknown's three.
                var getBuffer = (delegate* unmanaged[Stdcall]<nint, byte**, uint*, int>)(*(void***)access)[3];
                Marshal.ThrowExceptionForHR(getBuffer(access, &data, &capacity));
                return new Span<byte>(data, (int)capacity);
            }
            finally
            {
                Marshal.Release(access);
            }
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }
}
