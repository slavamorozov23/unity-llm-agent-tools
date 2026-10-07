using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;

namespace UnityAgentBridge.Editor
{
    internal static class BridgeRuntimeInstaller
    {
        private const string UvVersion = "0.11.32";
        private const string UvArchiveSha256 = "ACFDE570451CFDB8689FA159A138EE805BA4E241C466432750302C86254B0984";
        private const string UvArchiveUrl = "https://github.com/astral-sh/uv/releases/download/0.11.32/uv-x86_64-pc-windows-msvc.zip";

        public static string Ensure(string assetRoot, Action<string> report)
        {
            var runtimeRoot = Path.Combine(FindProjectRoot(assetRoot), "Library", "UnityAgentBridge", "Runtime");
            var environmentRoot = Path.Combine(runtimeRoot, "venv");
            var python = Path.Combine(environmentRoot, "Scripts", "python.exe");
            var requirements = Path.Combine(assetRoot, "Python~", "requirements.txt");
            var marker = Path.Combine(runtimeRoot, "environment-ready.txt");
            if (!File.Exists(requirements))
                throw new FileNotFoundException("Python requirements file was not found.", requirements);

            Directory.CreateDirectory(runtimeRoot);
            var expectedMarker = Sha256(requirements);
            if (File.Exists(python) && File.Exists(marker) && File.ReadAllText(marker).Trim() == expectedMarker)
                return python;

            report?.Invoke("Downloading runtime installer");
            var uv = EnsureUv(runtimeRoot);
            report?.Invoke("Installing local Python");
            if (Directory.Exists(environmentRoot))
                Directory.Delete(environmentRoot, true);

            var environment = new Action<ProcessStartInfo>(info =>
            {
                info.EnvironmentVariables["UV_PYTHON_INSTALL_DIR"] = Path.Combine(runtimeRoot, "python");
                info.EnvironmentVariables["UV_CACHE_DIR"] = Path.Combine(runtimeRoot, "cache");
                info.EnvironmentVariables["UV_NO_PROGRESS"] = "1";
            });
            Run(uv, "venv --python 3.13 --managed-python --no-config \"" + environmentRoot + "\"", runtimeRoot, environment);

            report?.Invoke("Installing Python libraries");
            Run(
                uv,
                "pip sync --python \"" + python + "\" --no-config \"" + requirements + "\"",
                runtimeRoot,
                environment);
            Run(python, "-c \"import fastembed, numpy, onnxruntime, wordninja; from PIL import Image\"", runtimeRoot, null);

            var temporaryMarker = marker + ".tmp";
            File.WriteAllText(temporaryMarker, expectedMarker);
            if (File.Exists(marker))
                File.Delete(marker);
            File.Move(temporaryMarker, marker);
            return python;
        }

        private static string FindProjectRoot(string assetRoot)
        {
            var directory = new DirectoryInfo(assetRoot);
            while (directory != null && !directory.Name.Equals("Assets", StringComparison.OrdinalIgnoreCase))
                directory = directory.Parent;
            if (directory == null || directory.Parent == null)
                throw new InvalidOperationException("Unity project Assets folder was not found.");
            return directory.Parent.FullName;
        }

        private static string EnsureUv(string runtimeRoot)
        {
            var uvRoot = Path.Combine(runtimeRoot, "uv-" + UvVersion);
            var uv = Path.Combine(uvRoot, "uv.exe");
            if (File.Exists(uv))
                return uv;

            Directory.CreateDirectory(uvRoot);
            var archive = Path.Combine(runtimeRoot, "uv-" + UvVersion + ".zip");
            var temporaryArchive = archive + ".tmp";
            if (File.Exists(temporaryArchive))
                File.Delete(temporaryArchive);

            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            using (var client = new WebClient())
                client.DownloadFile(UvArchiveUrl, temporaryArchive);
            if (!Sha256(temporaryArchive).Equals(UvArchiveSha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(temporaryArchive);
                throw new InvalidDataException("Downloaded uv archive failed SHA-256 verification.");
            }
            if (File.Exists(archive))
                File.Delete(archive);
            File.Move(temporaryArchive, archive);
            ExtractZip(archive, uvRoot);
            if (!File.Exists(uv))
                throw new FileNotFoundException("uv.exe was not found in the verified archive.", uv);
            return uv;
        }

        private static void ExtractZip(string archivePath, string destinationRoot)
        {
            var root = Path.GetFullPath(destinationRoot) + Path.DirectorySeparatorChar;
            using (var stream = File.OpenRead(archivePath))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
            {
                foreach (var entry in archive.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name))
                        continue;
                    var target = Path.GetFullPath(Path.Combine(destinationRoot, entry.FullName));
                    if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("uv archive contains an unsafe path: " + entry.FullName);
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    using (var source = entry.Open())
                    using (var destination = File.Create(target))
                        source.CopyTo(destination);
                }
            }
        }

        private static void Run(string executable, string arguments, string workingDirectory, Action<ProcessStartInfo> configure)
        {
            var info = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            configure?.Invoke(info);
            using (var process = Process.Start(info))
            {
                if (process == null)
                    throw new InvalidOperationException("Unable to start: " + executable);
                var output = process.StandardOutput.ReadToEndAsync();
                var error = process.StandardError.ReadToEndAsync();
                process.WaitForExit();
                if (process.ExitCode != 0)
                    throw new InvalidOperationException(
                        Path.GetFileName(executable) + " exited with code " + process.ExitCode + ".\n" +
                        output.GetAwaiter().GetResult() + "\n" + error.GetAwaiter().GetResult());
            }
        }

        private static string Sha256(string path)
        {
            using (var algorithm = SHA256.Create())
            using (var stream = File.OpenRead(path))
                return BitConverter.ToString(algorithm.ComputeHash(stream)).Replace("-", string.Empty);
        }
    }
}
