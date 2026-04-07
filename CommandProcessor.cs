using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;
using static Harvnyx.ConfigProcessor;


namespace Harvnyx
{
    public static class CommandProcessor
    {
        private const int STD_INPUT_HANDLE = -10;
        private const uint ENABLE_ECHO_INPUT = 0x0004;
        private const uint ENABLE_LINE_INPUT = 0x0002;
        private const uint ENABLE_PROCESSED_INPUT = 0x0001;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetStdHandle(int nStdHandle);

        public static async Task<bool> HandleCommand(string input, CancellationToken token = default)
        {
            if (string.IsNullOrWhiteSpace(input))
                return false;

            List<string> words = ParseArguments(input);
            if (words.Count == 0) return false;

            string command = words[0];
            List<string> args = words.Skip(1).ToList();

            switch (command)
            {
                case "sudo":
                    return await CommandExecute.HandleSudoCommand(args, token);
                case "shell":
                    return await CommandExecute.HandleShellCommand(args);
                case "tasks":
                    return await CommandExecute.HandleTasksCommand(args, token);
                case "ept":
                    return await CommandExecute.HandleEptCommand(args, token);
                default:
                    var plugin = CommandManager.GetPlugin(command);
                    string externalPath = FindExternalExecutable(command);

                    // 处理冲突
                    if (plugin != null && externalPath != null)
                    {
                        switch (ConfigProcessor.CommandInquiry)
                        {
                            case ConfigProcessor.CommandInquiryMode.Windows:
                                return await ExecuteExternalCommand(externalPath, args, token);
                            case ConfigProcessor.CommandInquiryMode.Harvnyx:
                                return await plugin.ExecuteAsync(args, token);
                            case ConfigProcessor.CommandInquiryMode.Mix:
                                // Mix 模式：Harvnyx 优先，但若 Harvnyx 执行失败可考虑 fallback？此处简单优先 Harvnyx
                                return await plugin.ExecuteAsync(args, token);
                            case ConfigProcessor.CommandInquiryMode.True:
                                Console.Write($"命令 '");
                                Console.ForegroundColor = ConsoleColor.Yellow;
                                Console.Write(command);
                                Console.ResetColor();
                                Console.WriteLine("' 存在多个可执行程序");
                                Console.ForegroundColor = ConsoleColor.Green;
                                Console.Write($"{command}.exe");
                                Console.ResetColor();
                                Console.Write(" for windows[");
                                Console.ForegroundColor = ConsoleColor.Cyan;
                                Console.Write(1);
                                Console.ResetColor();
                                Console.Write("] or ");
                                Console.ForegroundColor = ConsoleColor.Green;
                                Console.Write($"{command}.dll");
                                Console.ResetColor();
                                Console.Write(" for Harvnyx[");
                                Console.ForegroundColor = ConsoleColor.Cyan;
                                Console.Write(2);
                                Console.ResetColor();
                                Console.Write("] ");
                                string key = Console.ReadLine();
                                if (key == "1")
                                {
                                    return await ExecuteExternalCommand(externalPath, args, token);
                                }
                                else if (key == "2")
                                {
                                    return await plugin.ExecuteAsync(args, token);
                                }
                                else
                                {
                                    print.error("命令", "无效选择，取消执行", print.ErrorCodes.INVALID_PARAMETER);
                                    return false;
                                }
                            default:
                                return await plugin.ExecuteAsync(args, token);
                        }
                    }
                    else if (plugin != null)
                    {
                        return await plugin.ExecuteAsync(args, token);
                    }
                    else if (externalPath != null)
                    {
                        return await ExecuteExternalCommand(externalPath, args, token);
                    }
                    else
                    {
                        print.error("命令", $"未知命令: {command}", print.ErrorCodes.INVALID_COMMAND);
                        return false;
                    }
            }
            return false;
        }

        public static string FindExternalExecutable(string command)
        {
            // 如果命令包含路径分隔符，直接检查
            if (command.Contains(Path.DirectorySeparatorChar) || command.Contains(Path.AltDirectorySeparatorChar))
            {
                if (File.Exists(command))
                    return Path.GetFullPath(command);
                // 尝试添加扩展名
                string pathExt = Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD";
                var extensions = pathExt.Split(';', StringSplitOptions.RemoveEmptyEntries);
                foreach (var ext in extensions)
                {
                    string testPath = command + ext;
                    if (File.Exists(testPath))
                        return Path.GetFullPath(testPath);
                }
                return null;
            }

            // 获取 PATH 环境变量
            string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
            var paths = pathEnv.Split(Path.PathSeparator).Prepend(Environment.CurrentDirectory).Distinct();
            string[] exts = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD").Split(';', StringSplitOptions.RemoveEmptyEntries);

            foreach (var dir in paths)
            {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
                string fullDir = Path.GetFullPath(dir); // 规范化路径

                // 直接匹配无扩展名（如果文件本身有扩展名）
                string candidate = Path.Combine(fullDir, command);
                if (File.Exists(candidate))
                    return candidate;

                // 尝试每个扩展名
                foreach (var ext in exts)
                {
                    candidate = Path.Combine(fullDir, command + ext);
                    if (File.Exists(candidate))
                    {
                        Debug.WriteLine($"找到外部命令: {candidate}");
                        return candidate;
                    }
                }
            }
            return null;
        }

