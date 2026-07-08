using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using SpotifyWPF.Properties;

namespace SpotifyWPF.Service.Prediction
{
    /// <summary>
    /// Resolves the Python interpreter for the local analysis sidecar and builds a correct
    /// <see cref="ProcessStartInfo"/> (executable vs arguments are separate — never "py -3.12" as FileName).
    /// </summary>
    public static class PythonLauncher
    {
        private static readonly string[] ExecutableNames = { "python.exe", "python3.12.exe", "python3.exe" };

        /// <summary>Stored in user settings; must be a full path to python.exe when set manually.</summary>
        public static string GetConfiguredPath()
        {
            return Settings.Default.PythonExecutablePath?.Trim();
        }

        public static void SaveConfiguredPath(string path)
        {
            Settings.Default.PythonExecutablePath = path?.Trim() ?? string.Empty;
            Settings.Default.Save();
        }

        /// <summary>
        /// Returns a full path to python.exe when possible. Falls back to bare "python" if nothing else works.
        /// </summary>
        public static string ResolveExecutable()
        {
            var configured = GetConfiguredPath();

            if (!string.IsNullOrWhiteSpace(configured))
            {
                if (IsStorePythonAlias(configured))
                {
                    if (TryResolveFromLauncherVersion(ExtractVersionFromAlias(configured), out var fromAlias))
                        return fromAlias;
                }
                else if (File.Exists(configured))
                {
                    return configured;
                }

                if (TryResolveLauncherVersion(configured, out var resolved))
                    return resolved;
            }

            if (TryAutoDetect(out var detected))
            {
                SaveConfiguredPath(detected);
                return detected;
            }

            return "python";
        }

        /// <summary>
        /// Tries the Windows <c>py</c> launcher and common install locations; returns a full python.exe path.
        /// </summary>
        public static bool TryAutoDetect(out string executablePath)
        {
            // Prefer explicit launcher versions first — Store Python often appears in py -0p as
            // "python3.12.exe" without a full path, while py -3.12 resolves sys.base_prefix correctly.
            foreach (var version in new[] { "3.12", "3.13", "3.11", "3" })
            {
                if (TryResolveFromLauncherVersion(version, out executablePath))
                    return true;
            }

            if (TryResolveFromPyListPaths(out executablePath))
                return true;

            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            foreach (var candidate in new[]
                     {
                         Path.Combine(localAppData, @"Programs\Python\Python312\python.exe"),
                         Path.Combine(localAppData, @"Programs\Python\Python313\python.exe"),
                         Path.Combine(localAppData, @"Programs\Python\Python311\python.exe"),
                     })
            {
                if (File.Exists(candidate))
                {
                    executablePath = candidate;
                    return true;
                }
            }

            executablePath = null;
            return false;
        }

        public static ProcessStartInfo CreateSidecarStartInfo(string arguments)
        {
            var executable = ResolveExecutable();

            if (TryParseLauncherToken(executable, out var launcher, out var versionFlag))
            {
                return new ProcessStartInfo
                {
                    FileName = launcher,
                    Arguments = $"{versionFlag} {arguments}",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                };
            }

            return new ProcessStartInfo
            {
                FileName = executable,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };
        }

        private static bool TryResolveLauncherVersion(string token, out string executablePath)
        {
            if (!TryParseLauncherToken(token, out var launcher, out var versionFlag))
            {
                executablePath = null;
                return false;
            }

            return TryResolveFromLauncherVersion(versionFlag.TrimStart('-'), out executablePath);
        }

        private static bool TryResolveFromLauncherVersion(string version, out string executablePath)
        {
            executablePath = null;

            if (string.IsNullOrWhiteSpace(version))
                return false;

            var versionFlag = version.StartsWith("-", StringComparison.Ordinal) ? version : "-" + version;

            return TryQueryBasePrefixExecutable("py", versionFlag, out executablePath);
        }

        /// <summary>
        /// Parses <c>py -0p</c> output for lines that include a full drive path.
        /// </summary>
        private static bool TryResolveFromPyListPaths(out string executablePath)
        {
            executablePath = null;

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "py",
                    Arguments = "-0p",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using (var process = Process.Start(startInfo))
                {
                    if (process == null)
                        return false;

                    var output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit(5000);

                    if (process.ExitCode != 0)
                        return false;

                    string best = null;
                    var bestVersion = 0;

                    foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        var match = Regex.Match(line.Trim(),
                            @"^-(\d+)\.(\d+)(?:-64)?\s+(.+\.exe)\s*$", RegexOptions.IgnoreCase);

                        if (!match.Success)
                            continue;

                        var major = int.Parse(match.Groups[1].Value);
                        var minor = int.Parse(match.Groups[2].Value);
                        var candidate = match.Groups[3].Value.Trim();
                        var versionLabel = $"{major}.{minor}";

                        if (candidate.IndexOf(":\\", StringComparison.Ordinal) < 0)
                        {
                            if (TryResolveFromLauncherVersion(versionLabel, out var resolved))
                            {
                                candidate = resolved;
                            }
                            else
                            {
                                continue;
                            }
                        }

                        if (!File.Exists(candidate))
                            continue;

                        var versionScore = major * 100 + minor;

                        if (versionScore > bestVersion)
                        {
                            bestVersion = versionScore;
                            best = candidate;
                        }
                    }

                    if (best == null)
                        return false;

                    executablePath = best;
                    return true;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Python py -0p probe failed: {ex.Message}");
                return false;
            }
        }

        private static bool TryQueryBasePrefixExecutable(string launcher, string versionFlag,
            out string executablePath)
        {
            executablePath = null;

            try
            {
                const string script =
                    "import sys,os; bp=sys.base_prefix; " +
                    "print(next((os.path.join(bp,n) for n in ('python.exe','python3.12.exe','python3.exe') " +
                    "if os.path.isfile(os.path.join(bp,n))), ''))";

                var startInfo = new ProcessStartInfo
                {
                    FileName = launcher,
                    Arguments = $"{versionFlag} -c \"{script}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using (var process = Process.Start(startInfo))
                {
                    if (process == null)
                        return false;

                    var output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit(5000);

                    if (process.ExitCode != 0)
                        return false;

                    executablePath = output.Trim();

                    return !string.IsNullOrWhiteSpace(executablePath) && File.Exists(executablePath);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Python base_prefix probe failed for {launcher} {versionFlag}: {ex.Message}");
                return false;
            }
        }

        private static bool TryParseLauncherToken(string value, out string launcher, out string versionFlag)
        {
            launcher = null;
            versionFlag = null;

            if (string.IsNullOrWhiteSpace(value))
                return false;

            if (value.StartsWith("py:", StringComparison.OrdinalIgnoreCase))
            {
                launcher = "py";
                versionFlag = "-" + value.Substring(3).TrimStart('-');
                return versionFlag.Length > 1;
            }

            if (string.Equals(value, "py", StringComparison.OrdinalIgnoreCase))
            {
                launcher = "py";
                versionFlag = "-3";
                return true;
            }

            return false;
        }

        private static bool IsStorePythonAlias(string path)
        {
            return !string.IsNullOrWhiteSpace(path) &&
                   path.IndexOf(@"AppData\Local\Microsoft\WindowsApps\PythonSoftwareFoundation",
                       StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string ExtractVersionFromAlias(string path)
        {
            var match = Regex.Match(path, @"Python\.(\d+\.\d+)", RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value : "3.12";
        }
    }
}
