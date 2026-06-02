# Flow2Visio

**将 Graphviz DOT 流程图转换为可编辑的 Microsoft Visio (.vsdx) 文件。**

Flow2Visio 是一个命令行工具，利用 Graphviz 强大的自动布局能力生成坐标，再通过 Visio COM 接口将流程图绘制为原生 Visio 图形。生成的 `.vsdx` 文件完全可编辑，方便后续在 Visio 中微调。

![效果图](doc\img\image_22.png "效果图")

## 工作流程

```
.dot 文件 ──(Graphviz dot -Tplain)──> .plain 文件 ──(解析 + Visio 绘制)──> .vsdx 文件
```

## 功能特性

- **dot2plain** — 调用 Graphviz 将 `.dot` 文件转换为 `plain` 布局格式
- **plain2visio** — 解析 `plain` 文件并通过 Visio COM 绘制为原生图形
- 自动识别项目内嵌的 Graphviz，无需手动安装到系统 PATH
- 支持 box / ellipse / diamond 三种节点形状
- 支持贝塞尔曲线采样、边标签、虚线样式、颜色映射
- 使用 dynamic COM 调用，无需 Visual Studio 即可编译

## 环境要求

| 依赖 | 说明 |
|------|------|
| [.NET 8 SDK](https://dotnet.microsoft.com/download) | 编译和运行 |
| [Graphviz](https://graphviz.org/) | 已随项目打包在 `tools/` 目录 |
| Microsoft Visio | 运行 `plain2visio` 时需要安装 |

## 快速开始

### 1. 克隆项目

```bash
git clone <repo-url>
cd DotPlainVisio
```

### 2. 编译

```bash
dotnet build Flow2Visio.slnx
```

### 3. 验证 Graphviz 识别

```bash
dotnet run --project src/Flow2Visio.Cli -- where-dot
```

正常输出：

```
D:\...\Flow2Visio\tools\Graphviz-15.0.0-win64\bin\dot.exe
```

### 4. 转换流程图

```bash
# DOT → Plain
dotnet run --project src/Flow2Visio.Cli -- dot2plain samples\flow.dot samples\flow.plain

# Plain → Visio
dotnet run --project src/Flow2Visio.Cli -- plain2visio samples\flow.plain output\flow.vsdx --visible
```

## 命令参考

```
Flow2Visio.Cli <command> [arguments]
```

| 命令 | 说明 | 用法 |
|------|------|------|
| `dot2plain` | DOT 转 Plain | `dot2plain <input.dot> <output.plain>` |
| `plain2visio` | Plain 转 Visio | `plain2visio <input.plain> <output.vsdx> [--visible]` |
| `where-dot` | 显示 dot.exe 路径 | `where-dot` |

`--visible` 参数会在转换过程中显示 Visio 窗口，方便调试。

## 项目结构

```
Flow2Visio/
├── src/
│   ├── Flow2Visio.Core/           # 核心库
│   │   ├── Models/                # 数据模型 (Pt, GraphInfo, NodeInfo, EdgeInfo)
│   │   ├── Parsing/               # Plain 格式解析器
│   │   └── Utils/                 # 工具类 (Tokenizer, BezierHelper, ColorHelper)
│   ├── Flow2Visio.Graphviz/       # Graphviz 模块
│   │   ├── GraphvizLocator.cs     # 自动定位 dot.exe
│   │   └── GraphvizRunner.cs      # 调用 dot.exe 执行转换
│   ├── Flow2Visio.Visio/          # Visio 渲染模块
│   │   └── Rendering/
│   │       └── VisioRenderer.cs   # 通过 dynamic COM 绘制图形
│   └── Flow2Visio.Cli/            # 命令行入口
│       └── Program.cs
├── tools/
│   └── Graphviz-15.0.0-win64/        # 内嵌的 Graphviz
├── samples/
│   ├── flow.dot                      # 示例 DOT 文件
│   └── flow.plain                    # 示例 Plain 文件
└── output/                           # 默认输出目录
```

## 架构说明

项目分为 4 个模块，职责清晰：

- **Core** — 纯业务逻辑，不依赖 Graphviz 和 Visio，可独立测试
- **Graphviz** — 封装 Graphviz 的查找和调用，支持自动识别 `tools/Graphviz-*` 目录
- **Visio** — 使用 dynamic COM 调用 Visio 绘制图形，无需配置 Interop 引用
- **Cli** — 命令行入口，解析参数并调度对应模块

## 示例 DOT 文件

```dot
digraph G {
    rankdir=TB;
    node [fontname="Microsoft YaHei", fontsize=10];

    start [label="开始", shape=ellipse];
    review [label="审核资料", shape=box];
    decision [label="资料完整?", shape=diamond];
    approve [label="进入审批", shape=box];
    reject [label="退回修改", shape=box];
    end [label="结束", shape=ellipse];

    start -> review;
    review -> decision;
    decision -> approve [label="是"];
    decision -> reject [label="否"];
    reject -> review [style=dashed];
    approve -> end;
}
```

## 常见问题

### 找不到 dot.exe

确认 `tools/Graphviz-15.0.0-win64/bin/dot.exe` 存在。`GraphvizLocator` 会从程序所在目录向上查找 `tools` 目录中的 Graphviz。

### plain2visio 报 COM 错误

确保系统已安装 Microsoft Visio。程序通过 `Visio.Application` ProgID 调用 COM 接口，需要 Visio 正确注册。

### 编译报 dynamic 相关错误

本项目使用 `dynamic COM` 方式调用 Visio，不需要添加 `Microsoft.Office.Interop.Visio` 引用。确保使用 .NET 8 SDK 编译。

## 许可证

MIT License
