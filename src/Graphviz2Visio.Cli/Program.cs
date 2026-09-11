using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using Graphviz2Visio.Core.Parsing;
using Graphviz2Visio.Core.Validation;
using Graphviz2Visio.Core.Utils;
using Graphviz2Visio.Graphviz;
using Graphviz2Visio.Visio.Rendering;
using Graphviz2Visio.Vsdx.Rendering;
using Graphviz2Visio.Vsdx.Validation;

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
                        return RunPlain2Visio(args, false);
                    case "plain2visio-batch":
                    case "plain2visio-pages":
                        return RunPlain2VisioBatch(args, false);

                    case "plain2visio-com":
                        return RunPlain2Visio(args, true);

                    case "plain2visio-com-batch":
                        return RunPlain2VisioBatch(args, true);

                    case "validate-dot":
                        return RunValidateDot(args);

                    case "validate-layout":
                        return RunValidateLayout(args);

                    case "validate":
                        return RunValidate(args);

                    case "validate-vsdx":
                        return RunValidateVsdx(args);

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

        private static int RunPlain2Visio(string[] args, bool useComRenderer)
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

            if (useComRenderer)
            {
                VisioRenderer.RenderPlainToVisio(plainFile, vsdxFile, visible);
            }
            else
            {
                VsdxXmlRenderer.RenderPlainToVsdx(plainFile, vsdxFile);
                if (visible)
                    OpenFile(vsdxFile);
            }

            Console.WriteLine("已生成 Visio: " + Path.GetFullPath(vsdxFile));
            return 0;
        }

        private static int RunPlain2VisioBatch(string[] args, bool useComRenderer)
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

            if (useComRenderer)
            {
                VisioRenderer.RenderPlainFilesToVisio(plainFiles, vsdxFile, visible, pageNames);
            }
            else
            {
                VsdxXmlRenderer.RenderPlainFilesToVsdx(plainFiles, vsdxFile, pageNames);
                if (visible)
                    OpenFile(vsdxFile);
            }

            Console.WriteLine("已生成 Visio: " + Path.GetFullPath(vsdxFile));
            Console.WriteLine("页面数量: " + plainFiles.Count);
            return 0;
        }

        private static int RunValidateDot(string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("用法: Graphviz2Visio.Cli validate-dot input.dot");
                return 1;
            }

            ValidationResult result = DotRuleValidator.ValidateFile(args[1]);
            PrintJson(result);
            return result.Passed ? 0 : 3;
        }

        private static int RunValidateLayout(string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("用法: Graphviz2Visio.Cli validate-layout input.plain");
                return 1;
            }

            RoutedLayout layout = RoutingPipeline.Prepare(PlainParser.Parse(args[1]));
            PrintJson(new
            {
                detected = layout.Detected,
                validation = layout.Validation,
                passed = layout.Passed
            });
            return layout.Passed ? 0 : 3;
        }

        private static int RunValidate(string[] args)
        {
            if (args.Length < 3)
            {
                Console.WriteLine("用法: Graphviz2Visio.Cli validate input.dot input.plain");
                return 1;
            }

            var report = new ValidationReport
            {
                Dot = DotRuleValidator.ValidateFile(args[1]),
                Layout = LayoutValidator.Validate(PlainParser.Parse(args[2]))
            };
            PrintJson(report);
            return report.Passed ? 0 : 3;
        }

        private static int RunValidateVsdx(string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("用法: Graphviz2Visio.Cli validate-vsdx input.vsdx");
                return 1;
            }

            ValidationResult result = VsdxPackageValidator.ValidateFile(args[1]);
            PrintJson(result);
            return result.Passed ? 0 : 3;
        }

        private static void PrintJson(object value)
        {
            Console.WriteLine(JsonSerializer.Serialize(value, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true
            }));
        }

        private static int RunWhereDot()
        {
            string dotExe = GraphvizLocator.FindDotExe();
            Console.WriteLine(dotExe);
            return 0;
        }

        private static void OpenFile(string path)
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = Path.GetFullPath(path),
                UseShellExecute = true
            });
        }

        private static void PrintUsage()
        {
            Console.WriteLine("Graphviz2Visio");
            Console.WriteLine();
            Console.WriteLine("命令：");
            Console.WriteLine("  dot2plain    <input.dot>   <output.plain>");
            Console.WriteLine("  plain2visio  <input.plain> <output.vsdx> [--visible]");
            Console.WriteLine("  plain2visio-batch <output.vsdx> <input1.plain> [input2.plain ...] --page-name <中文标题1> [--page-name <中文标题2> ...] [--visible]");
            Console.WriteLine("  plain2visio-com <input.plain> <output.vsdx> [--visible]");
            Console.WriteLine("  plain2visio-com-batch <output.vsdx> <input1.plain> [...] --page-name <标题> [...]");
            Console.WriteLine("  validate-dot <input.dot>");
            Console.WriteLine("  validate-layout <input.plain>");
            Console.WriteLine("  validate <input.dot> <input.plain>");
            Console.WriteLine("  validate-vsdx <input.vsdx>");
            Console.WriteLine("  where-dot");
            Console.WriteLine();
            Console.WriteLine("示例：");
            Console.WriteLine("  Graphviz2Visio.Cli dot2plain samples\\flow.dot samples\\flow.plain");
            Console.WriteLine("  Graphviz2Visio.Cli plain2visio samples\\flow.plain output\\flow.vsdx");
        }
    }
}
