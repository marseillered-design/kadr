using System;
using System.Runtime.InteropServices;

namespace Kadr
{
    /// <summary>
    /// Writes an MP4 file (H.264 video + AAC sound) with Media Foundation, the encoder built into Windows:
    /// nothing to download or bundle. Frames come in as top-down 32-bit BGRA, sound as 48 kHz stereo 16-bit.
    /// </summary>
    sealed class Mp4Writer : IDisposable
    {
        public const int AudioRate = 48000, AudioChannels = 2;

        IMFSinkWriter writer;
        IMFMediaBuffer frameBuf;
        readonly int width, height, stride;
        int videoStream = -1, audioStream = -1;
        static bool started;

        public bool HasAudio { get { return audioStream >= 0; } }

        public Mp4Writer(string path, int width, int height, int fps, bool audio, bool hardware)
        {
            this.width = width;
            this.height = height;
            stride = width * 4;
            if (!started) { Check(MF.MFStartup(0x20070, 0)); started = true; }

            IMFAttributes attrs;
            Check(MF.MFCreateAttributes(out attrs, 2));
            if (hardware) attrs.SetUINT32(MF.READWRITE_ENABLE_HARDWARE_TRANSFORMS, 1);
            Check(MF.MFCreateSinkWriterFromURL(path, IntPtr.Zero, attrs, out writer));
            Marshal.ReleaseComObject(attrs);

            // H.264 out: about 0.1 bit per pixel; screens compress well, so this is sharp for text
            long bitrate = Math.Max(1500000L, Math.Min(24000000L, (long)(width * (long)height * fps * 0.1)));
            var vOut = Media(MF.MediaType_Video, MF.VideoFormat_H264);
            vOut.SetUINT32(MF.MT_AVG_BITRATE, (int)bitrate);
            vOut.SetUINT32(MF.MT_INTERLACE_MODE, 2);
            vOut.SetUINT64(MF.MT_FRAME_SIZE, Pack(width, height));
            vOut.SetUINT64(MF.MT_FRAME_RATE, Pack(fps, 1));
            vOut.SetUINT64(MF.MT_PIXEL_ASPECT_RATIO, Pack(1, 1));
            vOut.SetUINT32(MF.MT_MPEG2_PROFILE, 100);   // High
            Check(writer.AddStream(vOut, out videoStream));
            Marshal.ReleaseComObject(vOut);

            var vIn = Media(MF.MediaType_Video, MF.VideoFormat_RGB32);
            vIn.SetUINT32(MF.MT_INTERLACE_MODE, 2);
            vIn.SetUINT64(MF.MT_FRAME_SIZE, Pack(width, height));
            vIn.SetUINT64(MF.MT_FRAME_RATE, Pack(fps, 1));
            vIn.SetUINT64(MF.MT_PIXEL_ASPECT_RATIO, Pack(1, 1));
            vIn.SetUINT32(MF.MT_DEFAULT_STRIDE, stride);   // positive = top row first
            Check(writer.SetInputMediaType(videoStream, vIn, null));
            Marshal.ReleaseComObject(vIn);

            if (audio)
            {
                var aOut = Media(MF.MediaType_Audio, MF.AudioFormat_AAC);
                aOut.SetUINT32(MF.MT_AUDIO_BITS_PER_SAMPLE, 16);
                aOut.SetUINT32(MF.MT_AUDIO_SAMPLES_PER_SECOND, AudioRate);
                aOut.SetUINT32(MF.MT_AUDIO_NUM_CHANNELS, AudioChannels);
                aOut.SetUINT32(MF.MT_AUDIO_AVG_BYTES_PER_SECOND, 24000);   // 192 kbit/s
                Check(writer.AddStream(aOut, out audioStream));
                Marshal.ReleaseComObject(aOut);

                var aIn = Media(MF.MediaType_Audio, MF.AudioFormat_PCM);
                aIn.SetUINT32(MF.MT_AUDIO_BITS_PER_SAMPLE, 16);
                aIn.SetUINT32(MF.MT_AUDIO_SAMPLES_PER_SECOND, AudioRate);
                aIn.SetUINT32(MF.MT_AUDIO_NUM_CHANNELS, AudioChannels);
                aIn.SetUINT32(MF.MT_AUDIO_BLOCK_ALIGNMENT, AudioChannels * 2);
                aIn.SetUINT32(MF.MT_AUDIO_AVG_BYTES_PER_SECOND, AudioRate * AudioChannels * 2);
                Check(writer.SetInputMediaType(audioStream, aIn, null));
                Marshal.ReleaseComObject(aIn);
            }
            Check(writer.BeginWriting());
        }

