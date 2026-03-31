using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime;
using System.Text;
using System.Threading.Tasks;


namespace Harvnyx
{
    public class Program
    {
        private static ConfigProcessor _config = new ConfigProcessor();
        private static string _lastMainPrefix = null;      // 上一次补全时的初始前缀
        private static List<string> _lastMainMatches = null; // 对应的候选命令列表
        private static int _lastMainIndex = -1;            // 当前选中的索引

        private static CancellationTokenSource _cts = new CancellationTokenSource();

        private static CancellationTokenSource _currentCommandCts; // 用于跟踪当前正在执行的命令

        public static string LastConnectedSshHost = null;

        private static List<string> _commandHistory = new List<string>();
        private static string _currentSuggestion = null;
        private static int _previousTotalLength = 0;

        public const string COLOR_MAIN = "\u001b[38;2;86;150;191m"; // #5696BF
        public const string COLOR_PARAM = "\u001b[38;2;78;201;163m"; // #4EC9A3
        public const string COLOR_SUB = "\u001b[38;2;220;216;150m"; // #DCD896
        public const string COLOR_RESET = "\u001b[0m";
        public const string COLOR_GEAY = "\u001b[38;2;128;128;128m"; // 分割符
        public const string COLOR_BOOL = "\u001b[38;2;216;160;223m"; // 布尔值


        public static async Task Main(string[] args)
        {
            Console.WriteLine("Welcome Nexus Core, system is run now.... \r\n      This software cannot run on DOS....");
            print.warning("重要", "程序即将开始初始化，请不要关闭该窗口，以免发生不可估量的错误！");

            Console.CancelKeyPress += (sender, e) =>
            {
                e.Cancel = true;
                var cts = _currentCommandCts;
                if (cts != null)
                {
                    cts.Cancel();
                    // 短暂等待，让取消传播
                    Thread.Sleep(100);
                }
                // 清空输入缓冲区
                while (Console.KeyAvailable)
                {
                    Console.ReadKey(true);
                }
            };

            ConfigProcessor.StartMonitoring();
            await Task.Delay(1000);
            CommandManager.LoadCommands();
            CommandManager.StartWatching(Environment.CurrentDirectory);
            print.success(null, "监视器加载成功");
            string cpuInfo = await ConfigProcessor.MainGetCPUAsync();
            print.success(null, "CPU监听初始化成功");
            string ramInfo = await ConfigProcessor.MainGetRAMAsync();
            print.success(null, "RAM监听初始化成功");
            await Task.Delay(1000);
            await MainLoopAsync();
        }

        public static void Clear()
        {
            if (ConfigProcessor.ShellClear == true)
                Console.Clear();
            else if (ConfigProcessor.ShellClear == false)
                return;
        }

        // 显示抬头（包括 ASCII 艺术字、版本、随机消息等）
        private static void DisplayHeader(string cpuInfo, string ramInfo)
        {
            string[] messages = {
                    $"CPU占用:{cpuInfo}",
                    $"RAM占用:{ramInfo}",
                };
            Random random = new Random();
            string randomMessage = messages[random.Next(messages.Length)];

            Clear();
            Console.WriteLine();
            Console.WriteLine(" __  __     ______     ______     __   __   __   __     __  __     __  __                                   ");
            Console.WriteLine("/\\ \\_\\ \\   /\\  __ \\   /\\  __ \\   /\\ \\ / /  /\\ \"-.\\ \\   /\\ \\_\\ \\   /\\_\\_\\_\\            ");
            Console.WriteLine("\\ \\  __ \\  \\ \\  __ \\  \\ \\  __<   \\ \\ \\'/   \\ \\ \\-.  \\  \\ \\____ \\  \\/_/\\_\\/_            ");
            Console.WriteLine(" \\ \\_\\ \\_\\  \\ \\_\\ \\_\\  \\ \\_\\ \\_\\  \\ \\__|    \\ \\_\\\\\"\\_\\  \\/\\_____\\   /\\_\\/\\_\\ ");
            Console.WriteLine("  \\/_/\\/_/   \\/_/\\/_/   \\/_/ /_/   \\/_/      \\/_/ \\/_/   \\/_____/   \\/_/\\/_/                     ");
            Console.WriteLine();
            Console.WriteLine("Harvnyx Code 是一款免费软件，如果你在使用时被要求付款请不要轻信");
            Console.WriteLine("本产品仅共学习与教育使用！任何使用本产品的违法犯罪及违法插件均与本公司无关！");
            Console.Write($"    本产品最终解释权归 NORTHTECH Group 所有 [ ");
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.Write(randomMessage);
            Console.ResetColor();
            Console.WriteLine(" ]");
            Console.WriteLine($"    版本: {System.Reflection.Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion}");
            Console.WriteLine("    Harvnyx已启动 - 按下 Ctrl + Z 重新打印该抬头");
            Console.WriteLine("============================= Harvnyx 输出消息 ==============================");
            Console.WriteLine();
        }

        public static async Task MainLoopAsync()
        {
            // 初始显示抬头
            string cpuInfo = await ConfigProcessor.MainGetCPUAsync();
            string ramInfo = await ConfigProcessor.MainGetRAMAsync();
            DisplayHeader(cpuInfo, ramInfo);

            while (true)
            {
                Prompt();
                string input = ReadLineWithTabCompletion();

                // Ctrl+Z 刷新抬头
                if (input == null)
                {
                    DisplayHeader(cpuInfo, ramInfo);
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(input))
                {
                    if (_commandHistory.Count == 0 || _commandHistory.Last() != input)
                        _commandHistory.Add(input);
                }

                var (commands, operators) = ParseCommands(input);
                if (commands.Count == 0)
                    continue;

                CancellationTokenSource cts = null;
                try
                {
                    cts = new CancellationTokenSource();
                    _currentCommandCts = cts;

                    bool lastResult = false;
                    for (int i = 0; i < commands.Count; i++)
                    {
                        string cmd = commands[i];
                        bool result;
                        try
                        {
                            result = await CommandProcessor.HandleCommand(cmd, cts.Token);
                        }
                        catch (OperationCanceledException)
                        {
                            print.success("命令", "命令已经停止执行。");
                            break;
                        }

                        if (i == 0)
                        {
                            lastResult = result;
                        }
                        else
                        {
                            string op = operators[i - 1];
                            if (op == "&&" || op == "-and")
                            {
                                if (lastResult)
                                    lastResult = await CommandProcessor.HandleCommand(cmd, cts.Token);
                                else
                                    break;
                            }
                            else if (op == "-or")
                            {
                                if (!lastResult)
                                    lastResult = await CommandProcessor.HandleCommand(cmd, cts.Token);
                                else
                                    break;
                            }
                        }
                    }
                }
                finally
                {
                    _currentCommandCts = null;
                    cts?.Dispose();

                    // 命令执行完毕（无论成功、取消或失败），清空所有残留输入
                    while (Console.KeyAvailable)
                    {
                        Console.ReadKey(true);
                    }
                }
            }
        }

