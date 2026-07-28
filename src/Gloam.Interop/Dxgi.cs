using System;
using System.Runtime.InteropServices;

namespace Gloam.Interop
{
    public static class Dxgi
    {
        [DllImport("dxgi.dll")]
        public static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr ppFactory);

        public static readonly Guid IID_IDXGIFactory6 = new Guid("c1b6694f-ff09-44a9-b03c-77f45a836f1c");
        public static readonly Guid IID_IDXGIFactory1 = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");

        [StructLayout(LayoutKind.Sequential)]
        public struct LUID
        {
            public uint LowPart;
            public int HighPart;
            public override string ToString() => $"{HighPart:X8}-{LowPart:X8}";
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct DXGI_ADAPTER_DESC1
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string Description;
            public uint VendorId;
            public uint DeviceId;
            public uint SubSysId;
            public uint Revision;
            public UIntPtr DedicatedVideoMemory;
            public UIntPtr DedicatedSystemMemory;
            public UIntPtr SharedSystemMemory;
            public LUID AdapterLuid;
            public uint Flags;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct DXGI_OUTPUT_DESC1
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string DeviceName;
            public RECT DesktopCoordinates;
            public int AttachedToDesktop;
            public int Rotation;
            public IntPtr Monitor;
            public int BitsPerColor;
            public int ColorSpace;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)]
            public float[] RedPrimary;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)]
            public float[] GreenPrimary;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)]
            public float[] BluePrimary;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)]
            public float[] WhitePoint;
            public float MinLuminance;
            public float MaxLuminance;
            public float MaxFullFrameLuminance;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [Guid("770aae78-f26f-4dba-a829-253c83d1b387")]
        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IDXGIFactory1
        {
            // IDXGIObject
            [PreserveSig] int SetPrivateData(ref Guid Name, uint DataSize, IntPtr pData);
            [PreserveSig] int SetPrivateDataInterface(ref Guid Name, [MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
            [PreserveSig] int GetPrivateData(ref Guid Name, ref uint pDataSize, IntPtr pData);
            [PreserveSig] int GetParent(ref Guid riid, out IntPtr ppParent);
            
            // IDXGIFactory
            [PreserveSig] int EnumAdapters(uint Adapter, out IntPtr ppAdapter);
            [PreserveSig] int MakeWindowAssociation(IntPtr WindowHandle, uint Flags);
            [PreserveSig] int GetWindowAssociation(out IntPtr pWindowHandle);
            [PreserveSig] int CreateSwapChain(IntPtr pDevice, ref IntPtr pDesc, out IntPtr ppSwapChain);
            [PreserveSig] int CreateSoftwareAdapter(IntPtr Module, out IntPtr ppAdapter);
            
            // IDXGIFactory1
            [PreserveSig] int EnumAdapters1(uint Adapter, out IntPtr ppAdapter);
            [PreserveSig] bool IsCurrent();
        }

        [Guid("29038f61-3839-4626-91fd-086879011a05")]
        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IDXGIAdapter1
        {
            // IDXGIObject
            [PreserveSig] int SetPrivateData(ref Guid Name, uint DataSize, IntPtr pData);
            [PreserveSig] int SetPrivateDataInterface(ref Guid Name, [MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
            [PreserveSig] int GetPrivateData(ref Guid Name, ref uint pDataSize, IntPtr pData);
            [PreserveSig] int GetParent(ref Guid riid, out IntPtr ppParent);
            
            // IDXGIAdapter
            [PreserveSig] int EnumOutputs(uint Output, out IntPtr ppOutput);
            [PreserveSig] int GetDesc(out IntPtr pDesc);
            [PreserveSig] int CheckInterfaceSupport(ref Guid InterfaceName, out long pUMDVersion);
            
            // IDXGIAdapter1
            [PreserveSig] int GetDesc1(out DXGI_ADAPTER_DESC1 pDesc);
        }

        [Guid("068346e8-aaec-4b84-add7-137f513f77a1")]
        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IDXGIOutput6 
        {
            // IDXGIObject
            [PreserveSig] int SetPrivateData(ref Guid Name, uint DataSize, IntPtr pData);
            [PreserveSig] int SetPrivateDataInterface(ref Guid Name, [MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
            [PreserveSig] int GetPrivateData(ref Guid Name, ref uint pDataSize, IntPtr pData);
            [PreserveSig] int GetParent(ref Guid riid, out IntPtr ppParent);

            // IDXGIOutput
            [PreserveSig] int GetDesc(out DXGI_OUTPUT_DESC1 pDesc);
            [PreserveSig] int GetDisplayModeList(int EnumFormat, uint Flags, ref uint pNumModes, IntPtr pDesc);
            [PreserveSig] int FindClosestMatchingMode(ref IntPtr pModeToMatch, out IntPtr pClosestMatch, IntPtr pConcernedDevice);
            [PreserveSig] int WaitForVBlank();
            [PreserveSig] int TakeOwnership(IntPtr pDevice, bool Exclusive);
            [PreserveSig] int ReleaseOwnership();
            [PreserveSig] int GetGammaControlCapabilities(out IntPtr pGammaCaps);
            [PreserveSig] int SetGammaControl(IntPtr pArray);
            [PreserveSig] int GetGammaControl(out IntPtr pArray);
            [PreserveSig] int SetDisplaySurface(IntPtr pScanoutSurface);
            [PreserveSig] int GetDisplaySurfaceData(IntPtr pDestination);
            [PreserveSig] int GetFrameStatistics(out IntPtr pStats);
            
             // IDXGIOutput1
            [PreserveSig] int GetDisplayModeList1(int EnumFormat, uint Flags, ref uint pNumModes, IntPtr pDesc);
            [PreserveSig] int FindClosestMatchingMode1(ref IntPtr pModeToMatch, out IntPtr pClosestMatch, IntPtr pConcernedDevice);
            [PreserveSig] int GetDisplaySurfaceData1(IntPtr pDestination);
            [PreserveSig] int DuplicateOutput(IntPtr pDevice, out IntPtr ppOutputDuplication);

            // IDXGIOutput2
            [PreserveSig] bool SupportsOverlays();

            // IDXGIOutput3
            [PreserveSig] int CheckOverlaySupport(int EnumFormat, out IntPtr pConcernedDevice, out uint pFlags);

            // IDXGIOutput4
            [PreserveSig] int CheckOverlayColorSpaceSupport(int EnumFormat, int ColorSpace, out IntPtr pConcernedDevice, out uint pFlags);

            // IDXGIOutput5
            [PreserveSig] int DuplicateOutput1(IntPtr pDevice, uint Flags, int SupportedFormatsCount, IntPtr pSupportedFormats, out IntPtr ppOutputDuplication);

            // IDXGIOutput6
            [PreserveSig] int GetDesc1(out DXGI_OUTPUT_DESC1 pDesc);
            [PreserveSig] int CheckHardwareCompositionSupport(ref uint pFlags);
        }

        public const int DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020 = 12;
    }
}