        static IMFMediaType Media(Guid major, Guid sub)
        {
            IMFMediaType t;
            Check(MF.MFCreateMediaType(out t));
            t.SetGUID(MF.MT_MAJOR_TYPE, major);
            t.SetGUID(MF.MT_SUBTYPE, sub);
            return t;
        }

        static long Pack(int hi, int lo) { return ((long)hi << 32) | (uint)lo; }

        /// <summary>One frame: pixels = top-down BGRA rows of width*4 bytes; time and duration in 100 ns units.</summary>
        public void WriteFrame(IntPtr pixels, long time, long duration)
        {
            int size = stride * height;
            IMFMediaBuffer buf;
            Check(MF.MFCreateMemoryBuffer(size, out buf));
            try
            {
                IntPtr dst; int max, cur;
                Check(buf.Lock(out dst, out max, out cur));
                MF.CopyMemory(dst, pixels, (UIntPtr)(uint)size);
                buf.Unlock();
                buf.SetCurrentLength(size);
                Write(videoStream, buf, time, duration);
            }
            finally { Marshal.ReleaseComObject(buf); }
        }

        /// <summary>Interleaved 16-bit stereo samples (count = samples per channel).</summary>
        public void WriteAudio(short[] samples, int count, long time)
        {
            if (audioStream < 0 || count <= 0) return;
            int bytes = count * AudioChannels * 2;
            IMFMediaBuffer buf;
            Check(MF.MFCreateMemoryBuffer(bytes, out buf));
            try
            {
                IntPtr dst; int max, cur;
                Check(buf.Lock(out dst, out max, out cur));
                Marshal.Copy(samples, 0, dst, count * AudioChannels);
                buf.Unlock();
                buf.SetCurrentLength(bytes);
                Write(audioStream, buf, time, count * 10000000L / AudioRate);
            }
            finally { Marshal.ReleaseComObject(buf); }
        }

        void Write(int stream, IMFMediaBuffer buf, long time, long duration)
        {
            IMFSample s;
            Check(MF.MFCreateSample(out s));
            try
            {
                s.AddBuffer(buf);
                s.SetSampleTime(time);
                s.SetSampleDuration(duration);
                lock (this) Check(writer.WriteSample(stream, s));
            }
            finally { Marshal.ReleaseComObject(s); }
        }

        /// <summary>Writes the end of the file; without it the MP4 does not play.</summary>
        public void Finish()
        {
            if (writer == null) return;
            lock (this) Check(writer.Finalize_());
        }

        public void Dispose()
        {
            if (frameBuf != null) { Marshal.ReleaseComObject(frameBuf); frameBuf = null; }
            if (writer != null) { Marshal.ReleaseComObject(writer); writer = null; }
        }

        static void Check(int hr)
        {
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
        }
    }