        // 解析命令，返回命令列表和操作符列表
        private static (List<string> commands, List<string> operators) ParseCommands(string input)
        {
            var commands = new List<string>();
            var operators = new List<string>();
            var parts = new List<string>();
            var current = new StringBuilder();
            bool inQuotes = false;

            for (int i = 0; i < input.Length; i++)
            {
                char c = input[i];
                if (c == '"')
                {
                    inQuotes = !inQuotes;
                    current.Append(c);
                }
                else if (!inQuotes && (c == '&' || c == '-'))
                {
                    // 检测操作符
                    if (c == '&' && i + 1 < input.Length && input[i + 1] == '&')
                    {
                        // 遇到 &&
                        FlushCurrent(current, parts);
                        string cmd = string.Join(" ", parts);
                        if (!string.IsNullOrWhiteSpace(cmd))
                            commands.Add(cmd);
                        operators.Add("&&");
                        parts.Clear();
                        i++; // 跳过第二个 &
                    }
                    else if (c == '-' && i + 4 < input.Length && input.Substring(i, 4) == "-and")
                    {
                        // 遇到 -and
                        FlushCurrent(current, parts);
                        string cmd = string.Join(" ", parts);
                        if (!string.IsNullOrWhiteSpace(cmd))
                            commands.Add(cmd);
                        operators.Add("-and");
                        parts.Clear();
                        i += 3; // 跳过 "and"（因为 i 是 '-' 的位置，加3到达最后一个字符后）
                    }
                    else if (c == '-' && i + 2 < input.Length && input.Substring(i, 3) == "-or")
                    {
                        // 遇到 -or
                        FlushCurrent(current, parts);
                        string cmd = string.Join(" ", parts);
                        if (!string.IsNullOrWhiteSpace(cmd))
                            commands.Add(cmd);
                        operators.Add("-or");
                        parts.Clear();
                        i += 2; // 跳过 "or"
                    }
                    else
                        current.Append(c);
                    Debug.WriteLine("Fucking command parsing");
                }
                else
                    current.Append(c);
            }

            // 处理最后一个命令
            FlushCurrent(current, parts);
            string lastCmd = string.Join(" ", parts);
            if (!string.IsNullOrWhiteSpace(lastCmd))
                commands.Add(lastCmd);

            return (commands, operators);
        }

        private static void FlushCurrent(StringBuilder current, List<string> parts)
        {
            string token = current.ToString().Trim();
            if (!string.IsNullOrEmpty(token))
                parts.Add(token);
            current.Clear();
        }

        /*
         * Tab 补全读取函数
         */
        public static string ReadLineWithTabCompletion()
        {
            int promptLeft = Console.CursorLeft;
            int promptTop = Console.CursorTop;
            var input = new List<char>();
            _previousTotalLength = 0; // 重置总长度

            // 初始绘制
            _currentSuggestion = GetSuggestion(new string(input.ToArray()));
            UpdateInputDisplay(input, promptLeft, promptTop, _currentSuggestion);

            while (true)
            {
                var keyInfo = Console.ReadKey(true);
                if (keyInfo.Key == ConsoleKey.Enter)
                {
                    _currentSuggestion = null;
                    UpdateInputDisplay(input, promptLeft, promptTop, null);
                    Console.WriteLine();
                    Debug.WriteLine("fucking enter");
                    return new string(input.ToArray());
                }
                else if (keyInfo.Key == ConsoleKey.Tab)
                {
                    string currentText = new string(input.ToArray());
                    var parts = currentText.Split(' ');
                    var nonEmptyParts = parts.Where(p => !string.IsNullOrEmpty(p)).ToList();
                    bool endsWithSpace = currentText.EndsWith(" ");
                    string completion = null;
                    bool shouldAppendSpace = false;

                    // 判断是否需要重置主命令上下文（当用户修改了输入导致候选集失效时）
                    bool shouldReset = false;
                    if (_lastMainPrefix != null)
                    {
                        string currentWord = nonEmptyParts.Count > 0 ? nonEmptyParts[0] : "";
                        // 修改点1：使用不等于而非StartsWith
                        if (currentWord != _lastMainPrefix)
                            shouldReset = true;
                        if (nonEmptyParts.Count > 1 || endsWithSpace)
                            shouldReset = true;
                    }

                    if (shouldReset)
                    {
                        _lastMainPrefix = null;
                        _lastMainMatches = null;
                        _lastMainIndex = -1;
                    }

                    if ((nonEmptyParts.Count == 1 && !endsWithSpace) || nonEmptyParts.Count == 0)
                    {
                        string currentWord = nonEmptyParts.Count == 0 ? "" : nonEmptyParts[0];
                        // 修改点2：同样使用不等于判断是否需要重新生成候选列表
                        if (_lastMainPrefix == null || currentWord != _lastMainPrefix || _lastMainMatches == null)
                        {
                            _lastMainMatches = CommandCompleter.GetEffectiveMainCommands()
                                .Where(cmd => cmd.StartsWith(currentWord))
                                .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
                                .ToList();
                            _lastMainPrefix = currentWord;
                            _lastMainIndex = -1;
                            Debug.WriteLine("fucking Comparer");
                        }

                        if (_lastMainMatches.Count > 0)
                        {
                            if (_lastMainMatches.Contains(currentWord))
                            {
                                int idx = _lastMainMatches.IndexOf(currentWord);
                                _lastMainIndex = (idx + 1) % _lastMainMatches.Count;
                            }
                            else
                                _lastMainIndex = 0;
                            completion = _lastMainMatches[_lastMainIndex];
                        }
                    }
                    else
                    {
                        // 子命令/参数补全
                        _lastMainPrefix = null;
                        _lastMainMatches = null;
                        _lastMainIndex = -1;

                        if (nonEmptyParts.Count > 0 && nonEmptyParts[0] == "sudo")
                            (completion, shouldAppendSpace) = GetSudoCompletion(currentText, nonEmptyParts, endsWithSpace);
                        else
                            (completion, shouldAppendSpace) = GetSubCompletion(currentText);
                    }

                    if (!string.IsNullOrEmpty(completion))
                    {
                        string newText;
                        if (shouldAppendSpace)
                            newText = currentText.EndsWith(" ") ? currentText + completion : currentText + " " + completion;
                        else
                        {
                            if (currentText.EndsWith(" ") || string.IsNullOrEmpty(currentText))
                                newText = currentText + completion;
                            else
                            {
                                var words = currentText.Split(' ').Where(w => !string.IsNullOrEmpty(w)).ToList();
                                if (words.Count == 0)
                                    newText = completion;
                                else
                                {
                                    string lastWord = words.Last();
                                    int lastIndex = currentText.LastIndexOf(lastWord);
                                    newText = currentText.Substring(0, lastIndex) + completion;
                                }
                            }
                        }
                        input.Clear();
                        input.AddRange(newText);

                        // 修改点3：如果是主命令补全，更新 _lastMainPrefix 为新命令名
                        if ((nonEmptyParts.Count == 1 && !endsWithSpace) || nonEmptyParts.Count == 0)
                            // 确保 newText 是完整的命令名（不含空格）
                            _lastMainPrefix = newText;

                        _currentSuggestion = GetSuggestion(newText);
                        UpdateInputDisplay(input, promptLeft, promptTop, _currentSuggestion);
                    }
                    else { }
                }
                else if (keyInfo.Key == ConsoleKey.C && keyInfo.Modifiers == ConsoleModifiers.Control)
                {
                    // 清空当前输入行，重新开始
                    input.Clear();
                    _currentSuggestion = null;
                    UpdateInputDisplay(input, promptLeft, promptTop, null);
                    // 继续循环等待新输入
                }
                else if (keyInfo.Key == ConsoleKey.Backspace)
                {
                    if (input.Count > 0)
                    {
                        input.RemoveAt(input.Count - 1);
                        _currentSuggestion = GetSuggestion(new string(input.ToArray()));
                        UpdateInputDisplay(input, promptLeft, promptTop, _currentSuggestion);
                    }
                    else if (input.Count <= 0) { }
                }
                else if (keyInfo.Key == ConsoleKey.RightArrow)
                {
                    if (!string.IsNullOrEmpty(_currentSuggestion))
                    {
                        input.AddRange(_currentSuggestion.ToCharArray());
                        _currentSuggestion = null;
                        UpdateInputDisplay(input, promptLeft, promptTop, null);
                    }
                }
                else if (keyInfo.Key == ConsoleKey.Z && keyInfo.Modifiers == ConsoleModifiers.Control)
                {
                    Console.ResetColor();
                    return null;
                }
                else if (!char.IsControl(keyInfo.KeyChar))
                {
                    input.Add(keyInfo.KeyChar);
                    _currentSuggestion = GetSuggestion(new string(input.ToArray()));
                    UpdateInputDisplay(input, promptLeft, promptTop, _currentSuggestion);
                }
                // 其他键忽略
            }
        }

