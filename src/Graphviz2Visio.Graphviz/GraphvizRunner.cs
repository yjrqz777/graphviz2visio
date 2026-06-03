using System;
using System.Diagnostics;
using System.IO;

namespace Graphviz2Visio.Graphviz
{
    public static class GraphvizRunner
    {
        public static string RunDotToPlain(string dotFile, string plainFile, string dotExePath = null)
        {
            if (!File.Exists(dotFile))
                throw new FileNotFoundException("找不到 dot 文件", dotFile);

            string dotExe = string.IsNullOrWhiteSpace(dotExePath)
                ? GraphvizLocator.FindDotExe()
                : dotExePath;

            if (!File.Exists(dotExe))
                throw new FileNotFoundException("找不到 dot.exe", dotExe);

            string outputDir = Path.GetDirectoryName(Path.GetFullPath(plainFile));
            if (!string.IsNullOrWhiteSpace(outputDir) && !Directory.Exists(outputDir))
                Directory.CreateDirectory(outputDir);

            var psi = new ProcessStartInfo
            {
                FileName = dotExe,
                Arguments = $"-Tplain \"{Path.GetFullPath(dotFile)}\" -o \"{Path.GetFullPath(plainFile)}\"",
                WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(dotFile)) ?? Environment.CurrentDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };

            using (var process = Process.Start(psi))
            {
                string stdout = process.StandardOutput.ReadToEnd();
                string stderr = process.StandardError.ReadToEnd();

                process.WaitForExit();

                if (process.ExitCode != 0)
                {
                    throw new Exception(
                        "Graphviz 执行失败。" + Environment.NewLine +
                        "dot.exe: " + dotExe + Environment.NewLine +
                        "stderr: " + stderr + Environment.NewLine +
                        "stdout: " + stdout);
                }
            }

            return dotExe;
        }
    }
}
