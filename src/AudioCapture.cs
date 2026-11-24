using System;
using System.Runtime.InteropServices;

namespace Kadr
{
    /// <summary>
    /// Sound for videos through WASAPI, built into Windows: the computer's sound (what the speakers play, "loopback")
    /// or the microphone. Windows converts either to 48 kHz stereo float, so the two simply add up.
    /// </summary>
    sealed class AudioSource : IDisposable
    {
        const int Rate = Mp4Writer.AudioRate;
        IAudioClient client;
        IAudioCaptureClient cap;
        float[] buf = new float[Rate * 2];   // interleaved stereo, grows if needed
        int start, count;                    // frames
        public readonly bool Loopback;

        AudioSource(bool loopback) { Loopback = loopback; }

        /// <summary>Opens the default device; null when there is none (no microphone, no speakers).</summary>
        public static AudioSource Open(bool loopback)
        {
            var s = new AudioSource(loopback);
            try
            {
                var en = (IMMDeviceEnumerator)new MMDeviceEnumerator();
                IMMDevice dev;
                int hr = en.GetDefaultAudioEndpoint(loopback ? 0 : 1, 0, out dev);
                Marshal.ReleaseComObject(en);
                if (hr < 0 || dev == null) return null;
                object o;
                Check(dev.Activate(typeof(IAudioClient).GUID, 23, IntPtr.Zero, out o));
                Marshal.ReleaseComObject(dev);
                s.client = (IAudioClient)o;
                var fmt = new WAVEFORMATEX { wFormatTag = 3, nChannels = 2, nSamplesPerSec = Rate, wBitsPerSample = 32, nBlockAlign = 8, nAvgBytesPerSec = Rate * 8 };
                int flags = unchecked((int)0x80000000) | 0x08000000 | (loopback ? 0x00020000 : 0);   // convert to our format, good resampler, loopback
                Check(s.client.Initialize(0, flags, 2000000, 0, ref fmt, IntPtr.Zero));                  // shared mode, 200 ms buffer
                object c;
                Check(s.client.GetService(typeof(IAudioCaptureClient).GUID, out c));
                s.cap = (IAudioCaptureClient)c;
                Check(s.client.Start());
                return s;
            }
            catch
            {
                s.Dispose();
                return null;
            }
        }

        /// <summary>Moves everything Windows has captured into the buffer (drop = throw it away, while paused).</summary>
        public void Poll(bool drop)
        {
            if (cap == null) return;
            int frames;
            while (cap.GetNextPacketSize(out frames) >= 0 && frames > 0)
            {
                IntPtr data; int n, flags; long pos, qpc;
                if (cap.GetBuffer(out data, out n, out flags, out pos, out qpc) < 0) return;
                if (!drop && n > 0)
                {
                    Ensure(count + n);
                    int end = start + count;
                    if ((flags & 2) != 0) Array.Clear(buf, end * 2, n * 2);   // "silent" packet
                    else Marshal.Copy(data, buf, end * 2, n * 2);
                    count += n;
                }
                cap.ReleaseBuffer(n);
            }
        }

        void Ensure(int frames)
        {
            if (start > 0 && start + frames > buf.Length / 2)
            {
                Array.Copy(buf, start * 2, buf, 0, count * 2);
                start = 0;
            }
            if (frames > buf.Length / 2) Array.Resize(ref buf, Math.Max(frames, buf.Length / 2 * 2) * 2);
        }

        /// <summary>Adds up to n frames into mix; missing frames stay silent (loopback sends nothing while it is quiet).</summary>
        public void MixInto(float[] mix, int n)
        {
            int take = Math.Min(n, count);
            for (int i = 0; i < take * 2; i++) mix[i] += buf[start * 2 + i];
            start += take;
            count -= take;
            if (count == 0) start = 0;
            // more than 0.25 s waiting means this device runs ahead: drop the surplus to stay in sync with the picture
            int keep = Rate / 4;
            if (count > keep) { start += count - keep; count = keep; }
        }

        public void Dispose()
        {
            if (client != null) { try { client.Stop(); } catch { } }
            if (cap != null) { Marshal.ReleaseComObject(cap); cap = null; }
            if (client != null) { Marshal.ReleaseComObject(client); client = null; }
        }

        static void Check(int hr) { if (hr < 0) Marshal.ThrowExceptionForHR(hr); }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    struct WAVEFORMATEX
    {
        public short wFormatTag, nChannels;
        public int nSamplesPerSec, nAvgBytesPerSec;
        public short nBlockAlign, wBitsPerSample, cbSize;
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class MMDeviceEnumerator { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        void _EnumAudioEndpoints();
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate([MarshalAs(UnmanagedType.LPStruct)] Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity, [In] ref WAVEFORMATEX format, IntPtr sessionGuid);
        [PreserveSig] int GetBufferSize(out int frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out int frames);
        void _IsFormatSupported();
        void _GetMixFormat();
        void _GetDevicePeriod();
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        void _SetEventHandle();
        [PreserveSig] int GetService([MarshalAs(UnmanagedType.LPStruct)] Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out int frames, out int flags, out long devicePosition, out long qpcPosition);
        [PreserveSig] int ReleaseBuffer(int frames);
        [PreserveSig] int GetNextPacketSize(out int frames);
    }
}
