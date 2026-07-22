using System;
using System.Runtime.InteropServices;
using HDRGammaController.Interop;

namespace HDRGammaController
{
    /// <summary>
    /// True HDR-range patch emitter: a bare Win32 popup window backed by a D3D11 flip-model
    /// swapchain in scRGB FP16 (R16G16B16A16_FLOAT, colorspace RGB_FULL_G10_NONE_P709).
    ///
    /// WHY: WPF/GDI windows render into 8-bit sRGB surfaces whose brightest representable
    /// pixel IS SDR white - the compositor maps (255,255,255) to the SDR-content-brightness
    /// level and nothing brighter can be encoded. In scRGB FP16, 1.0 = 80 nits and values
    /// above 1.0 are legal: a 1000-nit patch is simply the float (12.5, 12.5, 12.5). The
    /// compositor converts to the wire PQ itself - the same path real HDR content takes.
    ///
    /// Implementation is hand-rolled COM vtable interop (matching this repo's Dxgi.cs
    /// style, no external packages): D3D11CreateDevice + CreateSwapChainForHwnd +
    /// SetColorSpace1, then per patch: GetBuffer → CreateRenderTargetView →
    /// ClearRenderTargetView → Present. Flip-model presents persist on screen, so one
    /// present per color suffices - no render loop.
    ///
    /// See docs/hdr-patch-renderer-design.md. Validate with a probe via the report's
    /// "Validate HDR patch renderer" tool BEFORE trusting measurements from this path.
    /// </summary>
    public sealed class HdrPatchRenderer : IDisposable
    {
        private const int DXGI_FORMAT_R16G16B16A16_FLOAT = 10;
        private const int DXGI_SWAP_EFFECT_FLIP_DISCARD = 4;
        private const int DXGI_COLOR_SPACE_RGB_FULL_G10_NONE_P709 = 1;
        private const uint DXGI_USAGE_RENDER_TARGET_OUTPUT = 0x20;

        private static Guid IID_IDXGIFactory2 = new("50c83a1c-e072-4c48-87b0-3630fa36a6d0");
        private static Guid IID_IDXGISwapChain3 = new("94d99bdb-f1f8-4ab0-b236-7da0170edab1");
        private static Guid IID_ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

        #region Win32 window

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WNDCLASSEX
        {
            public int cbSize;
            public uint style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            public string? lpszMenuName;
            public string lpszClassName;
            public IntPtr hIconSm;
        }

        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateWindowEx(uint exStyle, string className, string windowName,
            uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
        [DllImport("user32.dll")]
        private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")]
        private static extern bool DestroyWindow(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int cmd);
        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string? name);

        private const uint WS_POPUP = 0x80000000;
        private const uint WS_EX_TOPMOST = 0x8;
        private const uint WS_EX_TOOLWINDOW = 0x80;
        private const uint WS_EX_NOACTIVATE = 0x08000000;
        private const int SW_SHOWNOACTIVATE = 4;
        private static readonly IntPtr HWND_TOPMOST = new(-1);
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_SHOWWINDOW = 0x0040;

        // Keep the delegate alive for the window's lifetime (GC must not collect the thunk).
        private static readonly WndProcDelegate StaticWndProc = (h, m, w, l) => DefWindowProc(h, m, w, l);
        private static ushort _classAtom;

        #endregion

        #region D3D11 / DXGI interop

        [DllImport("d3d11.dll")]
        private static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software,
            uint flags, IntPtr featureLevels, uint numFeatureLevels, uint sdkVersion,
            out IntPtr device, out int featureLevel, out IntPtr context);

        [StructLayout(LayoutKind.Sequential)]
        private struct DXGI_SWAP_CHAIN_DESC1
        {
            public uint Width, Height;
            public int Format;
            public int Stereo;
            public uint SampleCount, SampleQuality;
            public uint BufferUsage;
            public uint BufferCount;
            public int Scaling;
            public int SwapEffect;
            public int AlphaMode;
            public uint Flags;
        }

        // vtable slot indices from the C++ headers - the load-bearing constants of this file.
        private const int Slot_Factory2_CreateSwapChainForHwnd = 15;
        private const int Slot_SwapChain_Present = 8;
        private const int Slot_SwapChain_GetBuffer = 9;
        private const int Slot_SwapChain3_CheckColorSpaceSupport = 37;
        private const int Slot_SwapChain3_SetColorSpace1 = 38;
        private const int Slot_Device_CreateRenderTargetView = 9;
        private const int Slot_Context_ClearRenderTargetView = 50;

