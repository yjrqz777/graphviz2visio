using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Graphviz2Visio.Graphviz
{
    public static class GraphvizLocator
    {
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

            throw new FileNotFoundException(
                "未找到 dot.exe。请确认 tools 目录下存在 Graphviz，并且包含 bin\\dot.exe。\r\n" +
                "已搜索目录：\r\n" +
                string.Join("\r\n", searchedToolsDirs.Distinct()));
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

            // 先检查 tools 根目录下是否直接有 dot.exe
            string directDot = Path.Combine(toolsDir, "dot.exe");
            if (File.Exists(directDot))
                return directDot;

            string directBinDot = Path.Combine(toolsDir, "bin", "dot.exe");
            if (File.Exists(directBinDot))
                return directBinDot;

            // 再检查 Graphviz-* 子目录
            var graphvizDirs = Directory.GetDirectories(toolsDir)
                .Where(d => Path.GetFileName(d).StartsWith("Graphviz", StringComparison.OrdinalIgnoreCase))
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

            // 最后兜底：递归搜一遍
            foreach (var dir in graphvizDirs)
            {
                string found = Directory.GetFiles(dir.FullPath, "dot.exe", SearchOption.AllDirectories).FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(found))
                    return found;
            }

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
            else
            {
                if (name.Contains("32") || name.Contains("86")) return 2;
                if (name.Contains("64")) return 0;
                return 1;
            }
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
