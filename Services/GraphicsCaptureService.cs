using DeepSeek_v4_for_VisualStudio.Utils;
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    /// <summary>
    /// 基于官方 Windows Graphics Capture (WGC) 的窗口截图服务。
    ///
    /// 通过 <c>IGraphicsCaptureItemInterop::CreateForWindow</c> 按 HWND 创建捕获项，
    /// 由桌面合成器直接提供窗口的 GPU 合成表面——因此既能抓 GPU 加速窗口
    /// （Chrome/Edge/WebView2/QQ 等），也能抓后台/被遮挡/最小化的窗口，无需把窗口置前。
    ///
    /// 取帧读回走纯 WinRT 的 <c>SoftwareBitmap.CreateCopyFromSurfaceAsync</c>，
    /// 不依赖 D3D11 staging/CopyResource/Map 等裸 COM 互操作。
    /// </summary>
    public static class GraphicsCaptureService
    {
        private const int D3D11SdkVersion = 7;
        private const uint D3D11CreateDeviceBgraSupport = 0x20;
        private const int DrvHardware = 1;
        private const int DrvWarp = 5;

        /// <summary>探测并缓存当前环境是否支持 WGC 窗口捕获（IGraphicsCaptureItemInterop）。</summary>
        public static bool IsWindowCaptureSupported() => CaptureItemInterop.IsSupported;

        /// <summary>把目标窗口捕获为一张 BGRA 位图（原始尺寸，未缩放）。</summary>
        public static async Task<Bitmap> CaptureWindowAsync(IntPtr hwnd, CancellationToken ct)
        {
            if (hwnd == IntPtr.Zero)
                throw new ArgumentException("hwnd 为空", nameof(hwnd));

            IntPtr? d3dDevice = null;
            IntPtr? d3dContext = null;
            IntPtr? dxgiDevice = null;
            GraphicsCaptureItem? item = null;
            Direct3D11CaptureFramePool? pool = null;
            GraphicsCaptureSession? session = null;

            try
            {
                if (!TryCreateD3D11Device(out IntPtr dev, out IntPtr ctx))
                    throw new InvalidOperationException("无法创建 D3D11 设备 (D3D11CreateDevice 失败)");
                d3dDevice = dev;
                d3dContext = ctx;

                Guid iidDxgiDevice = new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
                int hr = Marshal.QueryInterface(dev, ref iidDxgiDevice, out IntPtr dxgiDev);
                if (hr != 0 || dxgiDev == IntPtr.Zero)
                    throw new InvalidOperationException($"无法从 D3D11 设备获取 IDXGIDevice (HRESULT=0x{hr:X8})");
                dxgiDevice = dxgiDev;

                hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDev, out IntPtr inspectable3D);
                if (hr != 0 || inspectable3D == IntPtr.Zero)
                    throw new InvalidOperationException($"CreateDirect3D11DeviceFromDXGIDevice 失败 (HRESULT=0x{hr:X8})");
                IDirect3DDevice device = (IDirect3DDevice)Marshal.GetObjectForIUnknown(inspectable3D);
                Marshal.Release(inspectable3D);

                item = CaptureItemInterop.CreateForWindow(hwnd);
                Logger.Info("[WGC] CreateForWindow 成功");

                SizeInt32 size = item.Size;
                int w = checked((int)size.Width);
                int h = checked((int)size.Height);
                if (w <= 0 || h <= 0)
                    throw new InvalidOperationException($"捕获项尺寸无效 ({w}x{h})");
                Logger.Info($"[WGC] 捕获项尺寸 = {w}x{h}");

                pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                    device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, size);
                Logger.Info("[WGC] Direct3D11CaptureFramePool 已创建");

                session = pool.CreateCaptureSession(item);
                session.StartCapture();
                Logger.Info("[WGC] StartCapture 已启动，等待首帧...");

                // TryGetNextFrame 在无帧时可能阻塞，放到独立线程轮询；外层 WhenAny + 3s 硬超时。
                var frameTask = Task.Run<Direct3D11CaptureFrame?>(() =>
                {
                    while (!ct.IsCancellationRequested)
                    {
                        try
                        {
                            var f = pool.TryGetNextFrame();
                            if (f != null) return f;
                        }
                        catch
                        {
                            // 池可能已被释放（超时路径）或其它异常 → 退出轮询，避免未观察异常。
                            break;
                        }
                        Thread.Sleep(30);
                    }
                    return null;
                }, ct);

                var timeoutTask = Task.Delay(3000, ct);
                var done = await Task.WhenAny(frameTask, timeoutTask).ConfigureAwait(false);
                if (done == timeoutTask)
                    throw new TimeoutException("等待 WGC 首帧超时（3s）");

                var frame = (await frameTask.ConfigureAwait(false))!;
                try
                {
                    Logger.Info("[WGC] 取得首帧，开始 SoftwareBitmap 读回...");
                    return await ReadFrameToBitmapAsync(frame).ConfigureAwait(false);
                }
                finally
                {
                    try { frame.Dispose(); } catch { }
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException("等待 WGC 首帧超时（3s）", null!);
            }
            finally
            {
                try { session?.Dispose(); } catch { }
                try { pool?.Dispose(); } catch { }
                if (dxgiDevice.HasValue && dxgiDevice.Value != IntPtr.Zero) Marshal.Release(dxgiDevice.Value);
                if (d3dContext.HasValue && d3dContext.Value != IntPtr.Zero) Marshal.Release(d3dContext.Value);
                if (d3dDevice.HasValue && d3dDevice.Value != IntPtr.Zero) Marshal.Release(d3dDevice.Value);
            }
        }

        /// <summary>纯 WinRT 读回：IDirect3DSurface → SoftwareBitmap → BGRA 字节 → System.Drawing 位图。</summary>
        private static async Task<Bitmap> ReadFrameToBitmapAsync(Direct3D11CaptureFrame frame)
        {
            IDirect3DSurface surface = frame.Surface;
            Logger.Info("[WGC] 已获取 frame.Surface");

            using (var sb = await SoftwareBitmap.CreateCopyFromSurfaceAsync(surface))
            {
                int pw = sb.PixelWidth;
                int ph = sb.PixelHeight;
                Logger.Info($"[WGC] SoftwareBitmap: {pw}x{ph}, format={sb.BitmapPixelFormat}");

                var buffer = new byte[pw * ph * 4];
                sb.CopyToBuffer(buffer.AsBuffer());

                return BuildBitmapFromBuffer(buffer, pw, ph);
            }
        }

        private static Bitmap BuildBitmapFromBuffer(byte[] buffer, int w, int h)
        {
            var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                // SoftwareBitmap(CopyFromSurfaceAsync) 为 Bgra8，与 Format32bppArgb 内存字节序一致，直接整体拷入。
                Marshal.Copy(buffer, 0, data.Scan0, buffer.Length);
            }
            finally
            {
                bmp.UnlockBits(data);
            }
            return bmp;
        }

        private static bool TryCreateD3D11Device(out IntPtr device, out IntPtr context)
        {
            int hr = D3D11CreateDevice(
                IntPtr.Zero, DrvHardware, IntPtr.Zero, D3D11CreateDeviceBgraSupport,
                IntPtr.Zero, 0, D3D11SdkVersion,
                out device, out _, out context);

            if (hr == 0 && device != IntPtr.Zero)
                return true;

            // 硬件设备失败则回退到 WARP 软件光栅化
            LogWarn($"D3D11 硬件设备创建失败 (HRESULT=0x{hr:X8})，回退 WARP");
            int hr2 = D3D11CreateDevice(
                IntPtr.Zero, DrvWarp, IntPtr.Zero, D3D11CreateDeviceBgraSupport,
                IntPtr.Zero, 0, D3D11SdkVersion,
                out device, out _, out context);
            return hr2 == 0 && device != IntPtr.Zero;
        }

        private static void LogWarn(string msg) => Logger.Warn($"[WGC] {msg}");

        // ── 互操作接口与原生函数 ──

        /// <summary>windows.graphics.capture.interop.h 中的 IGraphicsCaptureItemInterop。</summary>
        [ComImport, Guid("3628e81b-3cac-4c60-b7f4-23ce0e0c3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IGraphicsCaptureItemInterop
        {
            [PreserveSig]
            int CreateForWindow(IntPtr hwnd, ref Guid riid, out IntPtr result);
        }

        /// <summary>通过激活工厂把 HWND 包装为 GraphicsCaptureItem。</summary>
        private static class CaptureItemInterop
        {
            private const string GraphicsCaptureItemClassName =
                "Windows.Graphics.Capture.GraphicsCaptureItem";

            private static readonly Guid IActivationFactoryIid =
                new Guid("00000035-0000-0000-C000-000000000046");

            private static readonly Guid IGraphicsCaptureItemInteropIid =
                new Guid("3628e81b-3cac-4c60-b7f4-23ce0e0c3356");

            private static readonly Guid GraphicsCaptureItemIid =
                new Guid("79c3f95b-31f7-4ec2-a464-632ef5d30760");

            private static int _isSupported = 0; // 0=未知, 1=支持, -1=不支持

            public static bool IsSupported
            {
                get
                {
                    if (_isSupported == 0)
                    {
                        _isSupported = Probe() ? 1 : -1;
                        Logger.Info($"[WGC] 窗口捕获能力探测: {(_isSupported > 0 ? "支持" : "不支持")}");
                    }
                    return _isSupported > 0;
                }
            }

            /// <summary>
            /// 探测当前环境是否提供 IGraphicsCaptureItemInterop（WGC 按窗口捕获）。
            /// 只做一次，结果缓存，避免每次截图都撞一次 E_NOINTERFACE。
            /// </summary>
            private static bool Probe()
            {
                try
                {
                    int hr = WindowsCreateString(GraphicsCaptureItemClassName, GraphicsCaptureItemClassName.Length, out IntPtr hstring);
                    if (hr < 0 || hstring == IntPtr.Zero) return false;
                    try
                    {
                        Guid interopIid = IGraphicsCaptureItemInteropIid;
                        hr = RoGetActivationFactory(hstring, ref interopIid, out IntPtr factory);
                        if (factory != IntPtr.Zero)
                        {
                            Marshal.Release(factory);
                            return hr == 0;
                        }
                        return false;
                    }
                    finally
                    {
                        WindowsDeleteString(hstring);
                    }
                }
                catch
                {
                    return false;
                }
            }

            public static GraphicsCaptureItem CreateForWindow(IntPtr hwnd)
            {
                int hr = WindowsCreateString(GraphicsCaptureItemClassName, GraphicsCaptureItemClassName.Length, out IntPtr hstring);
                if (hr < 0 || hstring == IntPtr.Zero)
                    throw new InvalidOperationException($"WindowsCreateString 失败 (HRESULT=0x{hr:X8})");

                try
                {
                    IntPtr factoryPtr = IntPtr.Zero;

                    // 路径 1：直接以 IID_IGraphicsCaptureItemInterop 请求激活工厂。
                    Guid interopIid = IGraphicsCaptureItemInteropIid;
                    int hrInterop = RoGetActivationFactory(hstring, ref interopIid, out factoryPtr);
                    Logger.Info($"[WGC] RoGetActivationFactory(IID_IGraphicsCaptureItemInterop) = 0x{hrInterop:X8}");

                    if (hrInterop != 0 || factoryPtr == IntPtr.Zero)
                    {
                        // 路径 2：以 IID_IActivationFactory 请求工厂，再 QI 到 interop。
                        Guid afIid = IActivationFactoryIid;
                        int hrAf = RoGetActivationFactory(hstring, ref afIid, out factoryPtr);
                        Logger.Info($"[WGC] RoGetActivationFactory(IID_IActivationFactory) = 0x{hrAf:X8}");
                        if (hrAf != 0 || factoryPtr == IntPtr.Zero)
                            throw new InvalidOperationException(
                                $"RoGetActivationFactory 失败 (interop=0x{hrInterop:X8}, af=0x{hrAf:X8})");
                    }

                    try
                    {
                        var interop = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factoryPtr);

                        Guid itemIid = GraphicsCaptureItemIid;
                        hr = interop.CreateForWindow(hwnd, ref itemIid, out IntPtr raw);
                        if (hr != 0 || raw == IntPtr.Zero)
                            throw new InvalidOperationException($"CreateForWindow 失败 (HRESULT=0x{hr:X8})");

                        try
                        {
                            return (GraphicsCaptureItem)Marshal.GetObjectForIUnknown(raw);
                        }
                        finally
                        {
                            Marshal.Release(raw);
                        }
                    }
                    finally
                    {
                        Marshal.Release(factoryPtr);
                    }
                }
                finally
                {
                    WindowsDeleteString(hstring);
                }
            }
        }

        [DllImport("d3d11.dll", SetLastError = true)]
        private static extern int D3D11CreateDevice(
            IntPtr pAdapter, int driverType, IntPtr software, uint flags,
            IntPtr pFeatureLevels, uint featureLevels, uint sdkVersion,
            out IntPtr ppDevice, out int pFeatureLevel, out IntPtr ppImmediateContext);

        [DllImport("d3d11.dll", SetLastError = true)]
        private static extern int CreateDirect3D11DeviceFromDXGIDevice(
            IntPtr dxgiDevice, out IntPtr graphicsDevice);

        [DllImport("combase.dll", ExactSpelling = true, PreserveSig = true)]
        private static extern int WindowsCreateString(
            [MarshalAs(UnmanagedType.LPWStr)] string sourceString,
            int length,
            out IntPtr hstring);

        [DllImport("combase.dll", ExactSpelling = true, PreserveSig = true)]
        private static extern int WindowsDeleteString(IntPtr hstring);

        [DllImport("combase.dll", ExactSpelling = true, PreserveSig = true)]
        private static extern int RoGetActivationFactory(
            IntPtr activatableClassId,
            ref Guid iid,
            out IntPtr factory);
    }
}