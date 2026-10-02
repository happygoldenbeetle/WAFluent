using System.Runtime.InteropServices;

namespace WhatsAppNative.Services;

/// <summary>
/// Windows' own H.264 encoder (the Media Foundation transform), for video calls: NV12 pictures
/// in, one Annex-B access unit out for each, the way WhatsApp's calls carry video — Baseline
/// profile, no B-frames, low delay, a whole picture (with its SPS and PPS) every couple of
/// seconds and whenever the other side asks for one. Media Foundation is plain COM, called
/// here through the interfaces' method tables.
/// </summary>
public sealed unsafe class H264Encoder : IDisposable
{
    private static readonly Guid ClsidEncoder = new("6ca50344-051a-4ded-9779-a43305165e35");
    private static readonly Guid IidTransform = new("bf94c121-5b05-4e6f-8000-ba598961414d");
    private static readonly Guid IidCodecApi = new("901db4c7-31ce-41a2-85dc-8fa0bf41b8da");

    private static readonly Guid MajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid Subtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid AvgBitrate = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    private static readonly Guid FrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d");
    private static readonly Guid FrameRate = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    private static readonly Guid PixelAspectRatio = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    private static readonly Guid InterlaceMode = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    private static readonly Guid Mpeg2Profile = new("ad76a80b-2d5c-4e0b-b375-64e520137036");
    private static readonly Guid Video = new("73646976-0000-0010-8000-00aa00389b71");
    private static readonly Guid H264 = new("34363248-0000-0010-8000-00aa00389b71");
    private static readonly Guid Nv12 = new("3231564e-0000-0010-8000-00aa00389b71");

    private static readonly Guid LowLatency = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    private static readonly Guid RateControl = new("1c0608e9-370c-4710-8a58-cb6181c42423");
    private static readonly Guid MeanBitrate = new("f7222374-2144-4815-b550-a37f8e12ee52");
    private static readonly Guid GopSize = new("95f31b26-95a4-41aa-9303-246a7fc6eef1");
    private static readonly Guid BFrames = new("8d390aac-dc5c-4200-b57f-814d04babab2");
    private static readonly Guid ForceKeyFrame = new("398c1b98-8353-475a-9ef2-8f265d260345");

    private const uint MfVersion = 0x00020070;
    private const int NeedMoreInput = unchecked((int)0xC00D6D72);
    private const uint BeginStreaming = 0x10000000, StartOfStream = 0x10000003;
    private const uint ProvidesSamples = 0x100;
    private const ushort VtBool = 11, VtUi4 = 19;

    // Method slots (after IUnknown's three): IMFAttributes, IMFSample, IMFMediaBuffer, IMFTransform, ICodecAPI.
    private const int SetUInt32 = 21, SetUInt64 = 22, SetGuid = 24;
    private const int SetSampleTime = 36, SetSampleDuration = 38, ConvertToContiguousBuffer = 41, AddBuffer = 42;
    private const int BufferLock = 3, BufferUnlock = 4, BufferSetCurrentLength = 6;
    private const int GetOutputStreamInfo = 7, SetInputType = 15, SetOutputType = 16, ProcessMessage = 23, ProcessInput = 24, ProcessOutput = 25;
    private const int CodecSetValue = 9;