        private static List<string> ParseArguments(string commandLine)
        {
            var args = new List<string>();
            var current = new StringBuilder();
            bool inQuotes = false;
            bool escapeNext = false;

            for (int i = 0; i < commandLine.Length; i++)
            {
                char c = commandLine[i];
                if (escapeNext)
                {
                    current.Append(c);
                    escapeNext = false;
                    continue;
                }

                if (c == '\\' && inQuotes)
                {
                    // 转义符，仅在引号内生效（例如 \"）
                    escapeNext = true;
                    continue;
                }

                if (c == '"')
                {
                    if (inQuotes)
                    {
                        // 结束引号，但需要检查下一个字符是否为非空白（如紧接其他参数）
                        // 简单起见，这里直接结束引号，后面的空格作为分隔
                        inQuotes = false;
                        // 不添加字符
                    }
                    else
                    {
                        // 开始引号
                        inQuotes = true;
                        // 不添加字符
                    }
                    continue;
                }

                if (!inQuotes && char.IsWhiteSpace(c))
                {
                    // 遇到空白且不在引号内，完成当前参数
                    if (current.Length > 0)
                    {
                        args.Add(current.ToString());
                        current.Clear();
                    }
                    continue;
                }

                current.Append(c);
            }

            if (current.Length > 0)
                args.Add(current.ToString());

            return args;
        }

        /// <summary>执行外部命令（非 sudo），在当前控制台运行，不创建新窗口</summary>
        private static async Task<bool> ExecuteExternalCommand(string exePath, List<string> args, CancellationToken token)
        {
            try
            {
                string arguments = string.Join(" ", args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
                var psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardInput = false,
                    RedirectStandardOutput = false,
                    RedirectStandardError = false,
                    CreateNoWindow = false
                };

                using (Process proc = new Process { StartInfo = psi })
                {
                    proc.Start();
                    // 可以保留调试输出，但建议注释掉以免干扰
                    // print.info("调试", $"外部进程已启动，PID: {proc.Id}");

                    try
                    {
                        await proc.WaitForExitAsync(token);
                        return proc.ExitCode == 0;
                    }
                    catch (OperationCanceledException)
                    {
                        if (!proc.HasExited) proc.Kill();
                        await proc.WaitForExitAsync(CancellationToken.None);
                        print.success("命令", "外部命令执行已取消。");
                        return false;
                    }
                }
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                print.error("命令", $"执行外部命令失败: {ex.Message}", print.ErrorCodes.EXTERNAL_PROGRAM_FAILED);
                return false;
            }
            finally
            {
                while (Console.KeyAvailable) Console.ReadKey(true);
            }
        }
    }

    public static class CommandCompleter
    {
        // 内置数据（不可变）
        private static readonly List<string> _builtInMainCommands;
        private static readonly Dictionary<string, List<string>> _builtInSubCommands;
        private static readonly Dictionary<string, Dictionary<string, List<string>>> _builtInOptionValues;
        private static readonly Dictionary<string, Dictionary<string, List<string>>> _builtInSubCommandOptions;

        // 公开的可变数据（volatile 保证可见性）
        public static volatile List<string> MainCommands;
        public static volatile Dictionary<string, List<string>> SubCommands;
        public static volatile Dictionary<string, Dictionary<string, List<string>>> OptionValues;
        public static volatile Dictionary<string, Dictionary<string, List<string>>> SubCommandOptions;

        public static readonly HashSet<string> ValueKeywords = new HashSet<string>(StringComparer.Ordinal) { };

        // 外部命令缓存
        private static readonly HashSet<string> _externalCommands = new(StringComparer.Ordinal);
        // 有效的主命令候选集（内部 + 外部，根据模式合并）
        private static volatile List<string> _effectiveMainCommands;
        private static readonly object _effectiveLock = new();
        private static CommandInquiryMode _lastMode;
        private static int _lastExternalHash; // 用于检测外部命令变化

        public static volatile HashSet<string> InternalCommands; // 存储所有内部命令（内置 + 插件）

        public static void InvalidateEffectiveCommands() => _effectiveMainCommands = null;

        static CommandCompleter()
        {
            // 初始化内置数据
            _builtInMainCommands = new List<string> 
            {
                "sudo",
                "shell",
                "tasks",
                "ept"
            };
            _builtInSubCommands = new Dictionary<string, List<string>>
            {
                { "sudo", new List<string> { "-l", "-u", "-v", "-k", "-b", "-p", "-h", "--" } },
                { "shell", new List<string> { "-a", "--additi", "-c", "--clear", "-r", "--reset", "-i", "--CommandInquiry", "-h", "--help" } },
                { "tasks", new List<string> { "-l", "--list", "-k", "--kill", "-d", "--detailed", "-h", "--help" } },
                { "ept", new List<string> { "update", "upgrade", "full-upgrade", "install", "remove", "purge", "search", "show", "list", "autoremove", "help" } }
            };
            _builtInOptionValues = new Dictionary<string, Dictionary<string, List<string>>>
            {
                { "shell", new Dictionary<string, List<string>>
                    {
                        { "-a", new List<string> { "path", "vpn" } },
                        { "--additi", new List<string> { "path", "vpn" } },
                        { "-c", new List<string> { "true", "false"} },
                        { "--clear", new List<string> { "true", "false"} },
                        { "-i", new List<string> { "Harvnyx", "Windows", "mix", "true" } },
                        { "--CommandInquiry", new List<string> { "Harvnyx", "Windows", "mix", "true" } }
                    }
                }
            };
            _builtInSubCommandOptions = new Dictionary<string, Dictionary<string, List<string>>>
            {
                ["ept"] = new Dictionary<string, List<string>>
                {
                    ["install"] = new List<string> { "--only-upgrade", "--no-upgrade" },
                    ["list"] = new List<string> { "--installed", "--upgradable" }
                }
            };

            // 公开字段初始为内置数据的副本
            MainCommands = new List<string>(_builtInMainCommands);
            SubCommands = CopySubCommands(_builtInSubCommands);
            OptionValues = CopyOptionValues(_builtInOptionValues);
            SubCommandOptions = CopySubCommandOptions(_builtInSubCommandOptions);

            Task.Run(async () =>
            {
                while (true)
                {
                    RefreshExternalCommands();
                    await Task.Delay(TimeSpan.FromSeconds(30)); // 每30秒刷新一次
                }
            });

            InternalCommands = new HashSet<string>(_builtInMainCommands, StringComparer.Ordinal);
        }

