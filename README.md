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
[0.1.80-alpha.2]()
