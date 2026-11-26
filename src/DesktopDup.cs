using System;
using System.Drawing;
using System.Runtime.InteropServices;

namespace Kadr
{
    /// <summary>
    /// Fast screen capture for videos through DXGI Desktop Duplication: the video card hands over the desktop, and
    /// only when something on it changed. Several times cheaper than GDI's BitBlt (which reads the whole screen back
    /// every frame). Works when the area lies on one monitor that is not rotated; otherwise the recorder uses BitBlt.
    /// The mouse pointer is not in these pictures; the recorder draws it itself, as it does with BitBlt.
    /// </summary>
    sealed class DesktopDup : IDisposable
    {
        IntPtr device, context, dup, staging;
        readonly Rectangle area;       // screen pixels
        readonly int boxX, boxY;       // the area inside the monitor's picture
        bool haveFrame;

        DesktopDup(Rectangle area, int boxX, int boxY) { this.area = area; this.boxX = boxX; this.boxY = boxY; }

        /// <summary>A duplication of the monitor that holds the whole area, or null (then use BitBlt).</summary>
        public static DesktopDup TryCreate(Rectangle area)
        {
            IntPtr factory = IntPtr.Zero;
            try
            {
                Guid iidFactory = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");   // IDXGIFactory1
                if (CreateDXGIFactory1(ref iidFactory, out factory) < 0) return null;
                for (uint a = 0; ; a++)
                {
                    IntPtr adapter;
                    if (Call<EnumDelegate>(factory, 12)(factory, a, out adapter) < 0) break;   // EnumAdapters1
                    try
                    {
                        for (uint o = 0; ; o++)
                        {
                            IntPtr output;
                            if (Call<EnumDelegate>(adapter, 7)(adapter, o, out output) < 0) break;   // EnumOutputs
                            try
                            {
                                var desc = new DXGI_OUTPUT_DESC();
                                if (Call<GetDescDelegate>(output, 7)(output, ref desc) < 0) continue;
                                var mon = Rectangle.FromLTRB(desc.Left, desc.Top, desc.Right, desc.Bottom);
                                if (!desc.AttachedToDesktop || !mon.Contains(area)) continue;
                                if (desc.Rotation > 1) return null;   // rotated monitor: BitBlt handles it
                                var d = new DesktopDup(area, area.X - mon.X, area.Y - mon.Y);
                                if (d.Open(adapter, output) && d.Prime()) return d;
                                d.Dispose();
                                return null;
                            }
                            finally { Marshal.Release(output); }
                        }
                    }
                    finally { Marshal.Release(adapter); }
                }
                return null;
            }
            catch { return null; }
            finally { if (factory != IntPtr.Zero) Marshal.Release(factory); }
        }

        bool Open(IntPtr adapter, IntPtr output)
        {
            int level;
            // driver type UNKNOWN because the adapter is given; BGRA support for desktop formats
            if (D3D11CreateDevice(adapter, 0, IntPtr.Zero, 0x20, IntPtr.Zero, 0, 7, out device, out level, out context) < 0) return false;
            Guid iidOutput1 = new Guid("00cddea8-939b-4b83-a340-a685226666cc");
            IntPtr output1;
            if (Marshal.QueryInterface(output, ref iidOutput1, out output1) < 0) return false;
            try
            {
                if (Call<DuplicateDelegate>(output1, 22)(output1, device, out dup) < 0) return false;   // DuplicateOutput
            }
            finally { Marshal.Release(output1); }
            var td = new D3D11_TEXTURE2D_DESC
            {
                Width = (uint)area.Width, Height = (uint)area.Height, MipLevels = 1, ArraySize = 1, Format = 87,   // B8G8R8A8_UNORM
                SampleCount = 1, Usage = 3, CPUAccessFlags = 0x20000                                              // staging, CPU read
            };
            return Call<CreateTextureDelegate>(device, 5)(device, ref td, IntPtr.Zero, out staging) >= 0;
        }

        IntPtr firstFrame;

        /// <summary>The first picture (the whole desktop as it is now); no picture within a second = do not use this.</summary>
        bool Prime()
        {
            firstFrame = Marshal.AllocHGlobal(area.Width * area.Height * 4);
            for (int i = 0; i < 10 && !haveFrame; i++)
            {
                bool lost;
                Grab(firstFrame, area.Width * 4, out lost, 100);
                if (lost) return false;
            }
            return haveFrame;
        }

        /// <summary>Copies the first picture (from Prime) into dst.</summary>
        public void CopyFirst(IntPtr dst, int stride)
        {
            int row = area.Width * 4;
            for (int y = 0; y < area.Height; y++)
                MF.CopyMemory(dst + y * stride, firstFrame + y * row, (UIntPtr)(uint)row);
        }

