using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Harvnyx
{
    public static class EptManager
    {
        private static readonly string BaseDir = AppDomain.CurrentDomain.BaseDirectory;
        private static readonly string DatabaseDir = Path.Combine(BaseDir, "ept");
        private static readonly string PackagesFile = Path.Combine(DatabaseDir, "packages.json");
        private static readonly string InstalledFile = Path.Combine(DatabaseDir, "installed.json");
        private static readonly string DownloadDir = Path.Combine(DatabaseDir, "downloads");
        private static readonly string ExpandDir = Path.Combine(BaseDir, "expand");

        private const string PackageIndexUrl = "https://raw.githubusercontent.com/I-AM-SOLO-GetixPolaris/Harvnyx/master/expand/packages.json";
        private const string PackageDownloadBase = "https://raw.githubusercontent.com/I-AM-SOLO-GetixPolaris/Harvnyx/master/expand/";
        private static readonly HttpClient _httpClient = new HttpClient();

        // 颜色定义：仅加粗、灰色（版本号）、::专用色
        private const string BOLD = "\u001b[1m";
        private const string GRAY = "\u001b[38;2;128;128;128m";
        private const string PROMPT_COLOR = "\u001b[38;2;125;151;255m"; // #7d97ff
        private const string RESET = "\u001b[0m";

        public class PackageInfo
        {
            public string Name { get; set; }
            public string Version { get; set; }
            public string Description { get; set; }
            public long Size { get; set; }
            public long InstalledSize { get; set; }
            public List<string> Dependencies { get; set; } = new List<string>();
            public List<string> Conflicts { get; set; } = new List<string>();
        }

        public class InstalledPackage
        {
            public string Name { get; set; }
            public string Version { get; set; }
            public DateTime InstallDate { get; set; }
            public bool IsConfigKept { get; set; }
        }

        static EptManager()
        {
            if (!Directory.Exists(DatabaseDir)) Directory.CreateDirectory(DatabaseDir);
            if (!Directory.Exists(DownloadDir)) Directory.CreateDirectory(DownloadDir);
            if (!Directory.Exists(ExpandDir)) Directory.CreateDirectory(ExpandDir);
        }

        private static async Task<Dictionary<string, PackageInfo>> LoadPackageDatabase()
        {
            if (!File.Exists(PackagesFile)) return new Dictionary<string, PackageInfo>();
            var json = await File.ReadAllTextAsync(PackagesFile);
            return JsonSerializer.Deserialize<Dictionary<string, PackageInfo>>(json) ?? new Dictionary<string, PackageInfo>();
        }

        private static async Task SavePackageDatabase(Dictionary<string, PackageInfo> db)
        {
            var json = JsonSerializer.Serialize(db, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(PackagesFile, json);
        }

        private static async Task<Dictionary<string, InstalledPackage>> LoadInstalled()
        {
            if (!File.Exists(InstalledFile)) return new Dictionary<string, InstalledPackage>();
            var json = await File.ReadAllTextAsync(InstalledFile);
            return JsonSerializer.Deserialize<Dictionary<string, InstalledPackage>>(json) ?? new Dictionary<string, InstalledPackage>();
        }

        private static async Task SaveInstalled(Dictionary<string, InstalledPackage> installed)
        {
            var json = JsonSerializer.Serialize(installed, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(InstalledFile, json);
        }

        // ========== 子命令实现 ==========

        public static async Task<bool> UpdateDatabase(CancellationToken token)
        {
            try
            {
                var response = await _httpClient.GetAsync(PackageIndexUrl, token);
                if (!response.IsSuccessStatusCode)
                {
                    print.error("ept", $"无法获取包索引，HTTP {response.StatusCode}", print.ErrorCodes.NETWORK_ERROR);
                    return false;
                }
                var json = await response.Content.ReadAsStringAsync(token);
                var db = JsonSerializer.Deserialize<Dictionary<string, PackageInfo>>(json);
                if (db == null)
                {
                    print.error("ept", "包索引格式无效", print.ErrorCodes.FILE_FORMAT_ERROR);
                    return false;
                }
                await SavePackageDatabase(db);
                print.success("ept", "包数据库更新成功");
                return true;
            }
            catch (Exception ex)
            {
                print.error("ept", $"更新失败: {ex.Message}", print.ErrorCodes.NETWORK_ERROR);
                return false;
            }
        }

        public static async Task<bool> UpgradePackages(bool fullUpgrade, CancellationToken token)
        {
            var installed = await LoadInstalled();
            var db = await LoadPackageDatabase();
            var upgradable = new List<(string name, InstalledPackage installed, PackageInfo latest)>();
            foreach (var kv in installed)
            {
                if (db.TryGetValue(kv.Key, out var pkg) && pkg.Version != kv.Value.Version)
                    upgradable.Add((kv.Key, kv.Value, pkg));
            }
            if (upgradable.Count == 0)
            {
                Console.WriteLine("所有软件包已是最新版本");
                return true;
            }

            Console.WriteLine("正在准备包...");
            Console.WriteLine("正在整理依赖关系...");
            print.success(null, "检测到包依赖周期");
            Console.WriteLine();

            // 包列表
            Console.Write($"{BOLD}Packages ({upgradable.Count}){RESET} ");
            var first = upgradable.First();
            PrintPackageName(first.name, first.latest.Version, true);
            Console.WriteLine();
            foreach (var (name, _, pkg) in upgradable.Skip(1))
            {
                Console.Write("             ");
                PrintPackageName(name, pkg.Version, false);
                Console.WriteLine();
            }
            Console.WriteLine();

            long totalDownload = upgradable.Sum(u => u.latest.Size);
            long totalInstall = upgradable.Sum(u => u.latest.InstalledSize);
            PrintSizeSummary(totalDownload, totalInstall);

            if (!await ConfirmAction()) return false;

            PrintColoredPrompt(":: Retrieving packages...");
            foreach (var (name, _, pkg) in upgradable)
            {
                if (!await DownloadAndInstall(pkg, token)) return false;
            }
            print.success(null, "Done");
            return true;
        }

        public static async Task<bool> InstallPackages(List<string> args, CancellationToken token)
        {
            Console.WriteLine("正在准备包...");
            Console.WriteLine("正在整理依赖关系...");
            print.success(null, "检测到包依赖周期");
            Console.WriteLine();

            bool onlyUpgrade = args.Contains("--only-upgrade");
            bool noUpgrade = args.Contains("--no-upgrade");
            var packageSpecs = args.Where(a => !a.StartsWith("--")).ToList();
            if (packageSpecs.Count == 0)
            {
                print.error("ept", "未指定要安装的包", print.ErrorCodes.MISSING_PARAMETER);
                return false;
            }

            var db = await LoadPackageDatabase();
            var installed = await LoadInstalled();
            var toInstall = new List<PackageInfo>();
            var toReinstall = new List<PackageInfo>();   // 已安装且版本相同
            var toUpgrade = new List<PackageInfo>();     // 已安装但版本不同

            foreach (var spec in packageSpecs)
            {
                string name = spec;
                string versionReq = null;
                if (spec.Contains('='))
                {
                    var parts = spec.Split('=', 2);
                    name = parts[0];
                    versionReq = parts[1];
                }

                if (!db.TryGetValue(name, out var pkg))
                {
                    print.error("ept", $"未找到包: {name}", print.ErrorCodes.INVALID_PARAMETER);
                    return false;
                }
                if (versionReq != null && pkg.Version != versionReq)
                {
                    print.error("ept", $"包 {name} 版本 {versionReq} 不存在", print.ErrorCodes.INVALID_PARAMETER);
                    return false;
                }

                if (installed.TryGetValue(name, out var inst))
                {
                    if (noUpgrade) continue;
                    if (onlyUpgrade && inst.Version == pkg.Version) continue;
                    if (inst.Version == pkg.Version)
                        toReinstall.Add(pkg);
                    else
                        toUpgrade.Add(pkg);
                }
                else
                {
                    if (onlyUpgrade)
                    {
                        print.warning("ept", $"包 {name} 未安装，--only-upgrade 模式下跳过");
                        continue;
                    }
                    toInstall.Add(pkg);
                }
            }

            // 所有要操作的包
            var allToInstall = new List<PackageInfo>();
            allToInstall.AddRange(toInstall);
            allToInstall.AddRange(toUpgrade);
            allToInstall.AddRange(toReinstall);

            int totalCount = toInstall.Count + toUpgrade.Count + toReinstall.Count;
            if (totalCount == 0)
            {
                Console.WriteLine("没有需要安装的包");
                return true;
            }

            // 依赖检查（简单警告）
            var missingDeps = new List<string>();
            foreach (var pkg in allToInstall)
            {
                foreach (var dep in pkg.Dependencies)
                {
                    if (!installed.ContainsKey(dep) && !allToInstall.Any(p => p.Name == dep))
                        missingDeps.Add(dep);
                }
            }
            if (missingDeps.Count > 0)
            {
                print.warning("ept", $"缺少依赖: {string.Join(", ", missingDeps)}");
                if (!await ConfirmAction("是否继续安装？[y/N] ", false))
                    return false;
            }

            // 情况1：只有重装（没有新安装和升级）
            bool onlyReinstall = (toInstall.Count == 0 && toUpgrade.Count == 0 && toReinstall.Count > 0);
            if (onlyReinstall)
            {
                // 逐个询问重装
                foreach (var pkg in toReinstall)
                {
                    print.success(null, $"包'{pkg.Name}-{pkg.Version}'已经安装。");
                    if (!await ConfirmAction(":: Proceed with reinstall? [Y/n] ", true))
                        return false;
                }
                // 直接开始重装，不显示包列表和总大小
                PrintColoredPrompt(":: Retrieving packages...");
                foreach (var pkg in toReinstall)
                {
                    if (!await DownloadAndInstall(pkg, token))
                        return false;
                }
                print.success(null, "Done");
                return true;
            }

            // 情况2：有安装或升级操作（可能包含重装）
            // 显示包列表和总大小，一次性确认（不再单独询问每个重装包）
            Console.Write($"{BOLD}Packages ({totalCount}){RESET} ");
            if (allToInstall.Count > 0)
            {
                var first = allToInstall[0];
                PrintPackageName(first.Name, first.Version, true);
                Console.WriteLine();
                foreach (var pkg in allToInstall.Skip(1))
                {
                    Console.Write("             ");
                    PrintPackageName(pkg.Name, pkg.Version, false);
                    Console.WriteLine();
                }
            }
            Console.WriteLine();

            long totalDownload = allToInstall.Sum(p => p.Size);
            long totalInstallSize = allToInstall.Sum(p => p.InstalledSize);
            PrintSizeSummary(totalDownload, totalInstallSize);

            if (!await ConfirmAction()) return false;

            PrintColoredPrompt(":: Retrieving packages...");
            foreach (var pkg in toInstall)
                if (!await DownloadAndInstall(pkg, token)) return false;
            foreach (var pkg in toUpgrade)
            {
                if (!await UninstallPackage(pkg.Name, false, token)) return false;
                if (!await DownloadAndInstall(pkg, token)) return false;
            }
            foreach (var pkg in toReinstall)
            {
                if (!await UninstallPackage(pkg.Name, false, token)) return false;
                if (!await DownloadAndInstall(pkg, token)) return false;
            }

            print.success(null, "Done");
            return true;
        }

        public static async Task<bool> RemovePackages(List<string> packages, bool purge, CancellationToken token)
        {
            Console.WriteLine("移除包...\n");

            var installed = await LoadInstalled();
            var toRemove = packages.Where(p => installed.ContainsKey(p)).ToList();
            if (toRemove.Count == 0)
            {
                print.warning("ept", "未找到指定的已安装包");
                return true;
            }

            // 包列表
            Console.Write($"{BOLD}Packages ({toRemove.Count}){RESET} ");
            var first = toRemove[0];
            var firstPkg = installed[first];
            PrintPackageName(first, firstPkg.Version, true);
            Console.WriteLine();
            foreach (var p in toRemove.Skip(1))
            {
                var pkg = installed[p];
                Console.Write("             ");
                PrintPackageName(p, pkg.Version, false);
                Console.WriteLine();
            }
            Console.WriteLine();

            if (!await ConfirmAction(":: Proceed with uninstall? [Y/n] ", true)) return false;

            PrintColoredPrompt(":: Retrieving packages...");
            foreach (var p in toRemove)
            {
                var pkg = installed[p];
                Console.Write($" {p}-{pkg.Version} 准备就绪");
                if (await UninstallPackage(p, purge, token))
                    Console.WriteLine();
                else
                    return false;
            }
            print.success(null, "Done");
            return true;
        }

        public static async Task<bool> SearchPackages(string keyword, CancellationToken token)
        {
            var db = await LoadPackageDatabase();
            var results = db.Values.Where(p => p.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                                               p.Description.Contains(keyword, StringComparison.OrdinalIgnoreCase)).ToList();
            if (results.Count == 0)
            {
                Console.WriteLine("未找到匹配的包");
                return true;
            }
            foreach (var pkg in results)
                Console.WriteLine($"{pkg.Name} {pkg.Version}  {pkg.Description}");
            return true;
        }

        public static async Task<bool> ShowPackage(string packageName, CancellationToken token)
        {
            var db = await LoadPackageDatabase();
            if (!db.TryGetValue(packageName, out var pkg))
            {
                print.error("ept", $"包 {packageName} 不存在", print.ErrorCodes.INVALID_PARAMETER);
                return false;
            }
            Console.WriteLine($"名称：{pkg.Name}");
            Console.WriteLine($"版本：{pkg.Version}");
            Console.WriteLine($"描述：{pkg.Description}");
            Console.WriteLine($"下载大小：{FormatSize(pkg.Size)}");
            Console.WriteLine($"安装大小：{FormatSize(pkg.InstalledSize)}");
            Console.WriteLine($"依赖：{(pkg.Dependencies.Count > 0 ? string.Join(", ", pkg.Dependencies) : "无")}");
            Console.WriteLine($"冲突：{(pkg.Conflicts.Count > 0 ? string.Join(", ", pkg.Conflicts) : "无")}");
            return true;
        }

        public static async Task<bool> ListPackages(List<string> args, CancellationToken token)
        {
            if (args.Contains("--installed"))
            {
                var installed = await LoadInstalled();
                if (installed.Count == 0) Console.WriteLine("没有已安装的包");
                else foreach (var kv in installed) Console.WriteLine($"{kv.Key} {kv.Value.Version}");
                return true;
            }
            else if (args.Contains("--upgradable"))
            {
                var installed = await LoadInstalled();
                var db = await LoadPackageDatabase();
                var upgradable = installed.Where(kv => db.TryGetValue(kv.Key, out var pkg) && pkg.Version != kv.Value.Version)
                                          .Select(kv => (kv.Key, kv.Value.Version, db[kv.Key].Version));
                if (!upgradable.Any()) Console.WriteLine("没有可更新的包");
                else foreach (var (name, oldVer, newVer) in upgradable) Console.WriteLine($"{name} {oldVer} -> {newVer}");
                return true;
            }
            else
            {
                print.error("ept", "请指定 --installed 或 --upgradable", print.ErrorCodes.INVALID_PARAMETER);
                return false;
            }
        }

        public static async Task<bool> AutoRemove(CancellationToken token)
        {
            var installed = await LoadInstalled();
            var db = await LoadPackageDatabase();
            var allDeps = new HashSet<string>();
            foreach (var name in installed.Keys)
                if (db.TryGetValue(name, out var pkg))
                    foreach (var dep in pkg.Dependencies) allDeps.Add(dep);
            var orphans = installed.Keys.Where(p => !allDeps.Contains(p)).ToList();
            if (orphans.Count == 0)
            {
                Console.WriteLine("没有孤立的包");
                return true;
            }
            Console.WriteLine($"孤立包 ({orphans.Count})");
            foreach (var p in orphans) Console.WriteLine($"  {p}");
            if (!await ConfirmAction("是否删除这些孤立包？[Y/n] ")) return false;
            foreach (var p in orphans) await UninstallPackage(p, false, token);
            print.success("ept", "清理完成");
            return true;
        }

        // ========== 辅助方法 ==========

        private static void PrintPackageName(string name, string version, bool isFirst)
        {
            int dashIndex = name.IndexOf('-');
            if (dashIndex >= 0)
            {
                Console.Write(name.Substring(0, dashIndex + 1));
                Console.Write($"{GRAY}{name.Substring(dashIndex + 1)}{RESET}");
            }
            else
            {
                Console.Write(name);
            }
            Console.Write($"-{version}");
        }

        private static void PrintSizeSummary(long totalDownload, long totalInstall)
        {
            string downloadStr = FormatSize(totalDownload);
            string installStr = FormatSize(totalInstall);
            Console.Write($"{BOLD}总下载大小:{RESET} ");
            Console.WriteLine($"{downloadStr,15}");
            Console.Write($"{BOLD}总安装大小:{RESET} ");
            Console.WriteLine($"{installStr,15}");
            Console.WriteLine();
        }

        private static string FormatSize(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F2} KiB";
            if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F2} MiB";
            return $"{bytes / (1024.0 * 1024 * 1024):F2} GiB";
        }

        // 确认提示：使用 ReadLine()
        private static async Task<bool> ConfirmAction(string prompt = ":: Proceed with installation? [Y/n] ", bool defaultYes = true)
        {
            PrintColoredPrompt(prompt);
            string input = (await Task.Run(() => Console.ReadLine()))?.Trim().ToLower() ?? "";
            if (string.IsNullOrEmpty(input)) return defaultYes;
            return input == "y" || input == "yes";
        }

        // 输出带颜色 :: 的提示行（仅两个冒号着色）
        private static void PrintColoredPrompt(string message)
        {
            if (message.StartsWith("::"))
            {
                Console.Write($"{PROMPT_COLOR}::{RESET}");
                Console.Write(message.Substring(2));
            }
            else
            {
                Console.WriteLine(message);
            }
        }

        private static async Task<bool> DownloadAndInstall(PackageInfo pkg, CancellationToken token)
        {
            string downloadUrl = $"{PackageDownloadBase}{pkg.Name}.dll";
            string tempFile = Path.Combine(DownloadDir, $"{pkg.Name}.dll");
            string targetFile = Path.Combine(ExpandDir, $"{pkg.Name}.dll");
            string displayName = $"{pkg.Name}-{pkg.Version}";

            try
            {
                Console.WriteLine($" {displayName} 准备就绪");

                using (var response = await _httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, token))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        print.error("ept", $"包'{displayName}'无法下载，返回代码{(int)response.StatusCode}", print.ErrorCodes.NETWORK_ERROR);
                        return false;
                    }

                    var totalBytes = response.Content.Headers.ContentLength ?? pkg.Size;
                    using (var stream = await response.Content.ReadAsStreamAsync(token))
                    using (var fileStream = File.Create(tempFile))
                    {
                        var buffer = new byte[8192];
                        long totalRead = 0;
                        var sw = Stopwatch.StartNew();
                        int lastPercent = -1;
                        string lastLine = null;

                        while (true)
                        {
                            int read = await stream.ReadAsync(buffer, 0, buffer.Length, token);
                            if (read == 0) break;
                            await fileStream.WriteAsync(buffer, 0, read, token);
                            totalRead += read;
                            int percent = (int)(totalRead * 100 / totalBytes);
                            if (percent != lastPercent)
                            {
                                lastPercent = percent;
                                double speed = totalRead / sw.Elapsed.TotalSeconds;
                                string speedStr = speed > 0 ? FormatSize((long)speed) + "/s" : "0 B/s";
                                string timeLeft = sw.Elapsed.TotalSeconds > 0 ? $"{TimeSpan.FromSeconds((totalBytes - totalRead) / speed):mm\\:ss}" : "--:--";
                                const int barLength = 35;
                                int filled = (int)(barLength * percent / 100);
                                string bar = new string('#', filled) + new string('-', barLength - filled);

                                // 格式： 包名-版本...   已下载大小   速度 耗时 [进度条] 百分比%
                                string line = $" {displayName}... {FormatSize(totalRead),8} {speedStr,8} {timeLeft} [{bar}] {percent}%";
                                if (lastLine != null && line.Length < lastLine.Length)
                                    line += new string(' ', lastLine.Length - line.Length);
                                Console.Write("\r" + line);
                                lastLine = line;
                            }
                        }
                        Console.WriteLine();
                    }
                }

                if (File.Exists(targetFile)) File.Delete(targetFile);
                File.Move(tempFile, targetFile);

                var installed = await LoadInstalled();
                installed[pkg.Name] = new InstalledPackage
                {
                    Name = pkg.Name,
                    Version = pkg.Version,
                    InstallDate = DateTime.Now,
                    IsConfigKept = true
                };
                await SaveInstalled(installed);
                return true;
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                print.error("ept", $"安装 {displayName} 失败: {ex.Message}", print.ErrorCodes.FILE_WRITE_FAILED);
                return false;
            }
        }

        private static async Task<bool> UninstallPackage(string name, bool purge, CancellationToken token)
        {
            var installed = await LoadInstalled();
            if (!installed.ContainsKey(name)) return true;

            string pluginFile = Path.Combine(ExpandDir, $"{name}.dll");
            if (File.Exists(pluginFile))
            {
                try { File.Delete(pluginFile); }
                catch (Exception ex)
                {
                    print.error("ept", $"删除文件 {pluginFile} 失败: {ex.Message}", print.ErrorCodes.FILE_DELETE_FAILED);
                    return false;
                }
            }
            if (purge)
            {
                string configDir = Path.Combine(BaseDir, "config", name);
                if (Directory.Exists(configDir)) Directory.Delete(configDir, true);
            }
            installed.Remove(name);
            await SaveInstalled(installed);
            return true;
        }
    }
}