        private static void RefreshExternalCommands()
        {
            try
            {
                var newExternal = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
                string[] paths = pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
                string[] exts = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
                                .Split(';', StringSplitOptions.RemoveEmptyEntries);

                foreach (string dir in paths)
                {
                    if (!Directory.Exists(dir)) continue;
                    foreach (string ext in exts)
                    {
                        try
                        {
                            foreach (string file in Directory.EnumerateFiles(dir, "*" + ext))
                            {
                                string name = Path.GetFileNameWithoutExtension(file);
                                if (!string.IsNullOrEmpty(name))
                                    newExternal.Add(name);
                            }
                        }
                        catch { /* 忽略无法访问的目录 */ }
                    }
                }

                // 更新缓存并标记变化
                lock (_externalCommands)
                {
                    if (!_externalCommands.SetEquals(newExternal))
                    {
                        _externalCommands.Clear();
                        _externalCommands.UnionWith(newExternal);
                        _lastExternalHash = _externalCommands.GetHashCode(); // 简单变化标记
                        _effectiveMainCommands = null; // 使有效列表失效
                    }
                }
            }
            catch { /* 忽略扫描错误 */ }
        }

        public static List<string> GetEffectiveMainCommands()
        {
            var mode = ConfigProcessor.CommandInquiry;
            int externalHash;
            lock (_externalCommands) externalHash = _lastExternalHash;

            if (_effectiveMainCommands == null || mode != _lastMode || externalHash != _lastExternalHash)
            {
                lock (_effectiveLock)
                {
                    if (_effectiveMainCommands == null || mode != _lastMode || externalHash != _lastExternalHash)
                    {
                        var list = new List<string>();
                        // 内部命令在任何模式下都可用（因为它们是 Harvnyx 的一部分）
                        list.AddRange(MainCommands);
                        // 外部命令根据模式添加
                        if (mode == CommandInquiryMode.Windows || mode == CommandInquiryMode.Mix || mode == CommandInquiryMode.True)
                            list.AddRange(_externalCommands);
                        _effectiveMainCommands = list.Distinct().OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
                        _lastMode = mode;
                        _lastExternalHash = externalHash;
                    }
                }
            }
            return _effectiveMainCommands;
        }

        private static Dictionary<string, Dictionary<string, List<string>>> CopySubCommandOptions(
            Dictionary<string, Dictionary<string, List<string>>> source)
        {
            var copy = new Dictionary<string, Dictionary<string, List<string>>>(source.Count, StringComparer.Ordinal);
            foreach (var cmd in source)
            {
                var innerCopy = new Dictionary<string, List<string>>(cmd.Value.Count, StringComparer.Ordinal);
                foreach (var sub in cmd.Value)
                    innerCopy[sub.Key] = new List<string>(sub.Value);
                copy[cmd.Key] = innerCopy;
            }
            return copy;
        }

        private static Dictionary<string, List<string>> CopySubCommands(Dictionary<string, List<string>> source)
        {
            var copy = new Dictionary<string, List<string>>(source.Count, StringComparer.Ordinal);
            foreach (var kv in source)
                copy[kv.Key] = new List<string>(kv.Value);
            return copy;
        }

        private static Dictionary<string, Dictionary<string, List<string>>> CopyOptionValues(Dictionary<string, Dictionary<string, List<string>>> source)
        {
            var copy = new Dictionary<string, Dictionary<string, List<string>>>(source.Count, StringComparer.Ordinal);
            foreach (var cmd in source)
            {
                var innerCopy = new Dictionary<string, List<string>>(cmd.Value.Count, StringComparer.Ordinal);
                foreach (var opt in cmd.Value)
                    innerCopy[opt.Key] = new List<string>(opt.Value);
                copy[cmd.Key] = innerCopy;
            }
            return copy;
        }

        public static void UpdateFromPlugins(
            List<string> pluginMainCommands,
            Dictionary<string, List<string>> pluginSubCommands,
            Dictionary<string, Dictionary<string, List<string>>> pluginOptionValues,
            Dictionary<string, Dictionary<string, List<string>>> pluginSubCommandOptions)
        {
            // 合并主命令
            var newMain = new List<string>(_builtInMainCommands);
            newMain.AddRange(pluginMainCommands.Except(_builtInMainCommands));

            // 合并子命令
            var newSub = CopySubCommands(_builtInSubCommands);
            foreach (var kv in pluginSubCommands)
            {
                if (newSub.TryGetValue(kv.Key, out var existing))
                    existing.AddRange(kv.Value.Except(existing));
                else
                    newSub[kv.Key] = new List<string>(kv.Value);
            }

            // 合并选项值
            var newOpt = CopyOptionValues(_builtInOptionValues);
            foreach (var cmd in pluginOptionValues)
            {
                if (newOpt.TryGetValue(cmd.Key, out var existingOpt))
                {
                    foreach (var opt in cmd.Value)
                    {
                        if (existingOpt.TryGetValue(opt.Key, out var existingValues))
                            existingValues.AddRange(opt.Value.Except(existingValues));
                        else
                            existingOpt[opt.Key] = new List<string>(opt.Value);
                    }
                }
                else
                {
                    newOpt[cmd.Key] = new Dictionary<string, List<string>>(cmd.Value);
                }
            }

            // 合并 SubCommandOptions
            var newSubCmdOpt = CopySubCommandOptions(_builtInSubCommandOptions);
            foreach (var cmd in pluginSubCommandOptions)
            {
                if (newSubCmdOpt.TryGetValue(cmd.Key, out var existingSubOpt))
                {
                    foreach (var sub in cmd.Value)
                    {
                        if (existingSubOpt.TryGetValue(sub.Key, out var existingValues))
                            existingValues.AddRange(sub.Value.Except(existingValues));
                        else
                            existingSubOpt[sub.Key] = new List<string>(sub.Value);
                    }
                }
                else
                {
                    newSubCmdOpt[cmd.Key] = new Dictionary<string, List<string>>(cmd.Value);
                }
            }

            // 原子性替换
            MainCommands = newMain;
            InternalCommands = new HashSet<string>(newMain, StringComparer.Ordinal);
            SubCommands = newSub;
            OptionValues = newOpt;
            SubCommandOptions = newSubCmdOpt; // 新增
            _effectiveMainCommands = null;
        }

