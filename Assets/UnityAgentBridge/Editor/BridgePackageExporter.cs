using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;

namespace UnityAgentBridge.Editor
{
    internal static class BridgePackageExporter
    {
        internal sealed class Result
        {
            public string path;
            public long bytes;
            public string sha256;
        }

        private const string StagingRoot = "Assets/__UnityAgentBridgeExport";
        private const string StagingAssetRoot = StagingRoot + "/UnityAgentBridge";
        private const string PackageAssetRoot = "Assets/UnityAgentBridge";
        private static readonly HashSet<string> ExcludedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".work", ".venv", "__pycache__", "cache", "Materials", "models", "Runtime", "venv"
        };
        private static readonly HashSet<string> ExcludedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".pyc", ".pyo", ".tmp", ".zip", ".unitypackage"
        };

        public static Result Pack(string assetRoot)
        {
            if (!Directory.Exists(assetRoot))
                throw new DirectoryNotFoundException("Unity Agent Bridge folder was not found: " + assetRoot);

            var projectRoot = FindProjectRoot(assetRoot);
            var stagingPath = Path.Combine(projectRoot, StagingAssetRoot.Replace('/', Path.DirectorySeparatorChar));
            var target = Path.Combine(projectRoot, "UnityAgentBridge.unitypackage");
            try
            {
                AssetDatabase.DeleteAsset(StagingRoot);
                CopyIncluded(assetRoot, stagingPath, true);
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                AssetDatabase.ExportPackage(StagingAssetRoot, target, ExportPackageOptions.Recurse);
                RewritePathnames(target);
            }
            finally
            {
                AssetDatabase.DeleteAsset(StagingRoot);
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            }

            if (!File.Exists(target) || new FileInfo(target).Length == 0)
                throw new InvalidOperationException("Unity did not create the package.");

            return new Result
            {
                path = target,
                bytes = new FileInfo(target).Length,
                sha256 = Sha256(target)
            };
        }

        public static void PackCurrentProject()
        {
            Pack(Path.Combine(Directory.GetCurrentDirectory(), PackageAssetRoot.Replace('/', Path.DirectorySeparatorChar)));
        }

        private static void CopyIncluded(string source, string destination, bool root)
        {
            Directory.CreateDirectory(destination);
            foreach (var directory in Directory.GetDirectories(source))
            {
                var name = Path.GetFileName(directory);
                if (ExcludedDirectories.Contains(name) || root && name.Equals("Prefabs", StringComparison.OrdinalIgnoreCase))
                    continue;
                CopyIncluded(directory, Path.Combine(destination, StagingName(name)), false);
            }
            foreach (var file in Directory.GetFiles(source))
            {
                var extension = Path.GetExtension(file);
                if (extension.Equals(".meta", StringComparison.OrdinalIgnoreCase) || ExcludedExtensions.Contains(extension))
                    continue;
                File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
            }
        }

        private static string StagingName(string name)
        {
            if (name.EndsWith("~", StringComparison.Ordinal))
                return name.Substring(0, name.Length - 1) + "_";
            return name.StartsWith(".", StringComparison.Ordinal) ? "_" + name.Substring(1) : name;
        }

        private static void RewritePathnames(string packagePath)
        {
            byte[] archive;
            using (var input = File.OpenRead(packagePath))
            using (var gzip = new GZipStream(input, CompressionMode.Decompress))
            using (var memory = new MemoryStream())
            {
                gzip.CopyTo(memory);
                archive = memory.ToArray();
            }

            var offset = 0;
            while (offset + 512 <= archive.Length && !IsZeroBlock(archive, offset))
            {
                var name = ReadAscii(archive, offset, 100).TrimEnd('\0');
                var size = ReadOctal(archive, offset + 124, 12);
                var dataOffset = offset + 512;
                if (name.EndsWith("/pathname", StringComparison.Ordinal))
                {
                    var path = ReadAscii(archive, dataOffset, checked((int)size));
                    var rewritten = RewritePath(path);
                    var bytes = Encoding.UTF8.GetBytes(rewritten);
                    if (bytes.Length > size)
                        throw new InvalidOperationException("Rewritten UnityPackage path is longer than its staging path: " + rewritten);
                    Array.Clear(archive, dataOffset, checked((int)size));
                    Buffer.BlockCopy(bytes, 0, archive, dataOffset, bytes.Length);
                    WriteOctal(archive, offset + 124, 12, bytes.Length, false);
                    WriteChecksum(archive, offset);
                }
                offset = dataOffset + checked((int)((size + 511L) / 512L * 512L));
            }

            using (var output = File.Create(packagePath))
            using (var gzip = new GZipStream(output, CompressionLevel.Optimal))
                gzip.Write(archive, 0, archive.Length);
        }

        private static string RewritePath(string path)
        {
            var result = path.Replace(StagingAssetRoot, PackageAssetRoot);
            result = result.Replace("/Python_/", "/Python~/").Replace("/CodexPlugin_/", "/CodexPlugin~/").Replace("/ClaudePlugin_/", "/ClaudePlugin~/").Replace("/ZaiPlugin_/", "/ZaiPlugin~/");
            if (result.EndsWith("/Python_", StringComparison.Ordinal)) result = result.Substring(0, result.Length - 1) + "~";
            if (result.EndsWith("/CodexPlugin_", StringComparison.Ordinal)) result = result.Substring(0, result.Length - 1) + "~";
            if (result.EndsWith("/ClaudePlugin_", StringComparison.Ordinal)) result = result.Substring(0, result.Length - 1) + "~";
            if (result.EndsWith("/ZaiPlugin_", StringComparison.Ordinal)) result = result.Substring(0, result.Length - 1) + "~";
            return result;
        }

        private static bool IsZeroBlock(byte[] data, int offset)
        {
            for (var index = 0; index < 512; index++)
                if (data[offset + index] != 0)
                    return false;
            return true;
        }

        private static string ReadAscii(byte[] data, int offset, int length)
        {
            return Encoding.UTF8.GetString(data, offset, length);
        }

        private static long ReadOctal(byte[] data, int offset, int length)
        {
            var text = Encoding.ASCII.GetString(data, offset, length).Trim('\0', ' ');
            return string.IsNullOrEmpty(text) ? 0L : Convert.ToInt64(text, 8);
        }

        private static void WriteOctal(byte[] data, int offset, int length, long value, bool terminate)
        {
            var suffix = terminate ? " \0" : " ";
            var digits = Convert.ToString(value, 8);
            var field = digits.PadLeft(length - suffix.Length) + suffix;
            Encoding.ASCII.GetBytes(field, 0, field.Length, data, offset);
        }

        private static void WriteChecksum(byte[] data, int headerOffset)
        {
            for (var index = 0; index < 8; index++)
                data[headerOffset + 148 + index] = 32;
            long checksum = 0;
            for (var index = 0; index < 512; index++)
                checksum += data[headerOffset + index];
            WriteOctal(data, headerOffset + 148, 8, checksum, true);
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

        private static string Sha256(string path)
        {
            using (var algorithm = SHA256.Create())
            using (var stream = File.OpenRead(path))
                return BitConverter.ToString(algorithm.ComputeHash(stream)).Replace("-", string.Empty);
        }
    }
}
