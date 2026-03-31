# Harvnyx
[README for CN](https://github.com/I-AM-SOLO-GetixPolaris/Harvnyx/edit/master/README.md) [README for EN](https://github.com/I-AM-SOLO-GetixPolaris/Harvnyx/edit/master/README.en.md) [README for RU](https://github.com/I-AM-SOLO-GetixPolaris/Harvnyx/edit/master/README.ru.md)
> **说明:** 本清单聚焦于稳定性、资源管理、逻辑缺陷和可维护性问题，**暂不包含安全性相关条目**（如密码明文、插件校验等）。请社区开发者按优先级依次处理。

> **注意:** 不要在您的主要计算机中运行该程序，因为该程序目前未确保信息不会泄露！
---
## 核心定位
Harvnyx 不仅仅是一个简单的命令解释器，它更类似于一个集成式命令执行框架。它无缝融合了内部命令、外部 Windows 可执行文件以及通过插件扩展的命令，为用户提供了一个统一的交互入口。

## 技术架构与核心模块
程序由几个核心协同工作的类组成：
1. **Program.cs:** 程序的交互逻辑与主循环。
   - 主循环：持续读取、解析(支持 `&&`, `-and`, `-or`操作符连接多个命令)和执行用户输入。
   - 高级交互：
      - Tab 自动补全：支持命令、子命令、选项和参数值的智能补全与循环补全。
      - 语法高亮：在输入时即对不同元素（主命令、参数、值等）进行颜色区分。
      - 历史记录与建议：记录命令历史并提供输入建议。
   - 动态提示符：可配置显示当前工作目录或网络(`VPN/IP`)信息。
2. **CommandProcessor.cs:** 系统的命令控制段。
   - 命令路由：解析用户输入，根据命令名决定执行路径：内部命令、插件命令或外部系统命令。
   - 冲突解决：内置四种处理模式(`Harvnyx`/`Windows`/`Mix`/`True`)，智能或交互式地解决内部命令与外部程序重名的问题。
   - 参数解析：支持带引号和转义符的复杂参数。
3. **ConfigProcessor.cs:** 统一的配置与状态管理器。
   - 从 config.ini文件加载和保存用户设置。
   - **关键配置:** 命令查询模式 (`CommandInquiry`)、提示符显示内容 (`additional`)、是否清屏 (`ShellClear`)。
   - **系统监控:** 启动后台任务，定期缓存并快速提供 CPU 占用率/频率和内存使用率信息。
4. **CommandCompleter(`CommandProcessor.cs`):** 补全与建议。
   - 维护着所有可用命令（内置、插件、外部）的清单及其子命令、选项的元数据。
   - 根据 `ConfigProcessor` 中的设置，动态计算当前生效的命令列表，为 `Program` 中的补全和高亮功能提供数据。
   - ~~AI 代写真牛逼~~
5. **CommandExecute(`CommandProcessor.cs`):** 命令解析器。
   - **`sudo`:** 在 Windows 上模拟类 Unix 的提权操作。支持以其他用户身份运行命令、后台执行、验证时间戳缓存等。
   - **`shell`:** 管理 Shell 本身的行为，如配置提示符显示模式、命令查询模式等。
   - **`tasks`:** 管理由 Harvnyx 启动的后台/前台任务，可以列表、查看详情或终止任务。
6. **插件系统 (`ICommand`接口 & `CommandManager`) (`CommandProcessor.cs`, `Program.cs`):** 可扩展性的核心。
   - 定义 `ICommand` 接口，任何实现该接口的类都可以作为插件命令被加载。
   - `CommandManager` 动态扫描并加载当前目录下的 `.dll` 文件，自动注册其中的命令，并更新补全系统。
7. **TaskManager(`Program.cs`):** 任务托管服务。跟踪由 Harvnyx 启动的进程和异步任务，为其分配 ID，便于用户通过 `tasks` 命令进行管理和监控。

## 安全性说明
[0.1.80-alpha.2 的错误](https://github.com/I-AM-SOLO-GetixPolaris/Harvnyx/blob/master/QuestionList/0.1.80-alpha.2.md)
## 安装教程
### 系统要求
- **操作系统**：Windows 7 SP1 / Windows 10 / Windows 11（64位推荐）
- **运行时**：[.NET 8.0 Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/8.0) 或更高版本
- **依赖组件**：无额外依赖

### 安装方式

#### 方式一:安装包安装
1. 前往 [Releases](https://github.com/NORTHTECH-Group/Harvnyx/releases) 页面下载最新版本的 `Harvnyx.zip`
2. 将压缩包解压到任意目录（例如 `C:\Harvnyx`）
3. 进入解压后的文件夹，双击 `Harvnyx.exe` 即可运行

#### 方式二:从源码编译
1. 前置要求
   - [.NET 8.0 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/8.0)
   - Git（可选，用于克隆仓库）
2. 编译步骤
```bash
# 克隆仓库
git clone https://github.com/NORTHTECH-Group/Harvnyx.git
cd Harvnyx

# 还原依赖并编译
dotnet restore
dotnet build -c Release

# 运行
dotnet run --project Harvnyx.csproj
```
编译后的可执行文件位于 `bin\Release\net8.0\Harvnyx.exe`

### 首次运行配置
- 程序启动时会自动生成 config.ini 配置文件，位于程序所在目录
- 支持的命令插件(`.dll`)请放置在程序同目录下，程序会自动加载
- 默认命令处理模式为 `Harvnyx` (仅使用内部命令)，可通过 `shell -i <模式>` 切换。(详情见`技术架构与核心模块`第二段第二小节)
     - ~~**防呆提示**~~
        - **`Harvnyx`:** 仅使用内部命令
        - **`Windows`:** 仅使用 Windows 命令
        - **`Mix`:** 混合使用命令
        - **`True`:** 执行命令前询问用户

### 卸载
直接删除程序所在文件夹即可，无注册表残留。
## 参与贡献
感谢您对 Harvnyx 的兴趣！我们欢迎任何形式的贡献，包括但不限于：报告 Bug、提出新功能、改进文档、提交代码。
### 行为准则
请遵守 [许可证](https://github.com/I-AM-SOLO-GetixPolaris/Harvnyx/edit/master/LICENSE.txr)内容，保持友善和尊重。
### 如何贡献
#### 1. 报告 Bug 或建议新功能
- 在 [Issues](https://github.com/I-AM-SOLO-GetixPolaris/Harvnyx/issues) 页面搜索是否已存在相同问题
- 若无，请新建 Issue，并选择对应模板：
  - **Bug 报告**：附上系统环境、复现步骤、错误日志（可使用 `print` 输出的内容）
  - **功能建议**：描述使用场景和预期行为
#### 2. 改进文档
- 文档位于 `docs/` 目录（或本 README）
- 修正错别字、优化排版、补充示例均可提交 PR
#### 3. 提交代码
##### 前置条件
- 了解 C# 和 .NET 8.0
- 本地已安装 [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
##### 开发流程
```bash
# Fork 主仓库到你的账号下，然后克隆你的 fork
git clone https://github.com/你的用户名/Harvnyx.git
cd Harvnyx

# 添加主仓库为 upstream（可选，便于同步）
git remote add upstream https://github.com/NORTHTECH-Group/Harvnyx.git

# 创建功能分支
git checkout -b feature/你的功能名

# 进行修改，确保代码可编译
dotnet build

# 运行（可选）
dotnet run

# 提交变更（遵循提交规范）
git add .
git commit -m "类型: 简短描述"

# 推送到你的 fork
git push origin feature/你的功能名
```
#### 4. 代码规范
1. **异步:** 所有可能阻塞的操作应使用 `async/await` ，并支持 `CancellationToken`
2. **信息处理:** 使用 `print` 类输出确认/错误/警告
```C#
// 示例
print.error("命令/严重程度", "问题描述", {错误代码print.ErrorCodes});
// 可选严重程度
public static class Severity
{
    public const string LOW = "轻微";
    public const string MEDIUM = "一般";
    public const string HIGH = "严重";
    public const string CRITICAL = "特别严重";
    public const string FATAL = "致命";
}
print.warning("命令/严重程度", "问题描述");
print.success("命令/严重程度(可选)", "问题描述");
```
---
## 源代码

## 程序
