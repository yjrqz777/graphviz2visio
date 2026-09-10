using System;
using System.IO;
using System.Linq;
using Graphviz2Visio.Graphviz;
using Graphviz2Visio.Visio.Rendering;

namespace Graphviz2Visio.Cli
{
    internal class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                if (args == null || args.Length == 0)
                {
                    PrintUsage();
                    return 1;
                }

                string command = args[0].Trim().ToLowerInvariant();

                switch (command)
                {
                    case "dot2plain":
                    case "to-plain":
                        return RunDot2Plain(args);

                    case "plain2visio":
                    case "to-visio":
                        return RunPlain2Visio(args);
                    case "plain2visio-batch":
                    case "plain2visio-pages":
                        return RunPlain2VisioBatch(args);

                    case "where-dot":
                        return RunWhereDot();

                    default:
                        Console.WriteLine("未知命令: " + args[0]);
                        PrintUsage();
                        return 1;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("执行失败：");
                Console.WriteLine(ex.Message);
                return 2;
            }
        }

        private static int RunDot2Plain(string[] args)
        {
            if (args.Length < 3)
            {
                Console.WriteLine("参数不足。");
                Console.WriteLine("用法: Graphviz2Visio.Cli dot2plain input.dot output.plain");
                return 1;
            }

            string dotFile = args[1];
            string plainFile = args[2];

            string dotExe = GraphvizRunner.RunDotToPlain(dotFile, plainFile);

            Console.WriteLine("Graphviz: " + dotExe);
            Console.WriteLine("已生成 plain: " + Path.GetFullPath(plainFile));
            return 0;
        }

        private static int RunPlain2Visio(string[] args)
        {
            if (args.Length < 3)
            {
                Console.WriteLine("参数不足。");
                Console.WriteLine("用法: Graphviz2Visio.Cli plain2visio input.plain output.vsdx [--visible]");
                return 1;
            }

            string plainFile = args[1];
            string vsdxFile = args[2];
            bool visible = args.Skip(3).Any(a => a.Equals("--visible", StringComparison.OrdinalIgnoreCase));

            VisioRenderer.RenderPlainToVisio(plainFile, vsdxFile, visible);

            Console.WriteLine("已生成 Visio: " + Path.GetFullPath(vsdxFile));
            return 0;
        }

        private static int RunPlain2VisioBatch(string[] args)
        {
            if (args.Length < 4)
            {
                Console.WriteLine("参数不足。");
                Console.WriteLine("用法: Graphviz2Visio.Cli plain2visio-batch output.vsdx input1.plain [input2.plain ...] --page-name 中文标题1 [--page-name 中文标题2 ...] [--visible]");
                return 1;
            }

            string vsdxFile = args[1];
            var plainFiles = new System.Collections.Generic.List<string>();
            var pageNames = new System.Collections.Generic.List<string>();
            bool visible = false;

            for (int index = 2; index < args.Length; index++)
            {
                string argument = args[index];
                if (argument.Equals("--visible", StringComparison.OrdinalIgnoreCase))
                {
                    visible = true;
                    continue;
                }

                if (argument.Equals("--page-name", StringComparison.OrdinalIgnoreCase))
                {
                    if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
                    {
                        Console.WriteLine("--page-name 后必须提供中文页面标题。");
                        return 1;
                    }

                    pageNames.Add(args[++index]);
                    continue;
                }

                if (argument.StartsWith("--", StringComparison.Ordinal))
                {
                    Console.WriteLine("未知选项: " + argument);
                    return 1;
                }

                plainFiles.Add(argument);
            }

            if (plainFiles.Count == 0)
            {
                Console.WriteLine("至少需要一个 plain 输入文件。");
                return 1;
            }

            if (pageNames.Count != plainFiles.Count)
            {
                Console.WriteLine("每个 plain 输入文件都必须提供一个 --page-name 中文标题。");
                return 1;
            }

            VisioRenderer.RenderPlainFilesToVisio(plainFiles, vsdxFile, visible, pageNames);

            Console.WriteLine("已生成 Visio: " + Path.GetFullPath(vsdxFile));
            Console.WriteLine("页面数量: " + plainFiles.Count);
            return 0;
        }
        private static int RunWhereDot()
        {
            string dotExe = GraphvizLocator.FindDotExe();
            Console.WriteLine(dotExe);
            return 0;
        }

        private static void PrintUsage()
        {
            Console.WriteLine("Graphviz2Visio");
            Console.WriteLine();
            Console.WriteLine("命令：");
            Console.WriteLine("  dot2plain    <input.dot>   <output.plain>");
            Console.WriteLine("  plain2visio  <input.plain> <output.vsdx> [--visible]");
            Console.WriteLine("  plain2visio-batch <output.vsdx> <input1.plain> [input2.plain ...] --page-name <中文标题1> [--page-name <中文标题2> ...] [--visible]");
            Console.WriteLine("  where-dot");
            Console.WriteLine();
            Console.WriteLine("示例：");
            Console.WriteLine("  Graphviz2Visio.Cli dot2plain samples\\flow.dot samples\\flow.plain");
            Console.WriteLine("  Graphviz2Visio.Cli plain2visio samples\\flow.plain output\\flow.vsdx");
        }
    }
}