        public static List<string> GetSubCommandOptions(string mainCommand, string subCommand)
        {
            if (SubCommandOptions.TryGetValue(mainCommand, out var subDict) &&
                subDict.TryGetValue(subCommand, out var options))
                return options;
            return null;
        }
    }

    public static class CommandExecute
    {
        private static Dictionary<string, DateTime> _sudoTimestamps = new Dictionary<string, DateTime>();
        private static readonly TimeSpan SudoTimeout = TimeSpan.FromMinutes(5);

        // 静态字段：用于存储上一次的 CPU 采样数据
        private static readonly ConcurrentDictionary<int, (DateTime time, TimeSpan cpu)> _previousCpuTimes = new();

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool LogonUser(string lpszUsername, string lpszDomain, string lpszPassword, int dwLogonType, int dwLogonProvider, out IntPtr phToken);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        /* ========================================================== */
        /* =================== sudo [选项] <命令> ==================== */
        /* ========================================================== */
        internal static async Task<bool> HandleSudoCommand(List<string> args, CancellationToken token)
        {
            bool list = false;
            string targetUser = null;          // -u 指定的目标用户
            bool updateTimestamp = false;      // -v
            bool invalidateTimestamp = false;  // -k
            bool background = false;           // -b
            string customPrompt = null;        // -p
            List<string> commandArgs = new List<string>();

            bool endOfOptions = false;
            bool foundCommand = false;

            // 解析参数（与原代码相同）
            for (int i = 0; i < args.Count; i++)
            {
                string arg = args[i];

                if (foundCommand || endOfOptions)
                {
                    commandArgs.Add(arg);
                    continue;
                }

                if (arg.StartsWith("-"))
                {
                    if (arg == "--")
                    {
                        endOfOptions = true;
                        continue;
                    }

                    switch (arg)
                    {
                        case "-l":
                            list = true;
                            break;
                        case "-u":
                            if (i + 1 < args.Count)
                            {
                                targetUser = args[i + 1];
                                i++;
                            }
                            else
                            {
                                print.error("sudo", "缺少 -u 参数的值", print.ErrorCodes.MISSING_PARAMETER);
                                return false;
                            }
                            break;
                        case "-v":
                            updateTimestamp = true;
                            break;
                        case "-k":
                            invalidateTimestamp = true;
                            break;
                        case "-b":
                            background = true;
                            break;
                        case "-h":
                            Console.WriteLine("用法: sudo [选项] <命令>");
                            Console.WriteLine("选项:");
                            Console.WriteLine("  -l            列出当前用户可以执行的命令");
                            Console.WriteLine("  -u <用户>     以指定用户的身份执行命令");
                            Console.WriteLine("  -v            更新用户的 sudo 验证时间戳");
                            Console.WriteLine("  -k            使 sudo 验证时间戳失效");
                            Console.WriteLine("  -b            在后台执行命令");
                            Console.WriteLine("  -p <提示语>   自定义密码提示语");
                            Console.WriteLine("  --            选项结束符，后续参数原样传递给命令");
                            return true;
                        case "-p":
                            if (i + 1 < args.Count)
                            {
                                customPrompt = args[i + 1];
                                i++;
                            }
                            else
                            {
                                print.error("sudo", "缺少 -p 参数的值", print.ErrorCodes.MISSING_PARAMETER);
                                return false;
                            }
                            break;
                        default:
                            // 未知选项，停止解析，将其作为命令的一部分
                            commandArgs.Add(arg);
                            commandArgs.AddRange(args.Skip(i + 1));
                            i = args.Count;
                            foundCommand = true;
                            break;
                    }
                }
                else
                {
                    commandArgs.Add(arg);
                    foundCommand = true;
                }
            }

            // 处理只有选项没有命令的情况
            if (commandArgs.Count == 0)
            {
                if (list)
                {
                    Console.WriteLine("当前用户可执行的命令：");
                    foreach (var cmd in CommandCompleter.MainCommands)
                        Console.WriteLine($"    {cmd}");
                    return false;
                }
                if (updateTimestamp)
                {
                    string currentUser = WindowsIdentity.GetCurrent().Name;
                    lock (_sudoTimestamps)
                    {
                        _sudoTimestamps[currentUser] = DateTime.Now;
                    }
                    Console.WriteLine("sudo 验证时间戳已更新。");
                    return true;
                }
                if (invalidateTimestamp)
                {
                    string currentUser = WindowsIdentity.GetCurrent().Name;
                    lock (_sudoTimestamps)
                    {
                        _sudoTimestamps.Remove(currentUser);
                    }
                    Console.WriteLine("sudo 验证时间戳已失效。");
                    return false;
                }
                return false;
            }

            // 有命令要执行
            string execCommand = commandArgs[0];
            string execArgs = string.Join(" ", commandArgs.Skip(1));

            // 确定目标用户（如果未指定 -u，则默认是当前用户，但希望以管理员身份运行）
            // 真正的 sudo 是以 root 身份运行，Windows 上没有 root，我们用管理员替代。
            string runAsUser = targetUser ?? Environment.UserName;
            string userDomain = Environment.UserDomainName;

            // 检查是否需要验证密码
            bool needAuth = true;
            string userKey = WindowsIdentity.GetCurrent().Name;
            if (!invalidateTimestamp)
            {
                lock (_sudoTimestamps)
                {
                    if (_sudoTimestamps.TryGetValue(userKey, out DateTime lastAuth))
                    {
                        if (DateTime.Now - lastAuth < SudoTimeout)
                        {
                            needAuth = false;
                        }
                    }
                }
            }

            string password = null;
            if (needAuth)
            {
                string prompt = customPrompt ?? $"[sudo] {userKey} 的密码：";
                password = ReadPassword(prompt);
                if (string.IsNullOrEmpty(password))
                {
                    print.error("sudo", "密码不能为空", print.ErrorCodes.INCORRECT_KEY_INPURT);
                    return false;
                }

                // 验证当前用户密码
                if (!ValidateUserPassword(Environment.UserName, Environment.UserDomainName, password))
                {
                    print.error("sudo", "密码错误", print.ErrorCodes.INCORRECT_KEY_INPURT);
                    return false;
                }

                // 验证成功，记录时间戳
                lock (_sudoTimestamps)
                {
                    _sudoTimestamps[userKey] = DateTime.Now;
                }
            }
            else
            {
                // 如果不需要密码，但我们仍需要密码来启动新进程（-u 或提权），此时需从缓存获取？无法获取明文密码。
                // 因此，如果执行外部命令且需要提权，仍然需要密码。我们在这里要求输入密码（如果目标用户不同于当前用户或需要提权）。
                // 简化：只要有外部命令需要提权，就要求输入密码（如果未提供缓存密码）。
                // 由于我们没有存储明文密码，所以只能再次提示。但如果是 -u 指定了其他用户，则必须输入该用户密码。
                // 我们将在后续处理中处理密码提示。
            }

            // 如果指定了 -u 且目标用户不是当前用户，则需要获取目标用户的密码
            string targetPassword = null;
            if (!string.IsNullOrEmpty(targetUser) && !string.Equals(targetUser, Environment.UserName, StringComparison.Ordinal))
            {
                string targetPrompt = $"[sudo] {targetUser} 的密码：";
                targetPassword = ReadPassword(targetPrompt);
                if (string.IsNullOrEmpty(targetPassword))
                {
                    print.error("sudo", "目标用户密码不能为空", print.ErrorCodes.INCORRECT_KEY_INPURT);
                    return false;
                }
                if (!ValidateUserPassword(targetUser, userDomain, targetPassword))
                {
                    print.error("sudo", "目标用户密码错误", print.ErrorCodes.INCORRECT_KEY_INPURT);
                    return false;
                }
            }
            else
            {
                // 目标用户是当前用户，且需要提权（执行外部程序），则复用之前输入的当前用户密码
                targetPassword = password;
            }

            // 判断是否为内部命令（无法切换用户权限）
            bool isInternalCommand = CommandCompleter.MainCommands.Contains(execCommand);

            if (isInternalCommand)
            {
                if (!string.IsNullOrEmpty(targetUser) && !string.Equals(targetUser, Environment.UserName, StringComparison.Ordinal))
                {

                }
                else if (targetPassword != null)
                {
                    // 即便要提权，内部命令也无法提权，给出提示
                }
                string fullInput = execCommand + (string.IsNullOrEmpty(execArgs) ? "" : " " + execArgs);
                await CommandProcessor.HandleCommand(fullInput, token);
                return true;
            }

            // 处理外部命令
            // 确定要以哪个用户启动进程：如果指定了 -u 则用 targetUser，否则默认用当前用户（但希望提权到管理员？）
            // Windows 上提权需要以管理员身份运行，通常就是当前用户如果属于管理员组，则可以通过 runas 获取管理员令牌。
            // 我们可以使用 ProcessStartInfo 的 UserName 和 Password 来以目标用户启动，前提是该用户具有管理员权限。
            // 如果目标用户是当前用户，但希望以管理员身份运行，那么当前用户必须有管理员权限，并且我们使用相同的用户名/密码启动即可（实际会获得完整管理员令牌）。

            // 如果没有 -u，且 targetPassword 不为空（即用户输入了密码），则以当前用户身份但使用密码启动（相当于 runas）。
            // 如果 targetPassword 为空（即之前已验证过但没存密码），则无法启动外部进程，需要再次提示。
            if (string.IsNullOrEmpty(targetPassword))
            {
                // 没有密码，无法启动外部进程，提示
                print.error("sudo", "无法启动外部进程：缺少用户密码。请重新执行并输入密码。", print.ErrorCodes.MISSING_PARAMETER);
                return false;
            }

            // 后台执行
            if (background)
            {
                Console.WriteLine("命令已在后台执行。");
                _ = Task.Run(() => StartBackgroundProcess(execCommand, execArgs, targetUser ?? Environment.UserName, targetPassword));
                return true;
            }

            // 前台执行
            return await ExecuteCommandAsUser(execCommand, execArgs, targetUser ?? Environment.UserName, targetPassword, token);
        }