    [DllImport("mfplat.dll")] private static extern int MFStartup(uint version, uint flags);
    [DllImport("mfplat.dll")] private static extern int MFCreateMediaType(out nint type);
    [DllImport("mfplat.dll")] private static extern int MFCreateSample(out nint sample);
    [DllImport("mfplat.dll")] private static extern int MFCreateMemoryBuffer(uint size, out nint buffer);
    [DllImport("ole32.dll")] private static extern int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, out nint instance);

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct Variant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public uint UInt;
        [FieldOffset(8)] public short Bool;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct OutputBuffer
    {
        public uint StreamId;
        public nint Sample;
        public uint Status;
        public nint Events;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StreamInfo
    {
        public uint Flags, Size, Alignment;
    }

    private static bool _started;
    private nint _transform, _codec;
    private readonly int _frameBytes;
    private readonly long _frameTime;
    private readonly uint _outputSize;
    private readonly bool _providesSamples;
    private long _time;
    private bool _wantKeyFrame;

    public int Width { get; }
    public int Height { get; }

    /// <param name="width">Even.</param>
    /// <param name="height">Even.</param>
    /// <param name="bitrate">Bits a second.</param>
    public H264Encoder(int width, int height, int framesPerSecond, int bitrate)
    {
        (Width, Height) = (width, height);
        _frameBytes = width * height * 3 / 2;
        _frameTime = 10_000_000 / framesPerSecond;
        if (!_started)
        {
            Check(MFStartup(MfVersion, 0), "starting Media Foundation");
            _started = true;
        }
        Check(CoCreateInstance(in ClsidEncoder, 0, 1 /* in-process */, in IidTransform, out _transform), "creating the H.264 encoder");
        try
        {
            // The encoder's own settings first (it reads them when the output type is set). Ones it
            // doesn't know are skipped: the types below still give a working stream.
            if (Marshal.QueryInterface(_transform, in IidCodecApi, out _codec) >= 0)
            {
                Set(LowLatency, new Variant { Type = VtBool, Bool = -1 });
                Set(RateControl, new Variant { Type = VtUi4, UInt = 0 });   // constant bitrate
                Set(MeanBitrate, new Variant { Type = VtUi4, UInt = (uint)bitrate });
                Set(GopSize, new Variant { Type = VtUi4, UInt = (uint)(framesPerSecond * 2) });
                Set(BFrames, new Variant { Type = VtUi4, UInt = 0 });
            }

            var size = ((ulong)width << 32) | (uint)height;
            var rate = ((ulong)framesPerSecond << 32) | 1;
            Check(MFCreateMediaType(out var output), "creating the output type");
            SetGuidOn(output, MajorType, Video);
            SetGuidOn(output, Subtype, H264);
            SetUInt32On(output, AvgBitrate, (uint)bitrate);
            SetUInt64On(output, FrameSize, size);
            SetUInt64On(output, FrameRate, rate);
            SetUInt64On(output, PixelAspectRatio, (1UL << 32) | 1);
            SetUInt32On(output, InterlaceMode, 2);     // progressive
            SetUInt32On(output, Mpeg2Profile, 66);     // Baseline
            var hr = Call(_transform, SetOutputType, 0u, output, 0u);
            Marshal.Release(output);
            Check(hr, "setting the encoder's output");

            Check(MFCreateMediaType(out var input), "creating the input type");
            SetGuidOn(input, MajorType, Video);
            SetGuidOn(input, Subtype, Nv12);
            SetUInt64On(input, FrameSize, size);
            SetUInt64On(input, FrameRate, rate);
            SetUInt64On(input, PixelAspectRatio, (1UL << 32) | 1);
            SetUInt32On(input, InterlaceMode, 2);
            hr = Call(_transform, SetInputType, 0u, input, 0u);
            Marshal.Release(input);
            Check(hr, "setting the encoder's input");

            StreamInfo info;
            Check(((delegate* unmanaged[Stdcall]<nint, uint, StreamInfo*, int>)Slot(_transform, GetOutputStreamInfo))(_transform, 0, &info), "reading the encoder's output");
            _providesSamples = (info.Flags & ProvidesSamples) != 0;
            _outputSize = Math.Max(info.Size, (uint)_frameBytes);

            Check(Message(BeginStreaming), "starting the encoder");
            Check(Message(StartOfStream), "starting the encoder");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>The next picture is a whole one (the other side lost something and asked).</summary>
    public void RequestKeyFrame() => _wantKeyFrame = true;

    /// <summary>
    /// Encodes one NV12 picture (the Y plane, then the interleaved UV plane, no padding). Returns
    /// its access unit with Annex-B start codes, or null when the encoder kept it for later.
    /// </summary>
    public byte[]? Encode(ReadOnlySpan<byte> nv12)
    {
        if (_transform == 0) throw new ObjectDisposedException(nameof(H264Encoder));
        if (nv12.Length < _frameBytes) throw new ArgumentException("the picture is smaller than the encoder's size", nameof(nv12));
        if (_wantKeyFrame && _codec != 0)
        {
            _wantKeyFrame = false;
            Set(ForceKeyFrame, new Variant { Type = VtUi4, UInt = 1 });
        }

        Check(MFCreateMemoryBuffer((uint)_frameBytes, out var buffer), "allocating a picture");
        Check(MFCreateSample(out var sample), "allocating a picture");
        try
        {
            byte* data;
            Check(((delegate* unmanaged[Stdcall]<nint, byte**, uint*, uint*, int>)Slot(buffer, BufferLock))(buffer, &data, null, null), "locking a picture");
            nv12[.._frameBytes].CopyTo(new Span<byte>(data, _frameBytes));
            Call(buffer, BufferUnlock);
            Check(Call(buffer, BufferSetCurrentLength, (uint)_frameBytes), "sizing a picture");
            Check(Call(sample, AddBuffer, buffer), "filling a picture");
            Check(((delegate* unmanaged[Stdcall]<nint, long, int>)Slot(sample, SetSampleTime))(sample, _time), "timing a picture");
            Check(((delegate* unmanaged[Stdcall]<nint, long, int>)Slot(sample, SetSampleDuration))(sample, _frameTime), "timing a picture");
            _time += _frameTime;
            Check(Call(_transform, ProcessInput, 0u, sample, 0u), "encoding");
        }
        finally
        {
            Marshal.Release(sample);
            Marshal.Release(buffer);
        }
        return Drain();
    }

    /// <summary>What the encoder has ready (usually exactly the picture just given).</summary>
    private byte[]? Drain()
    {
        MemoryStream? unit = null;
        while (true)
        {
            var output = new OutputBuffer();
            nint buffer = 0;
            if (!_providesSamples)
            {
                Check(MFCreateSample(out output.Sample), "allocating the output");
                Check(MFCreateMemoryBuffer(_outputSize, out buffer), "allocating the output");
                Check(Call(output.Sample, AddBuffer, buffer), "allocating the output");
            }
            uint status;
            nint whole = 0;
            var hr = ((delegate* unmanaged[Stdcall]<nint, uint, uint, OutputBuffer*, uint*, int>)Slot(_transform, ProcessOutput))(_transform, 0, 1, &output, &status);
            try
            {
                if (hr == NeedMoreInput) break;
                Check(hr, "reading the encoder");
                Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(output.Sample, ConvertToContiguousBuffer))(output.Sample, &whole), "reading the encoder");
                byte* data;
                uint length;
                Check(((delegate* unmanaged[Stdcall]<nint, byte**, uint*, uint*, int>)Slot(whole, BufferLock))(whole, &data, null, &length), "reading the encoder");
                (unit ??= new MemoryStream()).Write(new ReadOnlySpan<byte>(data, (int)length));
                Call(whole, BufferUnlock);
            }
            finally
            {
                if (whole != 0) Marshal.Release(whole);
                if (output.Events != 0) Marshal.Release(output.Events);
                if (output.Sample != 0) Marshal.Release(output.Sample);
                if (buffer != 0) Marshal.Release(buffer);
            }
        }
        return unit?.ToArray();
    }

    // ───── COM plumbing ─────

    private static void* Slot(nint obj, int index) => (*(void***)obj)[index];

    private static int Call(nint obj, int slot) => ((delegate* unmanaged[Stdcall]<nint, int>)Slot(obj, slot))(obj);

    private static int Call(nint obj, int slot, uint a) => ((delegate* unmanaged[Stdcall]<nint, uint, int>)Slot(obj, slot))(obj, a);

    private static int Call(nint obj, int slot, nint a) => ((delegate* unmanaged[Stdcall]<nint, nint, int>)Slot(obj, slot))(obj, a);

    private static int Call(nint obj, int slot, uint a, nint b, uint c) => ((delegate* unmanaged[Stdcall]<nint, uint, nint, uint, int>)Slot(obj, slot))(obj, a, b, c);

    private int Message(uint message) => ((delegate* unmanaged[Stdcall]<nint, uint, nuint, int>)Slot(_transform, ProcessMessage))(_transform, message, 0);

    private void Set(Guid property, Variant value) =>
        ((delegate* unmanaged[Stdcall]<nint, Guid*, Variant*, int>)Slot(_codec, CodecSetValue))(_codec, &property, &value);

    private static void SetGuidOn(nint attributes, Guid key, Guid value) =>
        Check(((delegate* unmanaged[Stdcall]<nint, Guid*, Guid*, int>)Slot(attributes, SetGuid))(attributes, &key, &value), "describing the video");

    private static void SetUInt32On(nint attributes, Guid key, uint value) =>
        Check(((delegate* unmanaged[Stdcall]<nint, Guid*, uint, int>)Slot(attributes, SetUInt32))(attributes, &key, value), "describing the video");

    private static void SetUInt64On(nint attributes, Guid key, ulong value) =>
        Check(((delegate* unmanaged[Stdcall]<nint, Guid*, ulong, int>)Slot(attributes, SetUInt64))(attributes, &key, value), "describing the video");

    private static void Check(int hr, string doing)
    {
        if (hr < 0) throw new InvalidOperationException($"{doing} failed (0x{hr:X8})");
    }

    public void Dispose()
    {
        if (_codec != 0) Marshal.Release(_codec);
        if (_transform != 0) Marshal.Release(_transform);
        _codec = _transform = 0;
    }
}
