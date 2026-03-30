using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Net;
using System.Net.Sockets;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Management;
using System.Reflection;

namespace Harvnyx
{
    public class ConfigProcessor
    {
        private static readonly string ConfigFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.ini");

        private static string _cachedCPU = "0% 0.00 GHz";
        private static string _cachedRAM = "0MB / 0GB 0%";
        private static readonly object _lock = new object();

        /* 配置项 */
        public enum Mode
        {
            path,
            vpn
        }
        public static Mode additional { get; set; } = Mode.path;

        public enum CommandInquiryMode
        {
            Harvnyx,   // 仅使用 Harvnyx 命令
            Windows,   // 仅使用 Windows 命令
            Mix,       // 混合使用命令(Harvnyx命令优先)
            True       // 询问用户
        }

        public static CommandInquiryMode CommandInquiry { get; set; } = CommandInquiryMode.Harvnyx;

        public static bool ShellClear { get; set; } = true;

        static ConfigProcessor()
        {
            LoadConfig();
        }

        /// <summary>
        /// 从 INI 文件加载配置
        /// </summary>
        public static void LoadConfig()
        {
            try
            {
                if (!File.Exists(ConfigFilePath))
                {
                    // 文件不存在时保存默认配置
                    SaveConfig();
                    return;
                }

                var lines = File.ReadAllLines(ConfigFilePath, Encoding.UTF8);
                foreach (var line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line) || line.StartsWith(";") || line.StartsWith("#"))
                        continue;

                    var parts = line.Split('=', 2);
                    if (parts.Length != 2)
                        continue;

                    var key = parts[0].Trim();
                    var value = parts[1].Trim();

                    switch (key.ToLowerInvariant())
                    {
                        case "additional":
                            if (Enum.TryParse<Mode>(value, true, out var mode))
                                additional = mode;
                            break;
                        case "commandinquiry":
                            if (Enum.TryParse<CommandInquiryMode>(value, true, out var inquiry))
                                CommandInquiry = inquiry;
                            break;
                        case "shellclear":
                            if (bool.TryParse(value, out var shellClear))
                                ShellClear = shellClear;
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                // 加载失败时使用默认值，并记录警告
                print.warning("配置", $"加载配置文件失败: {ex.Message}，将使用默认值。");
            }
        }

        /// <summary>
        /// 保存当前配置到 INI 文件
        /// </summary>
        public static void SaveConfig()
        {
            try
            {
                var directory = Path.GetDirectoryName(ConfigFilePath);
                if (!Directory.Exists(directory))
                    Directory.CreateDirectory(directory);

                var lines = new List<string>
                {
                    "; Harvnyx 配置文件",
                    $"; 最后修改时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
                    "",
                    $"[Settings]",
                    $"additional={additional}",
                    $"CommandInquiry={CommandInquiry}",
                    $"ShellClear={ShellClear}"
                };

                File.WriteAllLines(ConfigFilePath, lines, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                print.warning("配置", $"保存配置文件失败: {ex.Message}");
            }
        }

        public static async Task<string> MainGetCPUAsync()
        {
            try
            {
                double cpuSpeedGHz = 0;
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher("SELECT CurrentClockSpeed FROM Win32_Processor"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        cpuSpeedGHz = Convert.ToDouble(obj["CurrentClockSpeed"]) / 1000.0;
                        break;
                    }
                }

                PerformanceCounter cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
                cpuCounter.NextValue();
                await Task.Delay(100);
                float cpuUsage = cpuCounter.NextValue();

                return $"{cpuUsage:F0}% {cpuSpeedGHz:F2} GHz";
            }
            catch (Exception ex)
            {
                return $"CPU获取失败: {ex.Message}";
            }
        }
        public static async Task<string> MainGetRAMAsync()
        {
            try
            {
                ulong totalMemoryBytes = 0;
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        totalMemoryBytes = Convert.ToUInt64(obj["TotalPhysicalMemory"]);
                        break;
                    }
                }
                if (totalMemoryBytes == 0)
                    return "无法获取总内存";

                float availableRAM_MB;
                using (PerformanceCounter ramCounter = new PerformanceCounter("Memory", "Available MBytes"))
                {
                    ramCounter.NextValue();
                    await Task.Delay(100);
                    availableRAM_MB = ramCounter.NextValue();
                }

                // 3. 后续计算同上
                ulong availableMemoryBytes = (ulong)(availableRAM_MB * 1024 * 1024);
                ulong usedMemoryBytes = totalMemoryBytes - availableMemoryBytes;

                double usedPercent = (double)usedMemoryBytes / totalMemoryBytes * 100;
                double totalGB = totalMemoryBytes / (1024.0 * 1024 * 1024);

                const ulong fiveGB = 5UL * 1024 * 1024 * 1024;
                string usedPart;
                if (usedMemoryBytes >= fiveGB)
                {
                    double usedGB = usedMemoryBytes / (1024.0 * 1024 * 1024);
                    usedPart = $"{usedGB:F2}GB";
                }
                else
                {
                    double usedMB = usedMemoryBytes / (1024.0 * 1024);
                    usedPart = $"{(long)usedMB}MB";
                }

                return $"{usedPart}/{totalGB:F2}GB ({usedPercent:F1}%)";
            }
            catch (Exception ex)
            {
                return $"内存获取失败: {ex.Message}";
            }
        }

        // 启动后台更新任务
        public static void StartMonitoring()
        {
            Task.Run(async () =>
            {
                while (true)
                {
                    try
                    {
                        // 更新 CPU（异步获取，内部有延迟，但在这里执行，不影响主线程）
                        string cpu = await MainGetCPUAsync();
                        // 更新 RAM
                        string ram = await MainGetRAMAsync();

                        lock (_lock)
                        {
                            _cachedCPU = cpu;
                            _cachedRAM = ram;
                        }
                    }
                    catch (Exception ex)
                    {
                        // 记录错误，但保持缓存不变
                    }

                    // 每隔 2 秒更新一次（可调整）
                    await Task.Delay(2000);
                }
            });
        }

        // 获取缓存的 CPU 信息（立即返回）
        public static string GetCachedCPU()
        {
            lock (_lock)
                return _cachedCPU;
        }

        // 获取缓存的 RAM 信息（立即返回）
        public static string GetCachedRAM()
        {
            lock (_lock)
                return _cachedRAM;
        }

        public static string PromptDisplay()
        {
            string currentDir = Environment.CurrentDirectory;
            string exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

            if (string.Equals(currentDir, exeDir, StringComparison.OrdinalIgnoreCase))
                return "~";

            string root = Path.GetPathRoot(currentDir);
            if (!string.IsNullOrEmpty(root) && (string.Equals(currentDir, exeDir, StringComparison.OrdinalIgnoreCase)))
                return root.ToLowerInvariant();

            string[] partsDir = currentDir.Split(new char[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
            if (partsDir.Length >= 6)
                return $"..\\{partsDir[partsDir.Length - 2]}\\{partsDir[partsDir.Length - 1]}";
            else
                return currentDir;
        }
    }
}