        // 重新绘制输入行（带语法高亮）
        private static void UpdateInputDisplay(List<char> inputChars, int left, int top, string suggestion = null)
        {
            Console.CursorVisible = false;
            string inputText = new string(inputChars.ToArray());

            // 计算输入文本的实际显示宽度（全角字符占2，半角占1）
            int inputDisplayWidth = GetDisplayWidth(inputText);

            // 回到提示符后起始位置
            Console.SetCursorPosition(left, top);

            // 绘制输入文本（带颜色）
            var segments = ParseInputForColoring(inputText);
            foreach (var seg in segments)
            {
                if (!string.IsNullOrEmpty(seg.ColorCode))
                    Console.Write(seg.ColorCode);
                Console.Write(seg.Text);
                if (!string.IsNullOrEmpty(seg.ColorCode))
                    Console.Write(COLOR_RESET);
            }

            int suggestionLength = suggestion?.Length ?? 0;
            int currentTotalLength = inputDisplayWidth + suggestionLength;

            // 绘制灰色建议
            if (suggestionLength > 0)
            {
                Console.Write(COLOR_GEAY);
                Console.Write(suggestion);
                Console.Write(COLOR_RESET);
            }

            // 如果新总长度小于旧总长度，清除多余字符
            if (currentTotalLength < _previousTotalLength)
            {
                int charsToClear = _previousTotalLength - currentTotalLength;
                Console.Write(new string(' ', charsToClear));
            }

            // 光标移到输入末尾（按实际显示宽度）
            Console.SetCursorPosition(left + inputDisplayWidth, top);
            Console.CursorVisible = true;

            // 更新上一次总长度（使用显示宽度）
            _previousTotalLength = currentTotalLength;
        }

        // 辅助方法：获取字符串在控制台中的显示宽度
        private static int GetDisplayWidth(string text)
        {
            int width = 0;
            foreach (char c in text)
            {
                // 判断是否为全角字符（常见中文字符、全角标点等）
                if (c >= 0x4E00 && c <= 0x9FFF || // CJK 统一表意符号
                    c >= 0x3000 && c <= 0x303F || // CJK 符号和标点
                    c >= 0xFF00 && c <= 0xFFEF)   // 全角 ASCII 兼容
                {
                    width += 2;
                }
                else
                {
                    width += 1;
                }
            }
            return width;
        }

