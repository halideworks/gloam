using System;
using System.Text.Json;
using HDRGammaController.Core;

namespace HDRGammaController.DebugMonitors
{
    static class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("Enumerating Monitors...");
            try
            {
                var manager = new MonitorManager();
                var monitors = manager.EnumerateMonitors();

                Console.WriteLine($"Found {monitors.Count} monitors.");

                var options = new JsonSerializerOptions { WriteIndented = true };
                foreach (var monitor in monitors)
                {
                    Console.WriteLine("--------------------------------------------------");
                    // Custom formatting for legibility before JSON dump
                    Console.WriteLine($"Device: {monitor.DeviceName}");
                    Console.WriteLine($"Friendly: {monitor.FriendlyName}");
                    Console.WriteLine($"HDR: {(monitor.IsHdrActive ? "ACTIVE" : "Inactive")} (Capable: {monitor.IsHdrCapable})");

                    string json = JsonSerializer.Serialize(monitor, options);
                    Console.WriteLine(json);
                }
                Console.WriteLine("--------------------------------------------------");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Critical Error: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
            }

            Console.WriteLine("Press any key to exit...");
            if (!Console.IsInputRedirected)
            {
                try { Console.ReadKey(); } catch { }
            }
        }
    }
}
