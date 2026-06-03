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
            Console.WriteLine("  where-dot");
            Console.WriteLine();
            Console.WriteLine("示例：");
            Console.WriteLine("  Graphviz2Visio.Cli dot2plain samples\\flow.dot samples\\flow.plain");
            Console.WriteLine("  Graphviz2Visio.Cli plain2visio samples\\flow.plain output\\flow.vsdx");
        }
    }
}