        private static string GetSuggestion(string currentInput)
        {
            if (string.IsNullOrEmpty(currentInput))
                return null;

            // 历史记录建议（不变）
            for (int i = _commandHistory.Count - 1; i >= 0; i--)
            {
                string cmd = _commandHistory[i];
                if (cmd.StartsWith(currentInput, StringComparison.OrdinalIgnoreCase) && cmd.Length > currentInput.Length)
                    return cmd.Substring(currentInput.Length);
            }

            var parts = currentInput.Split(' ');
            var nonEmptyParts = parts.Where(p => !string.IsNullOrEmpty(p)).ToList();
            bool endsWithSpace = currentInput.EndsWith(" ");

            if (nonEmptyParts.Count == 0)
                return null;

            // 主命令阶段
            if (nonEmptyParts.Count == 1 && !endsWithSpace)
            {
                string currentWord = nonEmptyParts[0];
                var matches = CommandCompleter.GetEffectiveMainCommands().Where(cmd => cmd.StartsWith(currentWord)).ToList();
                if (matches.Count > 0 && matches[0].Length > currentWord.Length)
                    return matches[0].Substring(currentWord.Length);
                return null;
            }

            // 子命令/参数阶段
            string contextCmd = GetCurrentContextCommand(nonEmptyParts, endsWithSpace);
            if (string.IsNullOrEmpty(contextCmd))
                return null;
            if (!CommandCompleter.InternalCommands.Contains(contextCmd))
                return null;

            string lastWord = nonEmptyParts.Last();
            string prevWord = nonEmptyParts.Count >= 2 ? nonEmptyParts[nonEmptyParts.Count - 2] : null;


            if (endsWithSpace)
            {
                // 有尾随空格：检查最后一个单词是否为有值选项
                if (CommandCompleter.OptionValues.TryGetValue(contextCmd, out var optionDict) &&
                    optionDict.TryGetValue(lastWord, out var values))
                {
                    return values[0]; // 建议第一个值
                }
                else
                {
                    // 否则建议第一个子命令
                    if (CommandCompleter.SubCommands.TryGetValue(contextCmd, out var allSubs) && allSubs.Count > 0)
                        return allSubs[0];
                    return null;
                }
            }
            else
            {
                // 无尾随空格：检查上一个单词是否为有值选项
                if (prevWord != null && CommandCompleter.OptionValues.TryGetValue(contextCmd, out var optionDict) &&
                    optionDict.TryGetValue(prevWord, out var values))
                {
                    // 当前单词是值的一部分
                    if (values.Contains(lastWord))
                        return null; // 已完整
                    var matches = values.Where(v => v.StartsWith(lastWord, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (matches.Count > 0 && matches[0].Length > lastWord.Length)
                        return matches[0].Substring(lastWord.Length);
                    return null;
                }
                else
                {
                    // 当前单词是选项的一部分
                    if (!CommandCompleter.SubCommands.TryGetValue(contextCmd, out var allSubs) || allSubs.Count == 0)
                        return null;
                    if (allSubs.Contains(lastWord))
                        return null; // 已完整
                    var matches = allSubs.Where(s => s.StartsWith(lastWord, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (matches.Count > 0 && matches[0].Length > lastWord.Length)
                        return matches[0].Substring(lastWord.Length);
                    return null;
                }
            }
        }

        private static string GetCurrentContextCommand(List<string> nonEmptyParts, bool endsWithSpace)
        {
            int startIndex = nonEmptyParts.Count - 1;
            if (!endsWithSpace)
                startIndex = nonEmptyParts.Count - 2;

            for (int i = startIndex; i >= 0; i--)
            {
                string word = nonEmptyParts[i];
                if (CommandCompleter.SubCommands.ContainsKey(word))
                    return word;
            }
            return nonEmptyParts.Count > 0 ? nonEmptyParts[0] : null;
        }

        // 解析输入字符串，返回带颜色的文本段
        private static List<(string Text, string ColorCode)> ParseInputForColoring(string input)
        {
            var result = new List<(string, string)>();
            if (string.IsNullOrEmpty(input))
                return result;

            var matches = System.Text.RegularExpressions.Regex.Matches(input, @"(\s+|\S+)");
            string currentMainCmd = null;
            bool inSudoMode = false;
            string prevWord = null; // 上一个非空格单词

            foreach (System.Text.RegularExpressions.Match match in matches)
            {
                string token = match.Value;
                if (string.IsNullOrWhiteSpace(token))
                {
                    result.Add((token, null));
                    continue;
                }

                string word = token.Trim();
                string color = null;

                // 连接符
                if (word == "-and" || word == "&&" || word == "-or")
                {
                    color = COLOR_GEAY;
                    currentMainCmd = null;
                    inSudoMode = false;
                    prevWord = null; // 重置
                    result.Add((token, color));
                    continue;
                }

                if (inSudoMode)
                {
                    if (word.StartsWith("-"))
                    {
                        // sudo 自身的选项，保持原有逻辑
                        if (CommandCompleter.SubCommands.ContainsKey("sudo") &&
                            CommandCompleter.SubCommands["sudo"].Contains(word))
                        {
                            color = COLOR_PARAM;
                        }
                    }
                    else
                    {
                        // 这是实际要执行的命令
                        if (CommandCompleter.MainCommands.Contains(word))
                        {
                            color = COLOR_MAIN;
                            currentMainCmd = word;
                        }
                        else
                        {
                            color = null;
                            currentMainCmd = null;
                        }
                        inSudoMode = false;
                    }
                }
                else
                {
                    if (currentMainCmd == null)
                    {
                        // 使用 GetEffectiveMainCommands 判断是否为主命令（包含外部命令）
                        if (CommandCompleter.GetEffectiveMainCommands().Contains(word))
                        {
                            color = COLOR_MAIN;
                            currentMainCmd = word;
                            if (word == "sudo")
                                inSudoMode = true;
                        }
                    }
                    else
                    {
                        // 只有当前主命令是内部命令时，才对选项/参数进行高亮
                        bool isInternal = CommandCompleter.InternalCommands.Contains(currentMainCmd);

                        if (word.StartsWith("/") || word.StartsWith("--"))
                        {
                            if (isInternal && CommandCompleter.SubCommands.ContainsKey(currentMainCmd) &&
                                CommandCompleter.SubCommands[currentMainCmd].Contains(word))
                            {
                                color = COLOR_SUB;
                            }
                            // 如果 isInternal 为 false，color 保持 null，不赋值
                        }
                        else if (word.StartsWith("-"))
                        {
                            if (isInternal && CommandCompleter.SubCommands.ContainsKey(currentMainCmd) &&
                                CommandCompleter.SubCommands[currentMainCmd].Contains(word))
                            {
                                color = COLOR_PARAM;
                            }
                        }
                        else
                        {
                            // 值判断
                            bool isOptionValue = false;
                            if (isInternal && CommandCompleter.OptionValues.ContainsKey(currentMainCmd))
                            {
                                var options = CommandCompleter.OptionValues[currentMainCmd];
                                if (prevWord != null && options.ContainsKey(prevWord) && options[prevWord].Contains(word, StringComparer.OrdinalIgnoreCase))
                                {
                                    isOptionValue = true;
                                    color = COLOR_BOOL;
                                }
                            }
                            if (!isOptionValue)
                            {
                                if (isInternal && CommandCompleter.SubCommands.ContainsKey(currentMainCmd) &&
                                    CommandCompleter.SubCommands[currentMainCmd].Contains(word))
                                {
                                    color = COLOR_SUB;
                                }
                                else if (CommandCompleter.ValueKeywords.Contains(word))
                                {
                                    // 全局布尔值仍然可以上色，不管命令是否是内部命令（例如 true/false）
                                    color = COLOR_BOOL;
                                }
                            }
                        }
                    }
                }

                result.Add((token, color));
                if (!string.IsNullOrWhiteSpace(token))
                {
                    prevWord = word; // 更新上一个单词
                }
            }

            return result;
        }

        /*
         * 补全逻辑
         */
        private static (string completion, bool appendSpace) GetSubCompletion(string currentText)
        {
            var parts = currentText.Split(' ');
            var nonEmptyParts = parts.Where(p => !string.IsNullOrEmpty(p)).ToList();
            bool endsWithSpace = currentText.EndsWith(" ");

            if (nonEmptyParts.Count == 0)
                return (null, false);

            string contextCmd = GetCurrentContextCommand(nonEmptyParts, endsWithSpace);
            if (string.IsNullOrEmpty(contextCmd))
                return (null, false);

            if (!CommandCompleter.InternalCommands.Contains(contextCmd))
                return (null, false);

            string lastWord = nonEmptyParts.Last();
            string prevWord = nonEmptyParts.Count >= 2 ? nonEmptyParts[nonEmptyParts.Count - 2] : null;

            // 情况1：有尾随空格，准备输入下一个单词
            if (endsWithSpace)
            {
                // 检查最后一个单词（刚输入的单词）是否是有值选项
                if (CommandCompleter.OptionValues.TryGetValue(contextCmd, out var optionDict) &&
                    optionDict.TryGetValue(lastWord, out var values))
                {
                    Debug.WriteLine("сука ёбаный пробел");
                    // 有值选项，补全第一个值
                    return (values[0], false);
                }
                else
                {
                    // 普通子命令补全（基于 lastWord 循环下一个选项）
                    if (!CommandCompleter.SubCommands.TryGetValue(contextCmd, out var allSubs) || allSubs.Count == 0)
                        return (null, false);
                    int index = allSubs.FindIndex(s => s == lastWord);
                    int nextIndex = (index == -1) ? 0 : (index + 1) % allSubs.Count;
                    return (allSubs[nextIndex], false);
                }
            }
            // 情况2：无尾随空格，正在输入当前单词
            else
            {
                // 检查上一个单词是否是有值选项
                if (prevWord != null && CommandCompleter.OptionValues.TryGetValue(contextCmd, out var optionDict) &&
                    optionDict.TryGetValue(prevWord, out var values))
                {
                    // 当前单词是值的一部分
                    var matches = values.Where(v => v.StartsWith(lastWord, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (matches.Count > 0)
                    {
                        // 如果当前单词完全匹配某个值
                        if (matches.Contains(lastWord))
                        {
                            // 在当前值的列表中循环下一个值
                            int currentIndex = values.FindIndex(v => string.Equals(v, lastWord, StringComparison.OrdinalIgnoreCase));
                            int nextIndex = (currentIndex + 1) % values.Count;
                            return (values[nextIndex], false); // 直接替换当前单词为下一个值，不追加空格
                        }
                        else
                        {
                            // 部分匹配，补全第一个完整值
                            return (matches[0], false);
                        }
                    }
                    // 没有匹配的值，返回 null（或尝试补全下一个选项）
                    return (null, false);
                }
                else
                {
                    // 当前单词可能是选项的一部分
                    if (!CommandCompleter.SubCommands.TryGetValue(contextCmd, out var allSubs) || allSubs.Count == 0)
                        return (null, false);
                    var matches = allSubs.Where(s => s.StartsWith(lastWord, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (matches.Count > 0)
                    {
                        if (matches.Contains(lastWord))
                        {
                            // 当前单词已经是完整选项 → 循环下一个选项
                            int index = allSubs.FindIndex(s => s == lastWord);
                            int nextIndex = (index + 1) % allSubs.Count;
                            return (allSubs[nextIndex], false);
                        }
                        else
                        {
                            // 部分匹配，补全第一个完整选项
                            return (matches[0], false);
                        }
                    }
                    return (null, false);
                }
            }
        }

        // 专门处理 sudo 的补全
        private static (string completion, bool appendSpace) GetSudoCompletion(string currentText, List<string> parts, bool endsWithSpace)
        {
            // parts[0] == "sudo"
            int i = 1;
            string cmd = null;
            int cmdIndex = -1;
            bool foundCommand = false; // 是否已找到命令

            // 解析 sudo 的选项和参数，找到实际要执行的命令
            while (i < parts.Count)
            {
                string word = parts[i];

                if (!foundCommand && word.StartsWith("-"))
                {
                    // 选项处理：-u 和 -p 带参数，其余新选项不带参数
                    if (word == "-u" || word == "-p") // 带参数的选项
                    {
                        i += 2; // 跳过选项和它的参数
                    }
                    else if (word == "-l" || word == "-v" || word == "-k" || word == "-b") // 不带参数的选项
                    {
                        i += 1;
                    }
                    else // 未知选项，假设不带参数
                    {
                        i += 1;
                    }
                }
                else
                {
                    // 非选项，或者已找到命令后遇到任何单词（包括选项），都是命令部分
                    if (cmd == null)
                    {
                        cmd = word;
                        cmdIndex = i;
                        i++;
                        foundCommand = true; // 标记已找到命令
                    }
                    else
                    {
                        // 已找到命令，后面的都是命令的参数，停止解析
                        break;
                    }
                }
            }

            // 情况1：未找到命令（只有 sudo 和可能的选项）→ 补全 sudo 自身的子命令（选项）以及所有主命令（排除 sudo 自身）
            if (cmd == null)
            {
                // 获取 sudo 自身的选项
                var sudoOptions = CommandCompleter.SubCommands.TryGetValue("sudo", out var subs) ? subs : new List<string>();
                // 获取所有主命令，排除 sudo 本身
                var mainCommands = CommandCompleter.GetEffectiveMainCommands().Where(c => !c.Equals("sudo", StringComparison.OrdinalIgnoreCase)).ToList();
                // 合并候选集，去重
                var candidates = sudoOptions.Concat(mainCommands).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

                if (candidates.Count == 0)
                    return (null, false);

                if (endsWithSpace)
                {
                    // 光标在空格后，直接追加第一个候选
                    return (candidates[0], false);
                }
                else
                {
                    // 光标在最后一个单词上，需要循环替换
                    string lastWord = parts.Last();
                    int exactIndex = candidates.FindIndex(s => s.Equals(lastWord, StringComparison.OrdinalIgnoreCase));
                    if (exactIndex != -1)
                    {
                        // 完整匹配，使用完整列表循环
                        int nextIndex = (exactIndex + 1) % candidates.Count;
                        return (candidates[nextIndex], false);
                    }
                    else
                    {
                        // 不完全匹配，前缀匹配
                        var matches = candidates.Where(s => s.StartsWith(lastWord, StringComparison.OrdinalIgnoreCase)).ToList();
                        if (matches.Count == 0)
                            return (null, false);
                        return (matches[0], false);
                    }
                }
            }

            // 情况2：找到了命令 cmd
            if (endsWithSpace)
            {
                // 有尾随空格：进入命令的参数补全
                if (!CommandCompleter.InternalCommands.Contains(cmd)) // 如果是外部命令，不补全参数
                    return (null, false);
                if (!CommandCompleter.SubCommands.TryGetValue(cmd, out var cmdSubs) || cmdSubs.Count == 0)
                    return (null, false);
                return (cmdSubs[0], false);
            }
            else
            {
                // 无尾随空格
                string lastWord = parts.Last();
                if (lastWord == cmd)
                {
                    // 最后一个单词就是命令本身 → 仍在 sudo 的子命令中轮播（此时应使用合并候选集继续轮播，排除 sudo）
                    var sudoOptions = CommandCompleter.SubCommands.TryGetValue("sudo", out var subs) ? subs : new List<string>();
                    var mainCommands = CommandCompleter.MainCommands.Where(c => !c.Equals("sudo", StringComparison.OrdinalIgnoreCase)).ToList();
                    var candidates = sudoOptions.Concat(mainCommands).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

                    int exactIndex = candidates.FindIndex(s => s.Equals(lastWord, StringComparison.OrdinalIgnoreCase));
                    if (exactIndex != -1)
                    {
                        int nextIndex = (exactIndex + 1) % candidates.Count;
                        return (candidates[nextIndex], false);
                    }
                    else
                    {
                        var matches = candidates.Where(s => s.StartsWith(lastWord, StringComparison.OrdinalIgnoreCase)).ToList();
                        if (matches.Count == 0)
                            return (null, false);
                        return (matches[0], false);
                    }
                }
                else
                {
                    // 已经有参数了，进入命令的参数补全（循环参数）
                    if (!CommandCompleter.InternalCommands.Contains(cmd)) // 外部命令不补全参数
                        return (null, false);
                    if (!CommandCompleter.SubCommands.TryGetValue(cmd, out var cmdSubs) || cmdSubs.Count == 0)
                        return (null, false);
                    int exactIndex = cmdSubs.FindIndex(s => s == lastWord);
                    if (exactIndex != -1)
                    {
                        int nextIndex = (exactIndex + 1) % cmdSubs.Count;
                        return (cmdSubs[nextIndex], false);
                    }
                    else
                    {
                        var matches = cmdSubs.Where(s => s.StartsWith(lastWord, StringComparison.OrdinalIgnoreCase)).ToList();
                        if (matches.Count == 0)
                            return (null, false);
                        return (matches[0], false);
                    }
                }
            }
        }

        /*
         * 命令提示符
         */
        public static void Prompt()
        {
            string userName = Environment.UserName;
            string machineName = Environment.MachineName;

            string displayContent;
            if (ConfigProcessor.additional == ConfigProcessor.Mode.vpn)
            {
                // 获取 VPN IP 或本机 IP
                string ip = GetVpnOrLocalIP(); // 需要实现此方法
                displayContent = ip;
            }
            else // "path" 或其他情况默认显示路径
            {
                displayContent = ConfigProcessor.PromptDisplay();
            }

            Console.WriteLine($"┌──({userName}@{machineName})-[{displayContent}]");
            Console.Write("└─$ ");
        }

        private static string GetVpnOrLocalIP()
        {
            // 优先显示最近成功连接的 SSH 目标 IP
            if (!string.IsNullOrEmpty(LastConnectedSshHost))
                return LastConnectedSshHost;

            // 检测是否在 SSH 会话中（通过环境变量）
            string sshClient = Environment.GetEnvironmentVariable("SSH_CLIENT");
            if (!string.IsNullOrEmpty(sshClient))
            {
                string[] parts = sshClient.Split(' ');
                if (parts.Length >= 1 && IPAddress.TryParse(parts[0], out _))
                    return parts[0];
            }

            string sshConnection = Environment.GetEnvironmentVariable("SSH_CONNECTION");
            if (!string.IsNullOrEmpty(sshConnection))
            {
                string[] parts = sshConnection.Split(' ');
                if (parts.Length >= 1 && IPAddress.TryParse(parts[0], out _))
                    return parts[0];
            }

            // 原有 VPN 检测逻辑
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus == OperationalStatus.Up &&
                    (ni.Description.Contains("VPN") || ni.Name.Contains("VPN")))
                {
                    var ipProps = ni.GetIPProperties();
                    var ipv4 = ipProps.UnicastAddresses
                        .FirstOrDefault(addr => addr.Address.AddressFamily == AddressFamily.InterNetwork);
                    if (ipv4 != null)
                        return ipv4.Address.ToString();
                }
            }

            // 没有 VPN，返回本机第一个非回环 IPv4 地址
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus == OperationalStatus.Up)
                {
                    var ipProps = ni.GetIPProperties();
                    var ipv4 = ipProps.UnicastAddresses
                        .FirstOrDefault(addr => addr.Address.AddressFamily == AddressFamily.InterNetwork &&
                                                 !IPAddress.IsLoopback(addr.Address));
                    if (ipv4 != null)
                        return ipv4.Address.ToString();
                }
            }

            return "无网络";
        }
    }

    /*
     * TaskManager 来跟踪所有由 shell 启动的后台/前台进程或任务。
     */
    public static class TaskManager
    {
        private static int _nextId = 1;
        private static ConcurrentDictionary<int, TaskInfo> _tasks = new ConcurrentDictionary<int, TaskInfo>();

        public class TaskInfo
        {
            public int Id { get; set; }
            public Process Process { get; set; }
            public string CommandLine { get; set; }
            public DateTime StartTime { get; set; }
            public string Status { get; set; } // Running, Completed, Stopped, Failed
            public int? ExitCode { get; set; } // 保存退出代码，避免访问已释放的进程
            public CancellationTokenSource Cancellation { get; set; }
            public Task Task { get; set; }
        }

        public static int AddTask(Process process, string commandLine)
        {
            int id = Interlocked.Increment(ref _nextId);
            var info = new TaskInfo
            {
                Id = id,
                Process = process,
                CommandLine = commandLine,
                StartTime = DateTime.Now,
                Status = "Running"
            };
            _tasks[id] = info;

            // 启动监控任务（不等待）
            Task.Run(() =>
            {
                try
                {
                    process.WaitForExit();
                    info.ExitCode = process.ExitCode;
                    info.Status = process.ExitCode == 0 ? "Completed" : "Failed";
                }
                catch (InvalidOperationException)
                {
                    // 进程已被释放，标记为已停止
                    info.Status = "Stopped";
                }
                finally
                {
                    process.Dispose();
                    info.Process = null; // 标记为已释放
                }
            });

            return id;
        }

        public static int AddTask(CancellationTokenSource cts, Task task, string commandLine)
        {
            int id = Interlocked.Increment(ref _nextId);
            var info = new TaskInfo
            {
                Id = id,
                Cancellation = cts,
                Task = task,
                CommandLine = commandLine,
                StartTime = DateTime.Now,
                Status = "Running"
            };
            _tasks[id] = info;
            task.ContinueWith(t =>
            {
                if (t.IsCanceled) info.Status = "Stopped";
                else if (t.IsFaulted) info.Status = "Failed";
                else info.Status = "Completed";
            });
            return id;
        }

        public static bool KillTask(int id)
        {
            if (_tasks.TryGetValue(id, out var info))
            {
                if (info.Process != null && !info.Process.HasExited)
                {
                    info.Process.Kill();
                    info.Status = "Stopped";
                    return true;
                }
                else if (info.Cancellation != null)
                {
                    info.Cancellation.Cancel();
                    try
                    {
                        info.Task?.Wait(1000);
                    }
                    catch (AggregateException) { }
                    info.Status = "Stopped";
                    return true;
                }
            }
            return false;
        }

        public static List<TaskInfo> ListTasks() => _tasks.Values.ToList();

        public static TaskInfo GetTask(int id)
        {
            _tasks.TryGetValue(id, out var info);
            return info;
        }
    }

    public static class CommandManager
    {
        private class CommandMetadata
        {
            public ICommand Command { get; set; }
            public string DllPath { get; set; }
            public long FileSize { get; set; }
            public string Version { get; set; }
        }

        private static volatile Dictionary<string, CommandMetadata> _commandMetadata = new(StringComparer.Ordinal);
        private static HashSet<string> _previousCommands = new HashSet<string>(StringComparer.Ordinal);
        private static FileSystemWatcher _watcher;
        private static System.Timers.Timer _reloadTimer;
        private static readonly object _reloadLock = new();

        public static IReadOnlyDictionary<string, ICommand> Commands => _commandMetadata.ToDictionary(kv => kv.Key, kv => kv.Value.Command);

        public static void LoadCommands(string directory = ".")
        {
            var (cmdDict, mainList, subDict, optDict, cmdDetails) = ScanCommands(directory);
            UpdateCommandDictionary(cmdDict, cmdDetails);
            CommandCompleter.UpdateFromPlugins(mainList, subDict, optDict);
            _previousCommands = new HashSet<string>(cmdDict.Keys, StringComparer.OrdinalIgnoreCase);

            // 输出每个命令的详细信息
            foreach (var detail in cmdDetails)
            {
                PrintCommandDetails(detail);
            }
        }

        private static void PrintCommandDetails((string cmdName, string dllPath, long fileSize, string version) detail)
        {
            string sizeStr = detail.fileSize < 1024
                ? detail.fileSize + " B"
                : (detail.fileSize / 1024.0).ToString("F1") + " KB";

            print.success(null, $"命令 '{detail.cmdName}' 从模块 {Path.GetFileName(detail.dllPath)} 加载 {sizeStr}");
            if (!string.IsNullOrEmpty(detail.version))
                Console.WriteLine($"       版本 {detail.version} ");
        }

        private static void UpdateCommandDictionary(
            Dictionary<string, ICommand> cmdDict,
            List<(string cmdName, string dllPath, long fileSize, string version)> cmdDetails)
        {
            var newMeta = new Dictionary<string, CommandMetadata>(StringComparer.OrdinalIgnoreCase);
            foreach (var detail in cmdDetails)
            {
                newMeta[detail.cmdName] = new CommandMetadata
                {
                    Command = cmdDict[detail.cmdName],
                    DllPath = detail.dllPath,
                    FileSize = detail.fileSize,
                    Version = detail.version
                };
            }
            _commandMetadata = newMeta;
        }

        private static (Dictionary<string, ICommand> commands,
                        List<string> mainList,
                        Dictionary<string, List<string>> subDict,
                        Dictionary<string, Dictionary<string, List<string>>> optDict,
                        List<(string cmdName, string dllPath, long fileSize, string version)> cmdDetails)
            ScanCommands(string directory)
        {
            var commands = new Dictionary<string, ICommand>(StringComparer.OrdinalIgnoreCase);
            var mainList = new List<string>();
            var subDict = new Dictionary<string, List<string>>();
            var optDict = new Dictionary<string, Dictionary<string, List<string>>>();
            var cmdDetails = new List<(string cmdName, string dllPath, long fileSize, string version)>();

            string searchPath = string.IsNullOrEmpty(directory) || directory == "."
                ? Environment.CurrentDirectory
                : directory;

            if (!Directory.Exists(searchPath))
                return (commands, mainList, subDict, optDict, cmdDetails);

            foreach (string dllPath in Directory.GetFiles(searchPath, "*.dll"))
            {
                try
                {
                    Assembly asm = Assembly.LoadFrom(dllPath);
                    var fileInfo = new FileInfo(dllPath);
                    long fileSize = fileInfo.Length;
                    string assemblyVersion = asm.GetName().Version?.ToString();
                    var infoAttr = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
                    if (infoAttr != null)
                        assemblyVersion = infoAttr.InformationalVersion;

                    // 在 ScanCommands 方法中，遍历 DLL 的每个类型时：
                    foreach (Type type in asm.GetTypes())
                    {
                        if (typeof(ICommand).IsAssignableFrom(type) && !type.IsInterface && !type.IsAbstract)
                        {
                            var command = (ICommand)Activator.CreateInstance(type);
                            string cmdName = command.CommandName;

                            if (!commands.ContainsKey(cmdName))
                            {
                                commands[cmdName] = command;
                                mainList.Add(cmdName);

                                // 关键修改：只从 command.Version 获取，若无则直接设为 "-"
                                string pluginVersion = null;
                                var prop = type.GetProperty("Version", BindingFlags.Public | BindingFlags.Instance);
                                if (prop != null && prop.CanRead)
                                {
                                    pluginVersion = prop.GetValue(command) as string;
                                }
                                // 如果反射失败，回退到接口属性（可能返回默认实现）
                                if (string.IsNullOrEmpty(pluginVersion))
                                {
                                    pluginVersion = command.Version;
                                }
                                // 如果仍为空，则显示 "-"
                                if (string.IsNullOrEmpty(pluginVersion))
                                {
                                    pluginVersion = "-";
                                }

                                cmdDetails.Add((cmdName, dllPath, fileSize, pluginVersion));

                                var subs = command.GetSubCommands();
                                if (subs != null && subs.Count > 0)
                                    subDict[cmdName] = new List<string>(subs);

                                var opts = command.GetOptionValues();
                                if (opts != null && opts.Count > 0)
                                    optDict[cmdName] = new Dictionary<string, List<string>>(opts);
                            }
                        }
                    }

                }
                catch (Exception ex)
                {
                    print.error(null, $"加载命令 {Path.GetFileName(dllPath)} 失败: {ex.Message}", print.ErrorCodes.PLUGIN_LOAD_FAILED);
                }
            }

            return (commands, mainList, subDict, optDict, cmdDetails);
        }

        public static void StartWatching(string directory)
        {
            if (_watcher != null) return;

            string watchPath = string.IsNullOrEmpty(directory) || directory == "."
                ? Environment.CurrentDirectory
                : directory;

            if (!Directory.Exists(watchPath))
                Directory.CreateDirectory(watchPath);

            _watcher = new FileSystemWatcher(watchPath, "*.dll")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime,
                EnableRaisingEvents = true
            };

            _reloadTimer = new System.Timers.Timer(500) { AutoReset = false };
            _reloadTimer.Elapsed += (s, e) => ReloadCommands(watchPath);

            _watcher.Changed += (s, e) => _reloadTimer.Start();
            _watcher.Created += (s, e) => _reloadTimer.Start();
            _watcher.Deleted += (s, e) => _reloadTimer.Start();
            _watcher.Renamed += (s, e) => _reloadTimer.Start();
        }

        private static void ReloadCommands(string directory)
        {
            lock (_reloadLock)
            {
                try
                {
                    var (cmdDict, mainList, subDict, optDict, cmdDetails) = ScanCommands(directory);
                    var newCommands = new HashSet<string>(cmdDict.Keys, StringComparer.OrdinalIgnoreCase);

                    var added = newCommands.Except(_previousCommands).ToList();
                    var removed = _previousCommands.Except(newCommands).ToList();

                    // 更新
                    UpdateCommandDictionary(cmdDict, cmdDetails);
                    CommandCompleter.UpdateFromPlugins(mainList, subDict, optDict);
                    _previousCommands = newCommands;

                    // 输出新增命令的详细信息
                    if (added.Count > 0)
                    {
                        var addedDetails = cmdDetails.Where(d => added.Contains(d.cmdName, StringComparer.OrdinalIgnoreCase)).ToList();
                        foreach (var detail in addedDetails)
                        {
                            PrintCommandDetails(detail);
                        }
                    }

                    if (removed.Count > 0)
                        print.warning(null, $"移除命令: {string.Join(", ", removed)}");

                    if (added.Count == 0 && removed.Count == 0)
                        print.info(null, "命令未发生变化。");
                }
                catch (Exception ex)
                {
                    print.error("热加载", $"重新加载失败: {ex.Message}", print.ErrorCodes.PLUGIN_LOAD_FAILED);
                }
            }
        }

        public static ICommand GetPlugin(string commandName)
        {
            _commandMetadata.TryGetValue(commandName, out var meta);
            return meta?.Command;
        }
    }

    /* 
     * 自定义信息打印类
     * 使用 print 调用该类中的方法
     * 包括：
     *     print.Error (步骤, 严重程度, 错误信息, 错误代码)
     *     print.warning (步骤, 严重程度, 警告信息)
     *     print.success (步骤, 成功信息)
     *     print.info (步骤, 信息)
     * 其步骤均为可选参数，若不提供则默认为 "Error"、"Warning"、"ok"、"Tips"
     */
    public class print
    {
        // 错误代码定义
        public static class ErrorCodes
        {
            // 系统错误 (EX0001-EX0100)
            public const string SYSTEM_INIT_FAILED = "EX0001";
            public const string CONFIG_LOAD_FAILED = "EX0002";
            public const string PERMISSION_DENIED = "EX0003";
            public const string NETWORK_ERROR = "EX0004";
            public const string FILE_NOT_FOUND = "EX0005";
            public const string DIRECTORY_CREATE_FAILED = "EX0006";
            public const string FAILED_TO_REDIRECT = "EX0007";

            // 服务器错误 (EX0101-EX0200)
            public const string SERVER_START_FAILED = "EX0101";
            public const string SERVER_STOP_FAILED = "EX0102";
            public const string PORT_ALREADY_IN_USE = "EX0103";
            public const string INVALID_PORT = "EX0104";

            // 插件错误 (EX0201-EX0300)
            public const string PLUGIN_LOAD_FAILED = "EX0201";
            public const string PLUGIN_INIT_FAILED = "EX0202";
            public const string PLUGIN_COMMAND_NOT_FOUND = "EX0203";

            // 数据库错误 (EX0301-EX0400)
            public const string DB_CONNECTION_FAILED = "EX0301";
            public const string DB_QUERY_FAILED = "EX0302";

            // 日志错误 (EX0401-EX0500)
            public const string LOG_SAVE_FAILED = "EX0401";
            public const string LOG_VIEW_FAILED = "EX0402";
            public const string LOG_DELETE_FAILED = "EX0403";

            // 用户输入错误 (EX0501-EX0600)
            public const string INVALID_COMMAND = "EX0501";
            public const string MISSING_PARAMETER = "EX0502";
            public const string INVALID_PARAMETER = "EX0503";
            public const string INCORRECT_KEY_INPURT = "EX0504";

            // 外部程序错误 (EX0601-EX0700)
            public const string EXTERNAL_PROGRAM_FAILED = "EX0601";
            public const string PROCESS_START_FAILED = "EX0602";

            // 网络相关错误 (EX0701-EX0800)
            public const string URL_GENERATION_FAILED = "EX0701";
            public const string BROWSER_OPEN_FAILED = "EX0702";
            public const string CLIPBOARD_COPY_FAILED = "EX0703";

            // 文件系统错误 (EX0801-EX0900)
            public const string FILE_CREATE_FAILED = "EX0801";
            public const string FILE_WRITE_FAILED = "EX0803";
            public const string FILE_FORMAT_ERROR = "EX0900";

            // 日志特定错误 (EX0901-EX1000)
            public const string LOG_FORMAT_ERROR = "EX0901";
            public const string LOG_CLEANUP_FAILED = "EX0902";

            // 自修复错误 (EX1100-EX1200)
            public const string CHECK_UNKNOWN_ERROR = "EX1001";
            public const string CHECK_NOT_PASSED = "EX1002";
        }

        // 严重程度定义
        public static class Severity
        {
            public const string LOW = "轻微";
            public const string MEDIUM = "一般";
            public const string HIGH = "严重";
            public const string CRITICAL = "特别严重";
            public const string FATAL = "致命";
        }

        // 括号内使用指定颜色，括号外恢复默认
        private static void PrintColored(string bracket, string message, ConsoleColor bracketColor)
        {
            Console.Write("[ ");
            Console.ForegroundColor = bracketColor;
            Console.Write(bracket);
            Console.ResetColor();
            Console.Write(" ] ");
            Console.WriteLine(message);
        }

        // 错误方法：红色
        public static void error(string step, string message, string errorCode)
        {
            string bracket = string.IsNullOrEmpty(step) ? "Error" : $"Error:{step}";
            PrintColored(bracket, message, ConsoleColor.Red);
        }

        // 警告方法：黄色
        public static void warning(string step, string message)
        {
            string bracket = string.IsNullOrEmpty(step) ? "Warning" : $"Warning:{step}";
            PrintColored(bracket, message, ConsoleColor.Yellow);
        }

        // 成功方法：绿色
        public static void success(string step, string message)
        {
            string bracket = string.IsNullOrEmpty(step) ? "ok" : $"Success:{step}";
            PrintColored(bracket, message, ConsoleColor.Green);
        }

        // 信息方法：青色
        public static void info(string step, string message)
        {
            string bracket = string.IsNullOrEmpty(step) ? "Tips" : $"Tips:{step}";
            PrintColored(bracket, message, ConsoleColor.Cyan);
        }
    }
}