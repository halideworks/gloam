using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using HDRGammaController.Interop;

namespace HDRGammaController.Core
{
    public class MonitorManager
    {
        public List<MonitorInfo> EnumerateMonitors()
        {
            var monitors = new List<MonitorInfo>();
            
            // 1. Create DXGI Factory1 (Base for newer)
            Log.Info("MonitorManager: Creating DXGI Factory1...");
            Guid iidFactory1 = Dxgi.IID_IDXGIFactory1;
            int hr = Dxgi.CreateDXGIFactory1(ref iidFactory1, out IntPtr pFactory);
            Log.Info($"MonitorManager: CreateDXGIFactory1 returned HR={hr}, Ptr={pFactory}");

            if (pFactory == IntPtr.Zero)
            {
                // Fallback or error
                Log.Info("MonitorManager: Failed to create factory.");
                return monitors;
            }

            // We created an IDXGIFactory1.
            Dxgi.IDXGIFactory1? factory = null;
            try {
                 object? factoryObj = Marshal.GetObjectForIUnknown(pFactory);
                 factory = factoryObj as Dxgi.IDXGIFactory1;
            } catch (Exception ex) {
                Log.Info($"MonitorManager: Failed to wrap IDXGIFactory1: {ex.Message}");
            }
            
            if (factory == null)
            {
                Log.Info("MonitorManager: IDXGIFactory1 wrapper failed.");
                Marshal.Release(pFactory);
                return monitors;
            }

            try
            {
                uint adapterIndex = 0;

                // 2. Enum Adapters
                while (true)
                {
                    IntPtr pAdapter = IntPtr.Zero;
                    hr = factory.EnumAdapters1(adapterIndex, out pAdapter);
                    
                    if (hr == -2147024896) // DXGI_ERROR_NOT_FOUND (End of enumeration)
                    {
                         break;
                    }
                    if (hr < 0)
                    {
                        Log.Info($"MonitorManager: EnumAdapters1 failed for {adapterIndex} with HR={hr}");
                        break;
                    }

                    if (pAdapter == IntPtr.Zero) break;
                    
                    // Wrap adapter using GetObjectForIUnknown and cast via interface.
                    //
                    // COM lifetime invariant: `pAdapter` is a raw IntPtr whose refcount we
                    // release via Marshal.Release below. `GetObjectForIUnknown` creates a
                    // managed RCW with its own refcount; if the cast to IDXGIAdapter1 fails
                    // we must release that RCW explicitly — otherwise the orphaned refcount
                    // is only reclaimed at finalization, leaking one per failed enumeration.
                    Dxgi.IDXGIAdapter1? adapter = null;
                    object? adapterObj = null;
                    try {
                        adapterObj = Marshal.GetObjectForIUnknown(pAdapter);
                        adapter = adapterObj as Dxgi.IDXGIAdapter1;
                        if (adapter == null && adapterObj != null)
                        {
                            Log.Info($"MonitorManager: Adapter {adapterIndex} doesn't support IDXGIAdapter1, type: {adapterObj.GetType().Name}");
                            Marshal.ReleaseComObject(adapterObj);
                            adapterObj = null;
                        }
                    } catch (Exception ex) {
                        Log.Info($"MonitorManager: Exception wrapping adapter {adapterIndex}: {ex.GetType().Name}: {ex.Message}");
                        if (adapterObj != null) { try { Marshal.ReleaseComObject(adapterObj); } catch { } }
                    }
                    
                    if (adapter == null) {
                         Log.Info($"MonitorManager: Failed to wrap IDXGIAdapter1 for index {adapterIndex}");
                         Marshal.Release(pAdapter);
                         adapterIndex++;
                         continue;
                    }

                    Log.Info($"MonitorManager: Found adapter at index {adapterIndex}");

                    Dxgi.DXGI_ADAPTER_DESC1 adapterDesc;
                    adapter.GetDesc1(out adapterDesc);

                    uint outputIndex = 0;

                    // 3. Enum Outputs
                    while (true)
                    {
                        IntPtr pOutput = IntPtr.Zero;
                        hr = adapter.EnumOutputs(outputIndex, out pOutput);
                        
                        if (hr == -2147024896) // DXGI_ERROR_NOT_FOUND
                        {
                            break;
                        }
                        if (hr < 0)
                        {
                            Log.Info($"MonitorManager: EnumOutputs failed for {outputIndex} with HR={hr}");
                            break;
                        }

                        Log.Info($"MonitorManager: Found output {outputIndex}");

                        // QueryInterface for Output6 (HDR). Same RCW lifetime rule as adapters —
                        // if the cast fails we still own a refcount and must release it.
                        Dxgi.IDXGIOutput6? output6 = null;
                        object? outObj = null;
                        try
                        {
                            outObj = Marshal.GetObjectForIUnknown(pOutput);
                            output6 = outObj as Dxgi.IDXGIOutput6;
                            if (output6 == null && outObj != null)
                            {
                                Marshal.ReleaseComObject(outObj);
                                outObj = null;
                            }
                        }
                        catch (Exception ex)
                        {
                            Log.Info($"MonitorManager: Failed to get IDXGIOutput6 for output {outputIndex}: {ex.Message}");
                            if (outObj != null) { try { Marshal.ReleaseComObject(outObj); } catch { } }
                        }

                        if (output6 != null)
                        {
                            Dxgi.DXGI_OUTPUT_DESC1 desc1;
                            output6.GetDesc1(out desc1);

                            var monitorInfo = new MonitorInfo
                            {
                                DeviceName = desc1.DeviceName,
                                AdapterLuid = adapterDesc.AdapterLuid,
                                OutputId = outputIndex,
                                HMonitor = desc1.Monitor,
                                IsHdrCapable = (desc1.ColorSpace == Dxgi.DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020),
                                IsHdrActive = (desc1.ColorSpace == Dxgi.DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020),
                                MonitorBounds = desc1.DesktopCoordinates
                            };

                            EnrichWithGdiData(monitorInfo);
                            monitors.Add(monitorInfo);
                        }
                        else
                        {
                            Log.Info($"MonitorManager: Output {outputIndex} does not support IDXGIOutput6.");
                        }

                        if (pOutput != IntPtr.Zero)
                        {
                             Marshal.Release(pOutput);
                        }

                        outputIndex++;
                    }

                    // Done with Adapter
                    Marshal.Release(pAdapter);
                    adapterIndex++;
                }
            }
            finally
            {
                if (factory != null) Marshal.ReleaseComObject(factory);
            }

            return monitors;
        }