        /// <summary>
        /// 验证指定用户的密码
        /// </summary>
        private static bool ValidateUserPassword(string userName, string domain, string password)
        {
            IntPtr token;
            bool success = LogonUser(userName, domain, password, 2, 0, out token);
            if (success)
            {
                CloseHandle(token);
                return true;
            }
            return false;
        }

        /// <summary>
        /// 以指定用户身份启动进程
        /// </summary>
        private static async Task<bool> ExecuteCommandAsUser(string command, string arguments, string userName, string password, CancellationToken token)
        {
            try
            {
                // 将密码转换为 SecureString
                SecureString securePassword = new NetworkCredential("", password).SecurePassword;

                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = command,
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    UserName = userName,
                    Password = securePassword,
                    Domain = Environment.UserDomainName,
                    LoadUserProfile = true  // 加载用户配置文件，确保权限正确
                };

                using (Process proc = new Process { StartInfo = psi })
                {
                    proc.Start();
                    int taskId = TaskManager.AddTask(proc, $"{command} {arguments}");

                    using (token.Register(() => { if (!proc.HasExited) proc.Kill(); }))
                    {
                        string output = await proc.StandardOutput.ReadToEndAsync();
                        string error = await proc.StandardError.ReadToEndAsync();
                        await proc.WaitForExitAsync(token);

                        if (!string.IsNullOrEmpty(output))
                            Console.Write(output);
                        if (!string.IsNullOrEmpty(error))
                            Console.Error.Write(error);

                        return proc.ExitCode == 0;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                print.warning("sudo", "命令执行已取消。");
                return false;
            }
            catch (Exception ex)
            {
                print.error("sudo", $"执行命令失败: {ex.Message}", print.ErrorCodes.EXTERNAL_PROGRAM_FAILED);
                return false;
            }
        }

        /// <summary>
        /// 后台启动进程（以指定用户）
        /// </summary>
        private static void StartBackgroundProcess(string command, string arguments, string userName, string password)
        {
            try
            {
                SecureString securePassword = new NetworkCredential("", password).SecurePassword;
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = command,
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    UserName = userName,
                    Password = securePassword,
                    Domain = Environment.UserDomainName,
                    LoadUserProfile = true
                };
                using (Process proc = new Process { StartInfo = psi })
                {
                    proc.Start();
                    TaskManager.AddTask(proc, $"{command} {arguments}");
                    proc.WaitForExit();
                }
            }
            catch (Exception ex)
            {
                // 忽略后台错误
            }
        }

