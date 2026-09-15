using Gloam.Core.Calibration;
using Xunit;

namespace Gloam.Tests
{
    public class ColorimeterDetectionTests
    {
        [Fact]
        public void ParseDeviceListOutput_BareSerialPortOnly_IsNotAnInstrument()
        {
            // The issue #7 start-up trace: only a COM header was enumerated.
            var info = ColorimeterService.ParseDeviceListOutput(
                " -c listno            Set instrument port from the following list (default 1)\n" +
                "    1 = 'COM6'\n" +
                " -y l|c                Other: l = LCD, c = CRT\n");

            Assert.Null(info);
        }

        [Fact]
        public void ParseDeviceListOutput_HidInstrumentAfterSerialPort_UsesItsIndex()
        {
            var info = ColorimeterService.ParseDeviceListOutput(
                "    1 = 'hid:/33 (X-Rite i1 DisplayPro, ColorMunki Display)'\n" +
                "    2 = 'COM6'\n");

            Assert.NotNull(info);
            Assert.Equal(1, info!.InstrumentIndex);
            Assert.Contains("i1 Display", info.Model);
        }

        [Fact]
        public void ParseDeviceListOutput_UnknownUsbDescriptor_StillCountsAsDetected()
        {
            var info = ColorimeterService.ParseDeviceListOutput(
                "    1 = 'COM3'\n" +
                "    2 = 'usb:/5 (Some New Meter)'\n");

            Assert.NotNull(info);
            Assert.Equal(2, info!.InstrumentIndex);
        }
    }
}
