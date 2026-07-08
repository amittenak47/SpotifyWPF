using System;
using System.Diagnostics;
using System.IO;
using SpotifyWPF.Properties;

namespace SpotifyWPF.Service.Prediction
{
    /// <summary>
    /// Resolves the Python interpreter for the local analysis sidecar and builds a correct
    /// <see cref="ProcessStartInfo"/> (executable vs arguments are separate — never "py -3.12" as FileName).
    /// </summary>
    public static class PythonLauncher
    {
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
                if (File.Exists(configured))
                    return configured;

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
            foreach (var version in new[] { "3.12", "3.13", "3.11", "3" })
            {
                if (TryResolveLauncherVersion($"py:-{version}", out executablePath))
                    return true;
            }

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

            if (!TryQueryInterpreterPath(launcher, versionFlag, out executablePath))
                return false;

            return !string.IsNullOrWhiteSpace(executablePath) && File.Exists(executablePath);
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

        private static bool TryQueryInterpreterPath(string launcher, string versionFlag, out string executablePath)
        {
            executablePath = null;

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = launcher,
                    Arguments = $"{versionFlag} -c \"import sys; print(sys.executable)\"",
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
                    return !string.IsNullOrWhiteSpace(executablePath);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Python auto-detect failed for {launcher} {versionFlag}: {ex.Message}");
                return false;
            }
        }
    }
}