        private static string ReadPassword(string prompt)
        {
            Console.Write(prompt);
            var password = new StringBuilder();
            while (true)
            {
                var key = Console.ReadKey(true);
                if (key.Key == ConsoleKey.Enter)
                {
                    Console.WriteLine();
                    break;
                }
                else if (key.Key == ConsoleKey.Backspace && password.Length > 0)
                {
                    password.Remove(password.Length - 1, 1);
                    Console.Write("\b \b");
                }
                else if (!char.IsControl(key.KeyChar))
                {
                    password.Append(key.KeyChar);
                    Console.Write("*");
                }
            }
            return password.ToString();
        }

        /* ========================================================== */
        /* =========================== ept ========================== */
        /* ========================================================== */
        internal static async Task<bool> HandleEptCommand(List<string> args, CancellationToken token)
        {
            if (args.Count == 0)
            {
                ShowEptHelp();
                return true;
            }

            string subCommand = args[0].ToLowerInvariant();
            var subArgs = args.Skip(1).ToList();

            // 检查是否需要管理员权限（除 list/search/show 外都需要）
            bool needAdmin = !(subCommand == "list" || subCommand == "search" || subCommand == "show");
            if (needAdmin && !IsAdministrator())
            {
                print.error("ept", "此操作需要管理员权限，请使用 sudo ept ...", print.ErrorCodes.PERMISSION_DENIED);
                return false;
            }

            switch (subCommand)
            {
                case "update":
                    return await EptManager.UpdateDatabase(token);
                case "upgrade":
                    return await EptManager.UpgradePackages(false, token);
                case "full-upgrade":
                    return await EptManager.UpgradePackages(true, token);
                case "install":
                    return await EptManager.InstallPackages(subArgs, token);
                case "remove":
                    if (subArgs.Count == 0)
                    {
                        print.error("ept", "缺少包名，用法: ept remove <package>", print.ErrorCodes.MISSING_PARAMETER);
                        return false;
                    }
                    return await EptManager.RemovePackages(subArgs, false, token);
                case "purge":
                    if (subArgs.Count == 0)
                    {
                        print.error("ept", "缺少包名，用法: ept purge <package>", print.ErrorCodes.MISSING_PARAMETER);
                        return false;
                    }
                    return await EptManager.RemovePackages(subArgs, true, token);
                case "search":
                    if (subArgs.Count == 0)
                    {
                        print.error("ept", "缺少搜索关键词", print.ErrorCodes.MISSING_PARAMETER);
                        return false;
                    }
                    return await EptManager.SearchPackages(string.Join(" ", subArgs), token);
                case "show":
                    if (subArgs.Count == 0)
                    {
                        print.error("ept", "缺少包名", print.ErrorCodes.MISSING_PARAMETER);
                        return false;
                    }
                    return await EptManager.ShowPackage(subArgs[0], token);
                case "list":
                    return await EptManager.ListPackages(subArgs, token);
                case "autoremove":
                    return await EptManager.AutoRemove(token);
                case "help":
                    return await ShowEptHelp();
                default:
                    print.error("ept", $"未知子命令: {subCommand}", print.ErrorCodes.INVALID_COMMAND);
                    return false;
            }
        }