        private void EnrichWithGdiData(MonitorInfo monitor)
        {
            // Use EnumDisplayDevices to find friendly name and path
            // Match via monitor.DeviceName (\\.\DISPLAY1)
            
            var displayDevice = new User32.DISPLAY_DEVICE();
            displayDevice.Initialize();

            if (User32.EnumDisplayDevices(monitor.DeviceName, 0, ref displayDevice, 0))
            {
                // This gets the Monitor info attached to the adapter output
                // displayDevice.DeviceID looks like: MONITOR\GSM5B08\{GUID}
                monitor.MonitorDevicePath = displayDevice.DeviceID;
                
                // Read the EDID once; derive both the friendly name and the reported gamut.
                byte[]? edid = GetEdidBytes(displayDevice.DeviceID);
                string? edidName = edid != null ? ParseEdidForName(edid) : null;
                if (!string.IsNullOrEmpty(edidName))
                {
                    monitor.FriendlyName = edidName;
                    Log.Info($"MonitorManager: Got EDID name '{edidName}' for {monitor.DeviceName}");
                }
                else
                {
                    // Fallback to GDI name (usually "Generic PnP Monitor")
                    monitor.FriendlyName = displayDevice.DeviceString;
                }

                if (edid != null)
                {
                    monitor.EdidColor = ParseEdidColor(edid);
                    if (monitor.EdidColor != null)
                        Log.Info($"MonitorManager: EDID gamut R({monitor.EdidColor.RedX:F3},{monitor.EdidColor.RedY:F3}) " +
                                 $"G({monitor.EdidColor.GreenX:F3},{monitor.EdidColor.GreenY:F3}) " +
                                 $"B({monitor.EdidColor.BlueX:F3},{monitor.EdidColor.BlueY:F3})");
                }
            }
        }
        
