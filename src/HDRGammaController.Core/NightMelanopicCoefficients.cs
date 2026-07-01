namespace HDRGammaController.Core
{
    /// <summary>
    /// Relative per-primary melanopic and photopic coefficients estimated from display spectra.
    /// Values are normalized for ratios, not absolute melanopic EDI.
    /// </summary>
    public sealed class NightMelanopicCoefficients
    {
        public NightMelanopicCoefficients(
            double redMelanopic, double greenMelanopic, double blueMelanopic,
            double redLuminance, double greenLuminance, double blueLuminance,
            string sourceName)
        {
            RedMelanopic = redMelanopic;
            GreenMelanopic = greenMelanopic;
            BlueMelanopic = blueMelanopic;
            RedLuminance = redLuminance;
            GreenLuminance = greenLuminance;
            BlueLuminance = blueLuminance;
            SourceName = sourceName;
        }

        public double RedMelanopic { get; }
        public double GreenMelanopic { get; }
        public double BlueMelanopic { get; }
        public double RedLuminance { get; }
        public double GreenLuminance { get; }
        public double BlueLuminance { get; }
        public string SourceName { get; }

        public double RedMelanopicPerLuminance => SafeRatio(RedMelanopic, RedLuminance);
        public double GreenMelanopicPerLuminance => SafeRatio(GreenMelanopic, GreenLuminance);
        public double BlueMelanopicPerLuminance => SafeRatio(BlueMelanopic, BlueLuminance);

        private static double SafeRatio(double numerator, double denominator) =>
            double.IsFinite(numerator) && double.IsFinite(denominator) && denominator > 1e-12
                ? numerator / denominator
                : 0.0;
    }
}