        private delegate int CreateSwapChainForHwndD(IntPtr factory, IntPtr device, IntPtr hwnd,
            ref DXGI_SWAP_CHAIN_DESC1 desc, IntPtr fullscreenDesc, IntPtr restrictToOutput, out IntPtr swapChain);
        private delegate int PresentD(IntPtr swapChain, uint syncInterval, uint flags);
        private delegate int GetBufferD(IntPtr swapChain, uint buffer, ref Guid iid, out IntPtr surface);
        private delegate int CheckColorSpaceSupportD(IntPtr swapChain, int colorSpace, out uint support);
        private delegate int SetColorSpace1D(IntPtr swapChain, int colorSpace);
        private delegate int CreateRenderTargetViewD(IntPtr device, IntPtr resource, IntPtr desc, out IntPtr rtv);
        private delegate void ClearRenderTargetViewD(IntPtr context, IntPtr rtv, float[] rgba);

        private static T VtblCall<T>(IntPtr comObject, int slot) where T : Delegate
        {
            IntPtr vtbl = Marshal.ReadIntPtr(comObject);
            IntPtr fn = Marshal.ReadIntPtr(vtbl, slot * IntPtr.Size);
            return (T)Marshal.GetDelegateForFunctionPointer(fn, typeof(T));
        }

        #endregion

        private IntPtr _hwnd;
        private IntPtr _device;
        private IntPtr _context;
        private IntPtr _swapChain;   // IDXGISwapChain3
        private bool _disposed;

        /// <summary>True when the swapchain reports support for the scRGB colorspace.</summary>
        public bool ScRgbSupported { get; private set; }