        /// <summary>
        /// Copies the newest desktop picture of the area into dst (top-down BGRA, stride bytes per row).
        /// False = nothing changed since the last call (dst keeps the last picture), or the duplication was lost.
        /// </summary>
        public bool Grab(IntPtr dst, int stride, out bool lost, uint timeoutMs = 0)
        {
            lost = false;
            var info = new byte[64];
            IntPtr resource;
            int hr = Call<AcquireDelegate>(dup, 8)(dup, timeoutMs, info, out resource);
            if (hr == unchecked((int)0x887A0027)) return false;              // DXGI_ERROR_WAIT_TIMEOUT: no change
            if (hr < 0) { lost = true; return false; }                      // ACCESS_LOST (mode change, secure desktop…)
            try
            {
                long presented = BitConverter.ToInt64(info, 0);
                if (presented == 0 && haveFrame) return false;              // only the pointer moved: the picture is the same
                Guid iidTex = new Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
                IntPtr tex;
                if (Marshal.QueryInterface(resource, ref iidTex, out tex) < 0) return false;
                try
                {
                    var box = new D3D11_BOX { left = (uint)boxX, top = (uint)boxY, front = 0, right = (uint)(boxX + area.Width), bottom = (uint)(boxY + area.Height), back = 1 };
                    Call<CopyRegionDelegate>(context, 46)(context, staging, 0, 0, 0, 0, tex, 0, ref box);
                }
                finally { Marshal.Release(tex); }
            }
            finally
            {
                Marshal.Release(resource);
                Call<ReleaseFrameDelegate>(dup, 14)(dup);
            }
            var map = new D3D11_MAPPED_SUBRESOURCE();
            if (Call<MapDelegate>(context, 14)(context, staging, 0, 1, 0, out map) < 0) return false;
            try
            {
                int row = area.Width * 4;
                for (int y = 0; y < area.Height; y++)
                    MF.CopyMemory(dst + y * stride, map.pData + y * (int)map.RowPitch, (UIntPtr)(uint)row);
            }
            finally { Call<UnmapDelegate>(context, 15)(context, staging, 0); }
            haveFrame = true;
            return true;
        }

        public void Dispose()
        {
            if (firstFrame != IntPtr.Zero) { Marshal.FreeHGlobal(firstFrame); firstFrame = IntPtr.Zero; }
            foreach (var p in new[] { staging, dup, context, device })
                if (p != IntPtr.Zero) Marshal.Release(p);
            staging = dup = context = device = IntPtr.Zero;
        }

        // ---------------- COM by vtable slot ----------------

        static T Call<T>(IntPtr obj, int slot) where T : class
        {
            IntPtr vtbl = Marshal.ReadIntPtr(obj);
            return (T)(object)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(vtbl, slot * IntPtr.Size), typeof(T));
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int EnumDelegate(IntPtr self, uint index, out IntPtr result);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetDescDelegate(IntPtr self, ref DXGI_OUTPUT_DESC desc);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int DuplicateDelegate(IntPtr self, IntPtr device, out IntPtr dup);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CreateTextureDelegate(IntPtr self, ref D3D11_TEXTURE2D_DESC desc, IntPtr init, out IntPtr tex);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int AcquireDelegate(IntPtr self, uint timeoutMs, [Out] byte[] frameInfo, out IntPtr resource);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int ReleaseFrameDelegate(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void CopyRegionDelegate(IntPtr self, IntPtr dst, uint dstSub, uint x, uint y, uint z, IntPtr src, uint srcSub, ref D3D11_BOX box);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int MapDelegate(IntPtr self, IntPtr res, uint sub, uint mapType, uint flags, out D3D11_MAPPED_SUBRESOURCE mapped);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void UnmapDelegate(IntPtr self, IntPtr res, uint sub);

        [DllImport("dxgi.dll")] static extern int CreateDXGIFactory1(ref Guid iid, out IntPtr factory);
        [DllImport("d3d11.dll")] static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags, IntPtr levels, uint levelCount, uint sdk, out IntPtr device, out int level, out IntPtr context);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct DXGI_OUTPUT_DESC
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            public int Left, Top, Right, Bottom;
            [MarshalAs(UnmanagedType.Bool)] public bool AttachedToDesktop;
            public int Rotation;
            public IntPtr Monitor;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct D3D11_TEXTURE2D_DESC
        {
            public uint Width, Height, MipLevels, ArraySize, Format, SampleCount, SampleQuality, Usage, BindFlags, CPUAccessFlags, MiscFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct D3D11_BOX { public uint left, top, front, right, bottom, back; }

        [StructLayout(LayoutKind.Sequential)]
        struct D3D11_MAPPED_SUBRESOURCE { public IntPtr pData; public uint RowPitch, DepthPitch; }
    }
}
