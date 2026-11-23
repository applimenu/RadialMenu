using System.Runtime.InteropServices;

namespace RadialMenu1;

// All calls and COM releases are on the recording worker thread.
internal sealed class ScreenMp4Writer : IDisposable
{
    private const uint MediaFoundationVersion = 0x00020070;
    private const uint Progressive = 2;
    private static readonly Guid MajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid Subtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid FrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d");
    private static readonly Guid FrameRate = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    private static readonly Guid PixelAspectRatio = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    private static readonly Guid InterlaceMode = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    private static readonly Guid AverageBitrate = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    private static readonly Guid CodecProfile = new("ad76a80b-2d5c-4e0b-b375-64e520137036");
    private static readonly Guid DefaultStride = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
    private static readonly Guid Video = new("73646976-0000-0010-8000-00aa00389b71");
    private static readonly Guid H264 = new("34363248-0000-0010-8000-00aa00389b71");
    private static readonly Guid Nv12 = new("3231564e-0000-0010-8000-00aa00389b71");

    private nint _writer;
    private readonly uint _stream;
    private readonly uint _frameBytes;
    private readonly ScreenVideoConverter _converter;
    private bool _comInitialized, _foundationStarted;

    public ScreenMp4Writer(string path, int width, int height, int framesPerSecond)
    {
        _converter = new ScreenVideoConverter(width, height);
        _frameBytes = checked((uint)(width * height * 3 / 2));
        try
        {
            Check(CoInitializeEx(0, 0));
            _comInitialized = true;
            Check(MFStartup(MediaFoundationVersion, 0));
            _foundationStarted = true;
            Check(MFCreateSinkWriterFromURL(path, 0, 0, out _writer));
            uint bitRate = (uint)Math.Clamp(width * height * 3, 800_000, 6_500_000);
            using var output = CreateMediaType(H264, width, height, framesPerSecond);
            SetUInt32(output.Pointer, AverageBitrate, bitRate);
            SetUInt32(output.Pointer, CodecProfile, 66); // H.264 Baseline
            Check(Method<AddStream>(_writer, SinkWriter.AddStream)(_writer, output.Pointer, out _stream));
            using var input = CreateMediaType(Nv12, width, height, framesPerSecond);
            SetUInt32(input.Pointer, DefaultStride, (uint)width);
            Check(Method<SetInputMediaType>(_writer, SinkWriter.SetInputMediaType)(_writer, _stream, input.Pointer, 0));
            Check(Method<SimpleCall>(_writer, SinkWriter.BeginWriting)(_writer));
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private static ComReference CreateMediaType(Guid format, int width, int height, int fps)
    {
        Check(MFCreateMediaType(out nint pointer));
        var type = new ComReference(pointer);
        try
        {
            SetGuid(pointer, MajorType, Video);
            SetGuid(pointer, Subtype, format);
            SetUInt32(pointer, InterlaceMode, Progressive);
            SetPair(pointer, FrameSize, (uint)width, (uint)height);
            SetPair(pointer, FrameRate, (uint)fps, 1);
            SetPair(pointer, PixelAspectRatio, 1, 1);
            return type;
        }
        catch
        {
            type.Dispose();
            throw;
        }
    }

    public void WriteFrame(nint pixels, long time, long duration)
    {
        var nv12 = _converter.Convert(pixels);
        Check(MFCreateMemoryBuffer(_frameBytes, out nint buffer));
        using var ownedBuffer = new ComReference(buffer);
        Check(Method<LockBuffer>(buffer, Buffer.Lock)(buffer, out nint destination, 0, 0));
        try { Marshal.Copy(nv12, 0, destination, nv12.Length); }
        finally { Check(Method<SimpleCall>(buffer, Buffer.Unlock)(buffer)); }
        Check(Method<UInt32Call>(buffer, Buffer.SetCurrentLength)(buffer, _frameBytes));

        Check(MFCreateSample(out nint sample));
        using var ownedSample = new ComReference(sample);
        Check(Method<PointerCall>(sample, Sample.AddBuffer)(sample, buffer));
        Check(Method<Int64Call>(sample, Sample.SetTime)(sample, time));
        Check(Method<Int64Call>(sample, Sample.SetDuration)(sample, duration));
        Check(Method<WriteSample>(_writer, SinkWriter.WriteSample)(_writer, _stream, sample));
    }

    public void Finish() => Check(Method<SimpleCall>(_writer, SinkWriter.Finalize)(_writer));

    public void Dispose()
    {
        if (_writer != 0)
        {
            Marshal.Release(_writer);
            _writer = 0;
        }
        if (_foundationStarted)
        {
            MFShutdown();
            _foundationStarted = false;
        }
        if (_comInitialized)
        {
            CoUninitialize();
            _comInitialized = false;
        }
    }

    private static void SetGuid(nint type, Guid key, Guid value) =>
        Check(Method<GuidAttribute>(type, Attributes.SetGuid)(type, in key, in value));

    private static void SetUInt32(nint type, Guid key, uint value) =>
        Check(Method<UInt32Attribute>(type, Attributes.SetUInt32)(type, in key, value));

    private static void SetPair(nint type, Guid key, uint high, uint low) =>
        Check(Method<UInt64Attribute>(type, Attributes.SetUInt64)(type, in key, ((ulong)high << 32) | low));

    private static void Check(int hr) => Marshal.ThrowExceptionForHR(hr);


    private static T Method<T>(nint instance, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * nint.Size));

    private sealed class ComReference(nint pointer) : IDisposable
    {
        public nint Pointer { get; } = pointer;
        public void Dispose() => Marshal.Release(Pointer);
    }

    // Vtable indexes from the COM interfaces in mfobjects.h and mfreadwrite.h.
    private static class Attributes { public const int SetUInt32 = 21, SetUInt64 = 22, SetGuid = 24; }
    private static class Sample { public const int SetTime = 36, SetDuration = 38, AddBuffer = 42; }
    private static class Buffer { public const int Lock = 3, Unlock = 4, SetCurrentLength = 6; }
    private static class SinkWriter { public const int AddStream = 3, SetInputMediaType = 4, BeginWriting = 5, WriteSample = 6, Finalize = 11; }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SimpleCall(nint instance);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int UInt32Call(nint instance, uint value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Int64Call(nint instance, long value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int PointerCall(nint instance, nint value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GuidAttribute(nint instance, in Guid key, in Guid value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int UInt32Attribute(nint instance, in Guid key, uint value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int UInt64Attribute(nint instance, in Guid key, ulong value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int LockBuffer(nint instance, out nint buffer, nint maxLength, nint length);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int AddStream(nint instance, nint type, out uint index);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetInputMediaType(nint instance, uint stream, nint type, nint attributes);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int WriteSample(nint instance, uint stream, nint sample);

    [DllImport("mfplat.dll")] private static extern int MFStartup(uint version, uint flags);
    [DllImport("mfplat.dll")] private static extern int MFShutdown();
    [DllImport("mfplat.dll")] private static extern int MFCreateMediaType(out nint mediaType);
    [DllImport("mfplat.dll")] private static extern int MFCreateMemoryBuffer(uint maxLength, out nint buffer);
    [DllImport("mfplat.dll")] private static extern int MFCreateSample(out nint sample);
    [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int MFCreateSinkWriterFromURL(string path, nint byteStream, nint attributes, out nint writer);
    [DllImport("ole32.dll")] private static extern int CoInitializeEx(nint reserved, uint flags);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
}