        /// <summary>
        /// Creates the window (pixel-exact, topmost) and the FP16 swapchain on it.
        /// Throws with a descriptive message on any failure - callers treat that as
        /// "HDR-range patches unavailable".
        /// </summary>
        public HdrPatchRenderer(int x, int y, int width, int height)
        {
            try
            {
                CreateNativeWindow(x, y, width, height);
                CreateDeviceAndSwapChain((uint)width, (uint)height);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        private void CreateNativeWindow(int x, int y, int width, int height)
        {
            IntPtr instance = GetModuleHandle(null);
            if (_classAtom == 0)
            {
                var wc = new WNDCLASSEX
                {
                    cbSize = Marshal.SizeOf<WNDCLASSEX>(),
                    lpfnWndProc = Marshal.GetFunctionPointerForDelegate(StaticWndProc),
                    hInstance = instance,
                    lpszClassName = "HdrPatchRendererWindow",
                };
                _classAtom = RegisterClassEx(ref wc);
                if (_classAtom == 0)
                    throw new InvalidOperationException($"RegisterClassEx failed ({Marshal.GetLastWin32Error()}).");
            }

            _hwnd = CreateWindowEx(WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
                "HdrPatchRendererWindow", "HDR Patch", WS_POPUP, x, y, width, height,
                IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero)
                throw new InvalidOperationException($"CreateWindowEx failed ({Marshal.GetLastWin32Error()}).");

            // Hovering a taskbar icon mid-measurement triggers Aero Peek, which fades every
            // non-excluded window to glass - the probe would read black instead of the patch.
            Services.WindowTheme.ExcludeFromPeek(_hwnd);

            ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
            SetWindowPos(_hwnd, HWND_TOPMOST, x, y, width, height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }

        private void CreateDeviceAndSwapChain(uint width, uint height)
        {
            int hr = D3D11CreateDevice(IntPtr.Zero, /*HARDWARE*/ 1, IntPtr.Zero, 0,
                IntPtr.Zero, 0, /*D3D11_SDK_VERSION*/ 7, out _device, out _, out _context);
            if (hr < 0) throw new InvalidOperationException($"D3D11CreateDevice failed (0x{hr:X8}).");

            // Factory via the existing Dxgi P/Invoke, then QI to IDXGIFactory2.
            var iidFactory1 = Dxgi.IID_IDXGIFactory1;
            hr = Dxgi.CreateDXGIFactory1(ref iidFactory1, out IntPtr factory1);
            if (hr < 0) throw new InvalidOperationException($"CreateDXGIFactory1 failed (0x{hr:X8}).");
            try
            {
                hr = Marshal.QueryInterface(factory1, ref IID_IDXGIFactory2, out IntPtr factory2);
                if (hr < 0) throw new InvalidOperationException("IDXGIFactory2 unavailable (needs Windows 8+).");
                try
                {
                    var desc = new DXGI_SWAP_CHAIN_DESC1
                    {
                        Width = width,
                        Height = height,
                        Format = DXGI_FORMAT_R16G16B16A16_FLOAT,
                        SampleCount = 1,
                        BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT,
                        BufferCount = 2,
                        SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD, // FP16 requires flip model
                    };
                    var create = VtblCall<CreateSwapChainForHwndD>(factory2, Slot_Factory2_CreateSwapChainForHwnd);
                    hr = create(factory2, _device, _hwnd, ref desc, IntPtr.Zero, IntPtr.Zero, out IntPtr swapChain1);
                    if (hr < 0) throw new InvalidOperationException($"CreateSwapChainForHwnd FP16 failed (0x{hr:X8}).");

                    hr = Marshal.QueryInterface(swapChain1, ref IID_IDXGISwapChain3, out _swapChain);
                    Marshal.Release(swapChain1);
                    if (hr < 0) throw new InvalidOperationException("IDXGISwapChain3 unavailable (needs Windows 10+).");

                    var check = VtblCall<CheckColorSpaceSupportD>(_swapChain, Slot_SwapChain3_CheckColorSpaceSupport);
                    check(_swapChain, DXGI_COLOR_SPACE_RGB_FULL_G10_NONE_P709, out uint support);
                    ScRgbSupported = (support & 0x1) != 0; // DXGI_SWAP_CHAIN_COLOR_SPACE_SUPPORT_FLAG_PRESENT

                    var setCs = VtblCall<SetColorSpace1D>(_swapChain, Slot_SwapChain3_SetColorSpace1);
                    hr = setCs(_swapChain, DXGI_COLOR_SPACE_RGB_FULL_G10_NONE_P709);
                    if (hr < 0) throw new InvalidOperationException($"SetColorSpace1(scRGB) failed (0x{hr:X8}).");
                }
                finally
                {
                    Marshal.Release(factory2);
                }
            }
            finally
            {
                Marshal.Release(factory1);
            }
        }

        /// <summary>
        /// Presents a solid patch at the given ABSOLUTE luminance per channel. scRGB:
        /// value = nits / 80. The compositor performs the wire conversion; the installed
        /// MHC2 profile applies at scanout exactly as for any other HDR content.
        /// </summary>
        public void PresentNits(double nitsR, double nitsG, double nitsB)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(HdrPatchRenderer));

            var getBuffer = VtblCall<GetBufferD>(_swapChain, Slot_SwapChain_GetBuffer);
            int hr = getBuffer(_swapChain, 0, ref IID_ID3D11Texture2D, out IntPtr backBuffer);
            if (hr < 0) throw new InvalidOperationException($"GetBuffer failed (0x{hr:X8}).");
            IntPtr rtv = IntPtr.Zero;
            try
            {
                var createRtv = VtblCall<CreateRenderTargetViewD>(_device, Slot_Device_CreateRenderTargetView);
                hr = createRtv(_device, backBuffer, IntPtr.Zero, out rtv);
                if (hr < 0) throw new InvalidOperationException($"CreateRenderTargetView failed (0x{hr:X8}).");

                var clear = VtblCall<ClearRenderTargetViewD>(_context, Slot_Context_ClearRenderTargetView);
                clear(_context, rtv, new[]
                {
                    (float)(nitsR / 80.0), (float)(nitsG / 80.0), (float)(nitsB / 80.0), 1.0f,
                });

                var present = VtblCall<PresentD>(_swapChain, Slot_SwapChain_Present);
                hr = present(_swapChain, 1, 0);
                if (hr < 0) throw new InvalidOperationException($"Present failed (0x{hr:X8}).");
            }
            finally
            {
                if (rtv != IntPtr.Zero) Marshal.Release(rtv);
                Marshal.Release(backBuffer);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_swapChain != IntPtr.Zero) { Marshal.Release(_swapChain); _swapChain = IntPtr.Zero; }
            if (_context != IntPtr.Zero) { Marshal.Release(_context); _context = IntPtr.Zero; }
            if (_device != IntPtr.Zero) { Marshal.Release(_device); _device = IntPtr.Zero; }
            if (_hwnd != IntPtr.Zero) { DestroyWindow(_hwnd); _hwnd = IntPtr.Zero; }
        }
    }
}
