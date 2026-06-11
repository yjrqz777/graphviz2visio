using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;

namespace Graphviz2Visio.Graphviz
{
    public static class GraphvizLocator
    {
        private const string EmbeddedGraphvizResourceName = "Graphviz2Visio.Graphviz.Embedded.GraphvizWin64.zip";

        public static string FindDotExe(string startDirectory = null)
        {
            startDirectory = startDirectory ?? AppDomain.CurrentDomain.BaseDirectory;

            var searchedToolsDirs = new List<string>();

            foreach (var toolsDir in EnumerateCandidateToolsDirectories(startDirectory))
            {
                searchedToolsDirs.Add(toolsDir);

                string dotExe = FindDotExeInToolsDirectory(toolsDir);
                if (!string.IsNullOrWhiteSpace(dotExe) && File.Exists(dotExe))
                    return dotExe;
            }

            string embeddedDotExe = TryExtractEmbeddedGraphviz();
            if (!string.IsNullOrWhiteSpace(embeddedDotExe) && File.Exists(embeddedDotExe))
                return embeddedDotExe;

            throw new FileNotFoundException(
                "dot.exe was not found. Ensure a tools directory contains Graphviz with bin\\dot.exe, " +
                "or build with the embedded Graphviz package." + Environment.NewLine +
                "Searched tools directories:" + Environment.NewLine +
                string.Join(Environment.NewLine, searchedToolsDirs.Distinct()) + Environment.NewLine +
                "Embedded Graphviz resource: " + EmbeddedGraphvizResourceName);
        }

        private static IEnumerable<string> EnumerateCandidateToolsDirectories(string startDirectory)
        {
            var yielded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var dir = new DirectoryInfo(startDirectory);

            while (dir != null)
            {
                if (string.Equals(dir.Name, "tools", StringComparison.OrdinalIgnoreCase))
                {
                    if (yielded.Add(dir.FullName))
                        yield return dir.FullName;
                }

                string childTools = Path.Combine(dir.FullName, "tools");
                if (Directory.Exists(childTools) && yielded.Add(childTools))
                    yield return childTools;

                dir = dir.Parent;
            }
        }

        private static string FindDotExeInToolsDirectory(string toolsDir)
        {
            if (!Directory.Exists(toolsDir))
                return null;

            string directDot = Path.Combine(toolsDir, "dot.exe");
            if (File.Exists(directDot))
                return directDot;

            string directBinDot = Path.Combine(toolsDir, "bin", "dot.exe");
            if (File.Exists(directBinDot))
                return directBinDot;

            var graphvizDirs = Directory.GetDirectories(toolsDir)
                .Where(d => Path.GetFileName(d).IndexOf("Graphviz", StringComparison.OrdinalIgnoreCase) >= 0)
                .Select(d => new CandidateDir
                {
                    FullPath = d,
                    Name = Path.GetFileName(d),
                    Version = ExtractVersion(Path.GetFileName(d)),
                    ArchScore = GetArchScore(Path.GetFileName(d))
                })
                .OrderByDescending(x => x.ArchScore)
                .ThenByDescending(x => x.Version)
                .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var dir in graphvizDirs)
            {
                string binDot = Path.Combine(dir.FullPath, "bin", "dot.exe");
                if (File.Exists(binDot))
                    return binDot;

                string rootDot = Path.Combine(dir.FullPath, "dot.exe");
                if (File.Exists(rootDot))
                    return rootDot;
            }

            foreach (var dir in graphvizDirs)
            {
                string found = Directory.GetFiles(dir.FullPath, "dot.exe", SearchOption.AllDirectories).FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(found))
                    return found;
            }

            return Directory.GetFiles(toolsDir, "dot.exe", SearchOption.AllDirectories).FirstOrDefault();
        }

        private static string TryExtractEmbeddedGraphviz()
        {
            Stream resourceStream = Assembly.GetExecutingAssembly().GetManifestResourceStream(EmbeddedGraphvizResourceName);
            if (resourceStream == null)
                return null;

            string cacheBase = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(cacheBase))
                cacheBase = Path.GetTempPath();

            string cacheRoot = Path.Combine(cacheBase, "Graphviz2Visio", "embedded-graphviz-win64");
            string toolsDir = Path.Combine(cacheRoot, "tools");

            string cachedDotExe = FindDotExeInToolsDirectory(toolsDir);
            if (!string.IsNullOrWhiteSpace(cachedDotExe) && File.Exists(cachedDotExe))
                return cachedDotExe;

            Directory.CreateDirectory(cacheRoot);

            string zipPath = Path.Combine(cacheRoot, "GraphvizWin64.zip");
            using (resourceStream)
            using (var fileStream = File.Create(zipPath))
            {
                resourceStream.CopyTo(fileStream);
            }

            Directory.CreateDirectory(toolsDir);
            ZipFile.ExtractToDirectory(zipPath, toolsDir, true);

            string extractedDotExe = FindDotExeInToolsDirectory(toolsDir);
            if (!string.IsNullOrWhiteSpace(extractedDotExe) && File.Exists(extractedDotExe))
                return extractedDotExe;

            return null;
        }

        private static Version ExtractVersion(string folderName)
        {
            if (string.IsNullOrWhiteSpace(folderName))
                return new Version(0, 0);

            var match = Regex.Match(folderName, @"(\d+(\.\d+)+)");
            if (match.Success && Version.TryParse(match.Value, out Version version))
                return version;

            return new Version(0, 0);
        }

        private static int GetArchScore(string folderName)
        {
            if (string.IsNullOrWhiteSpace(folderName))
                return 0;

            string name = folderName.ToLowerInvariant();
            bool is64 = Environment.Is64BitProcess;

            if (is64)
            {
                if (name.Contains("64")) return 2;
                if (name.Contains("32") || name.Contains("86")) return 0;
                return 1;
            }

            if (name.Contains("32") || name.Contains("86")) return 2;
            if (name.Contains("64")) return 0;
            return 1;
        }

        private class CandidateDir
        {
            public string FullPath { get; set; }
            public string Name { get; set; }
            public Version Version { get; set; }
            public int ArchScore { get; set; }
        }
    }
}