        private static async Task<bool> ShowEptHelp()
        {
            Console.WriteLine("用法: ept <子命令> [选项]");
            Console.WriteLine("包管理工具，用于安装、升级、删除扩展命令/插件。");
            Console.WriteLine();
            Console.WriteLine("子命令:");
            Console.WriteLine("  update                更新包数据库");
            Console.WriteLine("  upgrade               升级所有已安装的软件包");
            Console.WriteLine("  full-upgrade          完整升级（升级前删除需要更新的包）");
            Console.WriteLine("  install <包名>...     安装指定的软件包");
            Console.WriteLine("         [=版本]         安装指定版本");
            Console.WriteLine("         --only-upgrade  仅升级已安装的包，不安装新包");
            Console.WriteLine("         --no-upgrade    安装但不升级已存在的包");
            Console.WriteLine("  remove <包名>...      删除软件包");
            Console.WriteLine("  purge <包名>...       移除软件包及配置文件");
            Console.WriteLine("  search <关键词>       查找软件包");
            Console.WriteLine("  show <包名>           显示软件包详细信息");
            Console.WriteLine("  list --installed      列出所有已安装的包");
            Console.WriteLine("  list --upgradable     列出可更新的软件包");
            Console.WriteLine("  autoremove            清理不再使用的依赖和库文件");
            return true;
        }

        private static bool IsAdministrator()
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }

        /* ========================================================== */
        /* ========================== shell ========================= */
        /* ========================================================== */
        internal static async Task<bool> HandleShellCommand(List<string> args)
        {
            if (args.Count == 0)
            {
                // 无参数时显示当前模式
                Console.WriteLine($"当前提示符模式: {ConfigProcessor.additional}");
                return true;
            }

            for (int i = 0; i < args.Count; i++)
            {
                string arg = args[i];
                switch (arg)
                {
                    case "-a":
                    case "--additi":
                        if (i + 1 < args.Count)
                        {
                            string value = args[i + 1];
                            if (value.Equals("path", StringComparison.OrdinalIgnoreCase))
                            {
                                ConfigProcessor.additional = Mode.path;
                                ConfigProcessor.SaveConfig();
                                i++;
                            }
                            else if (value.Equals("vpn", StringComparison.OrdinalIgnoreCase))
                            {
                                ConfigProcessor.additional = Mode.vpn;
                                ConfigProcessor.SaveConfig();
                                i++;
                            }
                            else
                            {
                                print.error("shell", "无效参数值，只能为 path 或 vpn", print.ErrorCodes.INVALID_PARAMETER);
                                return false;
                            }
                        }
                        else
                        {
                            print.error("shell", "缺少 -a 参数的值", print.ErrorCodes.MISSING_PARAMETER);
                            return false;
                        }
                        break;

                    case "-c":
                    case "--clear":
                        if (i + 1 < args.Count)
                        {
                            string value = args[i + 1];
                            if (value.Equals("true", StringComparison.OrdinalIgnoreCase))
                            {
                                ConfigProcessor.ShellClear = true;
                                ConfigProcessor.SaveConfig();
                                i++;
                            }
                            else if (value.Equals("false", StringComparison.OrdinalIgnoreCase))
                            {
                                ConfigProcessor.ShellClear = false;
                                ConfigProcessor.SaveConfig();
                                i++;
                            }
                            else
                            {
                                print.error("shell", "-i 的无效参数值，只能为 true 或 false", print.ErrorCodes.INVALID_PARAMETER);
                                return false;
                            }
                        }
                        else
                        {
                            print.error("shell", "缺少 -c 参数的值", print.ErrorCodes.MISSING_PARAMETER);
                            return false;
                        }
                        break;

                    case "-i":
                    case "--CommandInquiry":
                        if (i + 1 < args.Count)
                        {
                            string value = args[i + 1];
                            if (value.Equals("Harvnyx", StringComparison.OrdinalIgnoreCase))
                            {
                                ConfigProcessor.CommandInquiry = ConfigProcessor.CommandInquiryMode.Harvnyx;
                                ConfigProcessor.SaveConfig();
                                CommandCompleter.InvalidateEffectiveCommands();
                                i++;
                            }
                            else if (value.Equals("Windows", StringComparison.OrdinalIgnoreCase))
                            {
                                ConfigProcessor.CommandInquiry = ConfigProcessor.CommandInquiryMode.Windows;
                                ConfigProcessor.SaveConfig();
                                CommandCompleter.InvalidateEffectiveCommands();
                                i++;
                            }
                            else if (value.Equals("Mix", StringComparison.OrdinalIgnoreCase))
                            {
                                ConfigProcessor.CommandInquiry = ConfigProcessor.CommandInquiryMode.Mix;
                                ConfigProcessor.SaveConfig();
                                CommandCompleter.InvalidateEffectiveCommands();
                                i++;
                            }
                            else if (value.Equals("True", StringComparison.OrdinalIgnoreCase))
                            {
                                ConfigProcessor.CommandInquiry = ConfigProcessor.CommandInquiryMode.True;
                                ConfigProcessor.SaveConfig();
                                CommandCompleter.InvalidateEffectiveCommands();
                                i++;
                            }
                            else
                            {
                                print.error("shell", "无效参数值，只能为 Harvnyx/Windows/Mix/True", print.ErrorCodes.INVALID_PARAMETER);
                                return false;
                            }
                        }
                        else
                        {
                            if (ConfigProcessor.CommandInquiry == ConfigProcessor.CommandInquiryMode.Harvnyx)
                                Console.WriteLine("当前shell处理模式: 仅使用 Harvnyx 内部命令");
                            else if (ConfigProcessor.CommandInquiry == ConfigProcessor.CommandInquiryMode.Windows)
                                Console.WriteLine("当前shell处理模式: 仅使用 Windows 外部命令");
                            else if (ConfigProcessor.CommandInquiry == ConfigProcessor.CommandInquiryMode.Mix)
                                Console.WriteLine("当前shell处理模式: 混合使用");
                            else if (ConfigProcessor.CommandInquiry == ConfigProcessor.CommandInquiryMode.True)
                                Console.WriteLine("当前shell处理模式: 遇到冲突时询问用户");
                        }
                        break;

                    case "-r":
                    case "--reset":
                        ConfigProcessor.additional = Mode.path; // 重置为默认
                        ConfigProcessor.CommandInquiry = CommandInquiryMode.Harvnyx; // 重置为默认
                        CommandCompleter.InvalidateEffectiveCommands();
                        ConfigProcessor.SaveConfig();
                        break;

                    case "-h":
                    case "--help":
                        Console.WriteLine(" shell [选项]");
                        Console.WriteLine(" 管理提示符显示模式");
                        Console.WriteLine("  -a, --additi <path|vpn>   设置提示符显示内容：path 显示路径，vpn 显示 VPN 或本机 IP");
                        Console.WriteLine("  -c, --clear <true/false>  开关清理初始化信息");
                        Console.WriteLine("  -i, --CommandInquiry <模式> 设置命令冲突处理模式：");
                        Console.WriteLine("        Harvnyx - 仅使用 Harvnyx 内部命令");
                        Console.WriteLine("        Windows - 仅使用 Windows 外部命令");
                        Console.WriteLine("        Mix     - 混合使用（Harvnyx 优先）");
                        Console.WriteLine("        True    - 遇到冲突时询问用户");
                        Console.WriteLine("  -r, --reset               重置为默认模式");
                        Console.WriteLine("  -h, --help                显示此帮助");
                        return true;

                    default:
                        print.error("shell", $"未知选项: {arg}，使用 -h 获取帮助", print.ErrorCodes.INVALID_PARAMETER);
                        return false;
                }
            }

            return true;
        }

