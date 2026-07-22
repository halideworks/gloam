using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using HDRGammaController.Core;
using HDRGammaController.Core.Calibration;

namespace HDRGammaController.Services
{
    /// <summary>
    /// Coordinates the UI-facing half of joint HDR refinement: exact matrix planning,
    /// atomic profile installation, superseded-profile cleanup, persistence notification,
    /// and reporting of the state Windows actually accepted.
    /// </summary>
    internal static class HdrRefinementCoordinator
    {
        internal sealed record Installation(
            string ProfileName,
            HdrJointRefinement.State State,
            string? PreviousDefaultProfile);

        internal sealed record Config(
            MonitorInfo Monitor,
            DisplayCharacterization Characterization,
            CalibrationTarget Target,
            double[] LutR,
            double[] LutG,
            double[] LutB,
            double WhiteLevel,
            IReadOnlyList<MeasurementResult>? NativeMeasurements,
            string InstalledProfileName,
            string? PreviousDefaultProfile,
            HdrJointRefinement.State InitialState,
            IReadOnlyList<double> ToneRungs,
            IReadOnlyList<ColoredHdrStimulus> ColorStimuli,
            Func<IReadOnlyList<double>, IReadOnlyList<ColoredHdrStimulus>, int,
                CancellationToken, Task<HdrJointRefinement.Measurements>> MeasureAsync,
            Action<string, string?>? RecordInstalled,
            Action<Installation> InstallationCompleted,
            IProgress<HdrRefinementLoop.PassProgress>? Progress = null);

        internal sealed record Result(
            HdrJointRefinement.Outcome Outcome,
            bool InstalledRefinedProfile);

        internal static async Task<Result> RunAsync(Config config, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(config);
            string? supersededThisSession = null;
            bool installedRefined = false;

            var outcome = await HdrJointRefinement.RunAsync(new HdrJointRefinement.Config
            {
                InitialState = config.InitialState,
                TargetWhite = config.Target.WhitePoint.ToXyz(1.0),
                ToneRungs = config.ToneRungs,
                ColorStimuli = config.ColorStimuli,
                MeasureAsync = config.MeasureAsync,
                ResolveMatrixNeutralScale = correction =>
                {
                    try
                    {
                        return CalibrationProfileInstaller.BuildGamutMatrixPlan(
                            config.Characterization,
                            config.Target,
                            correction).UniformScale;
                    }
                    catch (InvalidOperationException ex) when (IsGamutLimitError(ex.Message))
                    {
                        throw new HdrRefinementGamutLimitException();
                    }
                },
                InstallAsync = async (candidate, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    var install = CalibrationProfileInstaller.Install(
                        config.Monitor,
                        config.Characterization,
                        config.Target,
                        config.LutR,
                        config.LutG,
                        config.LutB,
                        config.WhiteLevel,
                        hdrMode: true,
                        measurements: config.NativeMeasurements,
                        profileNameOverride: BuildRefinedProfileName(config.InstalledProfileName),
                        hdrLutsOverride: candidate.Luts,
                        xyzCorrectionOverride: candidate.XyzCorrection);
                    if (!install.Success)
                    {
                        if (IsGamutLimitError(install.Error))
                            throw new HdrRefinementGamutLimitException();
                        throw new InvalidOperationException($"Joint HDR profile install failed: {install.Error}");
                    }

                    installedRefined = true;
                    if (supersededThisSession is { } stale && stale != install.ProfileName)
                    {
                        try
                        {
                            CalibrationProfileInstaller.Uninstall(config.Monitor, stale);
                        }
                        catch (Exception ex)
                        {
                            Log.Info($"HdrRefinementCoordinator: superseded profile cleanup failed: {ex.Message}");
                        }
                    }
                    supersededThisSession = install.ProfileName;

                    var installedState = new HdrJointRefinement.State(
                        candidate.XyzCorrection,
                        install.HdrLuts ?? candidate.Luts);
                    config.InstallationCompleted(new Installation(
                        install.ProfileName,
                        installedState,
                        config.PreviousDefaultProfile));
                    try
                    {
                        config.RecordInstalled?.Invoke(install.ProfileName, config.PreviousDefaultProfile);
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"HdrRefinementCoordinator: recording installed profile failed: {ex}");
                    }

                    // Installation is already durable. Let the loop record it before a
                    // pending cancellation is observed by the next measurement.
                    await Task.Delay(1000, CancellationToken.None);
                    return (installedState, install.ProfileName);
                },
                Progress = config.Progress,
            }, cancellationToken);

            return new Result(outcome, installedRefined);
        }

        internal static bool IsGamutLimitError(string? message) =>
            message?.Contains("wider gamut", StringComparison.OrdinalIgnoreCase) == true ||
            message?.Contains("physically produce", StringComparison.OrdinalIgnoreCase) == true;

        internal static string BuildRefinedProfileName(string installedProfileName)
        {
            string stem = Path.GetFileNameWithoutExtension(installedProfileName);
            stem = Regex.Replace(stem, @" refined \d{6,9}$", "");
            return $"{stem} refined {DateTime.Now:HHmmssfff}.icm";
        }
    }

    internal sealed class HdrRefinementGamutLimitException : Exception;
}