        /// <summary>
        /// Reads the raw 128+ byte EDID block for a monitor device from the registry.
        /// </summary>
        private byte[]? GetEdidBytes(string deviceId)
        {
            try
            {
                // DeviceID format: MONITOR\{ManufacturerID}{ProductCode}\{InstanceID}
                // e.g., MONITOR\GSM5B08\{4d36e96e-e325-11ce-bfc1-08002be10318}\0008
                // We need to find the corresponding registry key under:
                // HKLM\SYSTEM\CurrentControlSet\Enum\DISPLAY\{ManufacturerID}{ProductCode}\{InstanceID}\Device Parameters\EDID
                
                if (string.IsNullOrEmpty(deviceId)) return null;
                
                // Parse the device ID to extract manufacturer and product code
                string[] parts = deviceId.Split('\\');
                if (parts.Length < 2) return null;
                
                string monitorId = parts[1]; // e.g., "GSM5B08"
                
                // Search in DISPLAY registry for matching monitors
                using (var displayKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\DISPLAY"))
                {
                    if (displayKey == null) return null;
                    
                    foreach (string subKeyName in displayKey.GetSubKeyNames())
                    {
                        if (!subKeyName.Equals(monitorId, StringComparison.OrdinalIgnoreCase)) continue;
                        
                        using (var monitorKey = displayKey.OpenSubKey(subKeyName))
                        {
                            if (monitorKey == null) continue;
                            
                            foreach (string instanceName in monitorKey.GetSubKeyNames())
                            {
                                using (var instanceKey = monitorKey.OpenSubKey(instanceName))
                                {
                                    if (instanceKey == null) continue;
                                    
                                    using (var paramsKey = instanceKey.OpenSubKey("Device Parameters"))
                                    {
                                        if (paramsKey == null) continue;
                                        
                                        byte[]? edid = paramsKey.GetValue("EDID") as byte[];
                                        if (edid != null && edid.Length >= 128)
                                        {
                                            return edid;
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Info($"MonitorManager: Error reading EDID: {ex.Message}");
            }
            
            return null;
        }
        
        /// <summary>
        /// Decodes the EDID color-characteristics block (bytes 25–34) into CIE xy chromaticities.
        /// Each coordinate is 10 bits: 8 high bits in bytes 27–34, low 2 bits packed into bytes
        /// 25–26. Value = (10-bit) / 1024.
        /// </summary>
        private EdidColorInfo? ParseEdidColor(byte[] e)
        {
            if (e.Length < 35) return null;

            double Coord(int highByte, int lowShift, int whichLowByte)
            {
                int low2 = (e[whichLowByte] >> lowShift) & 0x3;
                int v = (e[highByte] << 2) | low2;
                return v / 1024.0;
            }

            // byte 25 holds Rx,Ry,Gx,Gy low bits; byte 26 holds Bx,By,Wx,Wy low bits.
            var info = new EdidColorInfo
            {
                RedX   = Coord(27, 6, 25),
                RedY   = Coord(28, 4, 25),
                GreenX = Coord(29, 2, 25),
                GreenY = Coord(30, 0, 25),
                BlueX  = Coord(31, 6, 26),
                BlueY  = Coord(32, 4, 26),
                WhiteX = Coord(33, 2, 26),
                WhiteY = Coord(34, 0, 26),
            };

            // Sanity: real primaries are positive, < 1, and red is the most red (highest x).
            if (info.RedX <= 0 || info.RedX > 0.8 || info.GreenY <= 0 || info.GreenY > 0.9 ||
                info.RedX < info.BlueX) // red x should exceed blue x on any sane display
                return null;
            return info;
        }

        /// <summary>
        /// Parses EDID data to extract the monitor name from descriptor blocks.
        /// </summary>
        private string? ParseEdidForName(byte[] edid)
        {
            // EDID is 128 bytes minimum
            // Detailed timing descriptors start at byte 54
            // Each descriptor is 18 bytes
            // Descriptor type 0xFC = Monitor name

            // SECURITY: Validate minimum EDID size before any access
            if (edid == null || edid.Length < 128) return null;

            // Check for valid EDID header
            if (edid[0] != 0x00 || edid[1] != 0xFF || edid[2] != 0xFF || edid[3] != 0xFF ||
                edid[4] != 0xFF || edid[5] != 0xFF || edid[6] != 0xFF || edid[7] != 0x00)
            {
                return null;
            }

            // Search through the 4 descriptor blocks (starting at offsets 54, 72, 90, 108)
            for (int offset = 54; offset <= 108; offset += 18)
            {
                // SECURITY: Bounds check - ensure we won't read past end of array
                // We need to access offset+5 through offset+17 (13 bytes of name data)
                if (offset + 18 > edid.Length)
                {
                    Log.Info($"MonitorManager: EDID too short for descriptor at offset {offset}");
                    break;
                }

                // Check if this is a text descriptor (bytes 0-3 are 0x00, byte 3 is descriptor type)
                if (edid[offset] == 0x00 && edid[offset + 1] == 0x00 &&
                    edid[offset + 2] == 0x00 && edid[offset + 3] == 0xFC)
                {
                    // Bytes 5-17 contain the monitor name (13 characters max)
                    // SECURITY: Calculate safe copy length
                    int nameStartOffset = offset + 5;
                    int maxCopyLen = Math.Min(13, edid.Length - nameStartOffset);
                    if (maxCopyLen <= 0) continue;

                    var nameBytes = new byte[maxCopyLen];
                    Array.Copy(edid, nameStartOffset, nameBytes, 0, maxCopyLen);

                    string name = Encoding.ASCII.GetString(nameBytes);
                    // Trim trailing newlines, spaces, and null characters
                    name = name.TrimEnd('\n', '\r', ' ', '\0');

                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        return name;
                    }
                }
            }

            return null;
        }
    }
}