        /* ========================================================== */
        /* ========================== tasks ========================= */
        /* ========================================================== */
        internal static async Task<bool> HandleTasksCommand(List<string> args, CancellationToken token)
        {
            if (args.Count == 0 || args[0] == "-l" || args[0] == "--list")
            {
                var tasks = TaskManager.ListTasks();
                if (tasks.Count == 0)
                {
                    Console.WriteLine("没有正在运行的任务。");
                }
                else
                {
                    Console.WriteLine("ID\t状态\t\t命令");
                    foreach (var t in tasks)
                    {
                        string status = t.Status;
                        if (t.Process != null && t.Process.HasExited) status = "Exited";
                        Console.WriteLine($"{t.Id}\t{status}\t{t.CommandLine}");
                    }
                }
                return true;
            }
            else if (args[0] == "-k" || args[0] == "--kill")
            {
                if (args.Count < 2)
                {
                    print.error("tasks", "缺少要杀死的任务ID", print.ErrorCodes.MISSING_PARAMETER);
                    return false;
                }
                if (int.TryParse(args[1], out int id))
                {
                    if (TaskManager.KillTask(id))
                        Console.WriteLine($"任务 {id} 已终止。");
                    else
                        print.error("tasks", $"找不到任务 {id} 或终止失败", print.ErrorCodes.INVALID_PARAMETER);
                    return true;
                }
                else
                {
                    print.error("tasks", "无效的任务ID", print.ErrorCodes.INVALID_PARAMETER);
                    return false;
                }
            }
            else if (args[0] == "-d" || args[0] == "--detailed")
            {
                if (args.Count >= 2)
                {
                    if (int.TryParse(args[1], out int id))
                    {
                        var task = TaskManager.GetTask(id);
                        if (task == null)
                        {
                            print.error("tasks", $"找不到任务 {id}", print.ErrorCodes.INVALID_PARAMETER);
                            return false;
                        }
                        PrintTaskDetails(task);
                    }
                    else
                    {
                        print.error("tasks", "无效的任务ID", print.ErrorCodes.INVALID_PARAMETER);
                        return false;
                    }
                }
                else
                {
                    var tasks = TaskManager.ListTasks();
                    if (tasks.Count == 0)
                        Console.WriteLine("没有正在运行的任务。");
                    else
                        foreach (var t in tasks) PrintTaskDetails(t);
                }
                return true;
            }
            else if (args[0] == "-h" || args[0] == "--help")
            {
                Console.WriteLine("用法: tasks [选项]");
                Console.WriteLine("选项:");
                Console.WriteLine("  -l, --list             列出所有任务（简略）");
                Console.WriteLine("  -k, --kill <id>        终止指定ID的任务");
                Console.WriteLine("  -d, --detailed [id]    显示指定任务的详细信息（不指定id则显示所有）");
                Console.WriteLine("  -h, --help             显示此帮助");
                return true;
            }
            else
            {
                print.error("tasks", $"未知选项: {args[0]}", print.ErrorCodes.INVALID_PARAMETER);
                return false;
            }
        }

        private static void PrintTaskDetails(TaskManager.TaskInfo task)
        {
            Console.WriteLine($"ID: {task.Id}");
            Console.WriteLine($"命令: {task.CommandLine}");
            Console.WriteLine($"启动时间: {task.StartTime}");
            Console.WriteLine($"状态: {task.Status}");
            if (task.Process != null)
            {
                Console.WriteLine($"进程ID: {task.Process.Id}");
                Console.WriteLine($"已退出: {task.Process.HasExited}");
            }
            if (task.ExitCode.HasValue)
            {
                Console.WriteLine($"退出代码: {task.ExitCode.Value}");
            }
            else if (task.Task != null)
            {
                Console.WriteLine($"任务状态: {task.Task.Status}");
            }
            Console.WriteLine();
        }
    }

    public interface ICommand
    {
        string CommandName { get; }
        string Version => null;
        List<string> GetSubCommands();
        Dictionary<string, List<string>> GetOptionValues();
        Dictionary<string, List<string>> GetSubCommandOptions() => null;
        Task<bool> ExecuteAsync(List<string> args, CancellationToken token = default);
    }
}