    static class MF
    {
        [DllImport("mfplat.dll")] public static extern int MFStartup(int version, int flags);
        [DllImport("mfplat.dll")] public static extern int MFCreateAttributes(out IMFAttributes attrs, int size);
        [DllImport("mfplat.dll")] public static extern int MFCreateMediaType(out IMFMediaType type);
        [DllImport("mfplat.dll")] public static extern int MFCreateSample(out IMFSample sample);
        [DllImport("mfplat.dll")] public static extern int MFCreateMemoryBuffer(int max, out IMFMediaBuffer buf);
        [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode)] public static extern int MFCreateSinkWriterFromURL(string url, IntPtr byteStream, IMFAttributes attrs, out IMFSinkWriter writer);
        [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory")] public static extern void CopyMemory(IntPtr dst, IntPtr src, UIntPtr len);

        public static readonly Guid MT_MAJOR_TYPE = new Guid("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
        public static readonly Guid MT_SUBTYPE = new Guid("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
        public static readonly Guid MT_AVG_BITRATE = new Guid("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
        public static readonly Guid MT_INTERLACE_MODE = new Guid("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
        public static readonly Guid MT_FRAME_SIZE = new Guid("1652c33d-d6b2-4012-b834-72030849a37d");
        public static readonly Guid MT_FRAME_RATE = new Guid("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
        public static readonly Guid MT_PIXEL_ASPECT_RATIO = new Guid("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
        public static readonly Guid MT_DEFAULT_STRIDE = new Guid("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
        public static readonly Guid MT_MPEG2_PROFILE = new Guid("ad76a80b-2d5c-4e0b-b375-64e520137036");
        public static readonly Guid MT_AUDIO_NUM_CHANNELS = new Guid("37e48bf5-645e-4c5b-89de-ada9e29b696a");
        public static readonly Guid MT_AUDIO_SAMPLES_PER_SECOND = new Guid("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
        public static readonly Guid MT_AUDIO_BITS_PER_SAMPLE = new Guid("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");
        public static readonly Guid MT_AUDIO_AVG_BYTES_PER_SECOND = new Guid("1aab75c8-cfef-451c-ab95-ac034b8e1731");
        public static readonly Guid MT_AUDIO_BLOCK_ALIGNMENT = new Guid("322de230-9eeb-43bd-ab7a-ff412251541d");
        public static readonly Guid READWRITE_ENABLE_HARDWARE_TRANSFORMS = new Guid("a634a91c-822b-41b9-a494-4de4643612b0");
        public static readonly Guid MediaType_Video = new Guid("73646976-0000-0010-8000-00AA00389B71");
        public static readonly Guid MediaType_Audio = new Guid("73647561-0000-0010-8000-00AA00389B71");
        public static readonly Guid VideoFormat_H264 = new Guid("34363248-0000-0010-8000-00AA00389B71");
        public static readonly Guid VideoFormat_RGB32 = new Guid("00000016-0000-0010-8000-00AA00389B71");
        public static readonly Guid AudioFormat_AAC = new Guid("00001610-0000-0010-8000-00AA00389B71");
        public static readonly Guid AudioFormat_PCM = new Guid("00000001-0000-0010-8000-00AA00389B71");
    }

    // COM interfaces: only the methods Kadr calls have real signatures; the others keep their places in the table.

    [ComImport, Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMFAttributes
    {
        void _0(); void _1(); void _2(); void _3(); void _4(); void _5(); void _6(); void _7(); void _8(); void _9();
        void _10(); void _11(); void _12(); void _13(); void _14(); void _15(); void _16(); void _17();
        [PreserveSig] int SetUINT32([MarshalAs(UnmanagedType.LPStruct)] Guid key, int value);
        [PreserveSig] int SetUINT64([MarshalAs(UnmanagedType.LPStruct)] Guid key, long value);
        void _20();
        [PreserveSig] int SetGUID([MarshalAs(UnmanagedType.LPStruct)] Guid key, [MarshalAs(UnmanagedType.LPStruct)] Guid value);
        void _22(); void _23(); void _24(); void _25(); void _26(); void _27(); void _28(); void _29();
    }

    [ComImport, Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMFMediaType
    {
        void _0(); void _1(); void _2(); void _3(); void _4(); void _5(); void _6(); void _7(); void _8(); void _9();
        void _10(); void _11(); void _12(); void _13(); void _14(); void _15(); void _16(); void _17();
        [PreserveSig] int SetUINT32([MarshalAs(UnmanagedType.LPStruct)] Guid key, int value);
        [PreserveSig] int SetUINT64([MarshalAs(UnmanagedType.LPStruct)] Guid key, long value);
        void _20();
        [PreserveSig] int SetGUID([MarshalAs(UnmanagedType.LPStruct)] Guid key, [MarshalAs(UnmanagedType.LPStruct)] Guid value);
        void _22(); void _23(); void _24(); void _25(); void _26(); void _27(); void _28(); void _29();
    }

    [ComImport, Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMFSample
    {
        void _0(); void _1(); void _2(); void _3(); void _4(); void _5(); void _6(); void _7(); void _8(); void _9();
        void _10(); void _11(); void _12(); void _13(); void _14(); void _15(); void _16(); void _17(); void _18(); void _19();
        void _20(); void _21(); void _22(); void _23(); void _24(); void _25(); void _26(); void _27(); void _28(); void _29();
        void _GetSampleFlags(); void _SetSampleFlags(); void _GetSampleTime();
        [PreserveSig] int SetSampleTime(long time);
        void _GetSampleDuration();
        [PreserveSig] int SetSampleDuration(long duration);
        void _GetBufferCount(); void _GetBufferByIndex(); void _ConvertToContiguousBuffer();
        [PreserveSig] int AddBuffer(IMFMediaBuffer buffer);
    }

    [ComImport, Guid("045FA593-8799-42b8-BC8D-8968C6453507"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMFMediaBuffer
    {
        [PreserveSig] int Lock(out IntPtr buffer, out int maxLength, out int currentLength);
        [PreserveSig] int Unlock();
        [PreserveSig] int GetCurrentLength(out int length);
        [PreserveSig] int SetCurrentLength(int length);
    }

    [ComImport, Guid("3137f1cd-fe5e-4805-a5d8-fb477448cb3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMFSinkWriter
    {
        [PreserveSig] int AddStream(IMFMediaType type, out int index);
        [PreserveSig] int SetInputMediaType(int index, IMFMediaType type, IMFAttributes encodingParams);
        [PreserveSig] int BeginWriting();
        [PreserveSig] int WriteSample(int index, IMFSample sample);
        [PreserveSig] int SendStreamTick(int index, long time);
        void _PlaceMarker(); void _NotifyEndOfSegment(); void _Flush();
        [PreserveSig] int Finalize_();
    }
}
