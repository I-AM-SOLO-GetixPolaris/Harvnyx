using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Harvnyx
{
    public static class EptManager
    {
        // 配置路径
        private static readonly string BaseDir = AppDomain.CurrentDomain.BaseDirectory;
        private static readonly string DatabaseDir = Path.Combine(BaseDir, "ept");
        private static readonly string PackagesFile = Path.Combine(DatabaseDir, "packages.json");
        private static readonly string InstalledFile = Path.Combine(DatabaseDir, "installed.json");
        private static readonly string DownloadDir = Path.Combine(DatabaseDir, "downloads");
        private static readonly string ExpandDir = Path.Combine(BaseDir, "expand");

        // 包源配置
        private const string PackageIndexUrl = "https://raw.githubusercontent.com/I-AM-SOLO-GetixPolaris/Harvnyx/master/expand/packages.json";
        private const string PackageDownloadBase = "https://raw.githubusercontent.com/I-AM-SOLO-GetixPolaris/Harvnyx/master/expand/";
        private static readonly HttpClient _httpClient = new HttpClient();

        // 颜色定义
        private const string ColorCyan = "\u001b[38;2;125;151;255m";
        private const string ColorGray = "\u001b[38;2;128;128;128m";
        private const string ColorBold = "\u001b[1m";
        private const string ColorReset = "\u001b[0m";

        // 包信息模型
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
            if (!Directory.Exists(DatabaseDir))
                Directory.CreateDirectory(DatabaseDir);
            if (!Directory.Exists(DownloadDir))
                Directory.CreateDirectory(DownloadDir);
            if (!Directory.Exists(ExpandDir))
                Directory.CreateDirectory(ExpandDir);
        }

        private static async Task<Dictionary<string, PackageInfo>> LoadPackageDatabase()
        {
            if (!File.Exists(PackagesFile))
                return new Dictionary<string, PackageInfo>();
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
            if (!File.Exists(InstalledFile))
                return new Dictionary<string, InstalledPackage>();
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
            // 调试信息已移除，静默更新
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

            Console.WriteLine($"{ColorBold}Packages ({upgradable.Count}){ColorReset}");
            foreach (var (name, old, newPkg) in upgradable)
            {
                string versionDisplay = $"{name}-{newPkg.Version}";
                int dashIndex = versionDisplay.IndexOf('-');
                if (dashIndex >= 0)
                {
                    Console.Write(versionDisplay.Substring(0, dashIndex + 1));
                    Console.Write($"{ColorGray}{versionDisplay.Substring(dashIndex + 1)}{ColorReset}");
                }
                else
                    Console.Write(versionDisplay);
                Console.WriteLine();
            }

            long totalDownload = upgradable.Sum(u => u.latest.Size);
            long totalInstall = upgradable.Sum(u => u.latest.InstalledSize);
            Console.WriteLine($"{ColorBold}总下载大小:{ColorReset} {FormatSize(totalDownload)}");
            Console.WriteLine($"{ColorBold}总安装大小:{ColorReset} {FormatSize(totalInstall)}");

            if (!await ConfirmAction())
                return false;

            if (fullUpgrade)
            {
                foreach (var (name, old, _) in upgradable)
                {
                    if (!await UninstallPackage(name, false, token))
                    {
                        print.error("ept", $"卸载 {name} 失败，中止升级", print.ErrorCodes.EXTERNAL_PROGRAM_FAILED);
                        return false;
                    }
                }
            }

            foreach (var (name, _, newPkg) in upgradable)
            {
                if (!await DownloadAndInstall(newPkg, token))
                    return false;
            }
            return true;
        }

        public static async Task<bool> InstallPackages(List<string> args, CancellationToken token)
        {
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
            var toUpgrade = new List<(InstalledPackage old, PackageInfo newPkg)>();

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
                    if (noUpgrade)
                        continue;
                    if (onlyUpgrade && inst.Version == pkg.Version)
                        continue;
                    toUpgrade.Add((inst, pkg));
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

            var allToInstall = new List<PackageInfo>(toInstall);
            foreach (var (_, pkg) in toUpgrade)
                allToInstall.Add(pkg);

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

            int totalCount = toInstall.Count + toUpgrade.Count;
            if (totalCount == 0)
            {
                Console.WriteLine("没有需要安装的包");
                return true;
            }
            Console.WriteLine($"{ColorBold}Packages ({totalCount}){ColorReset}");
            foreach (var pkg in toInstall)
                PrintPackageLine(pkg);
            foreach (var (_, pkg) in toUpgrade)
                PrintPackageLine(pkg);

            long totalDownload = toInstall.Sum(p => p.Size) + toUpgrade.Sum(u => u.newPkg.Size);
            long totalInstallSize = toInstall.Sum(p => p.InstalledSize) + toUpgrade.Sum(u => u.newPkg.InstalledSize);
            Console.WriteLine($"{ColorBold}总下载大小:{ColorReset} {FormatSize(totalDownload)}");
            Console.WriteLine($"{ColorBold}总安装大小:{ColorReset} {FormatSize(totalInstallSize)}");

            if (!await ConfirmAction())
                return false;

            foreach (var pkg in toInstall)
            {
                if (!await DownloadAndInstall(pkg, token))
                    return false;
            }
            foreach (var (old, pkg) in toUpgrade)
            {
                if (!await UninstallPackage(pkg.Name, false, token))
                {
                    print.error("ept", $"卸载旧版 {pkg.Name} 失败", print.ErrorCodes.EXTERNAL_PROGRAM_FAILED);
                    return false;
                }
                if (!await DownloadAndInstall(pkg, token))
                    return false;
            }
            return true;
        }

        public static async Task<bool> RemovePackages(List<string> packages, bool purge, CancellationToken token)
        {
            var installed = await LoadInstalled();
            var toRemove = packages.Where(p => installed.ContainsKey(p)).ToList();
            if (toRemove.Count == 0)
            {
                print.warning("ept", "未找到指定的已安装包");
                return true;
            }

            Console.WriteLine($"{ColorBold}将要删除的包 ({toRemove.Count}){ColorReset}");
            foreach (var p in toRemove)
                Console.WriteLine($"  {p}");

            if (!await ConfirmAction($"是否继续删除{(purge ? "并清除配置" : "")}？[Y/n] "))
                return false;

            foreach (var p in toRemove)
            {
                if (!await UninstallPackage(p, purge, token))
                    return false;
            }
            return true;
        }

        public static async Task<bool> SearchPackages(string keyword, CancellationToken token)
        {
            var db = await LoadPackageDatabase();
            var results = db.Values.Where(p => p.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                                               p.Description.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                                   .ToList();
            if (results.Count == 0)
            {
                Console.WriteLine("未找到匹配的包");
                return true;
            }
            foreach (var pkg in results)
            {
                Console.Write($"{pkg.Name} ");
                Console.Write($"{ColorGray}{pkg.Version}{ColorReset}");
                Console.WriteLine($"  {pkg.Description}");
            }
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
            Console.WriteLine($"{ColorBold}名称：{ColorReset}{pkg.Name}");
            Console.WriteLine($"{ColorBold}版本：{ColorReset}{pkg.Version}");
            Console.WriteLine($"{ColorBold}描述：{ColorReset}{pkg.Description}");
            Console.WriteLine($"{ColorBold}下载大小：{ColorReset}{FormatSize(pkg.Size)}");
            Console.WriteLine($"{ColorBold}安装大小：{ColorReset}{FormatSize(pkg.InstalledSize)}");
            Console.WriteLine($"{ColorBold}依赖：{ColorReset}{(pkg.Dependencies.Count > 0 ? string.Join(", ", pkg.Dependencies) : "无")}");
            Console.WriteLine($"{ColorBold}冲突：{ColorReset}{(pkg.Conflicts.Count > 0 ? string.Join(", ", pkg.Conflicts) : "无")}");
            return true;
        }

        public static async Task<bool> ListPackages(List<string> args, CancellationToken token)
        {
            if (args.Contains("--installed"))
            {
                var installed = await LoadInstalled();
                if (installed.Count == 0)
                    Console.WriteLine("没有已安装的包");
                else
                {
                    foreach (var kv in installed)
                        Console.WriteLine($"{kv.Key} {kv.Value.Version}");
                }
                return true;
            }
            else if (args.Contains("--upgradable"))
            {
                var installed = await LoadInstalled();
                var db = await LoadPackageDatabase();
                var upgradable = installed.Where(kv => db.TryGetValue(kv.Key, out var pkg) && pkg.Version != kv.Value.Version)
                                          .Select(kv => (kv.Key, kv.Value.Version, db[kv.Key].Version));
                if (!upgradable.Any())
                    Console.WriteLine("没有可更新的包");
                else
                {
                    foreach (var (name, oldVer, newVer) in upgradable)
                        Console.WriteLine($"{name} {oldVer} -> {newVer}");
                }
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
            {
                if (db.TryGetValue(name, out var pkg))
                {
                    foreach (var dep in pkg.Dependencies)
                        allDeps.Add(dep);
                }
            }
            var orphans = installed.Keys.Where(p => !allDeps.Contains(p)).ToList();
            if (orphans.Count == 0)
            {
                Console.WriteLine("没有孤立的包");
                return true;
            }
            Console.WriteLine($"{ColorBold}孤立包 ({orphans.Count}){ColorReset}");
            foreach (var p in orphans)
                Console.WriteLine($"  {p}");
            if (!await ConfirmAction("是否删除这些孤立包？[Y/n] "))
                return false;

            foreach (var p in orphans)
                await UninstallPackage(p, false, token);
            print.success("ept", "清理完成");
            return true;
        }

        // ========== 辅助方法 ==========

        private static void PrintPackageLine(PackageInfo pkg)
        {
            string line = $"{pkg.Name}-{pkg.Version}";
            int dash = line.IndexOf('-');
            if (dash >= 0)
            {
                Console.Write(line.Substring(0, dash + 1));
                Console.Write($"{ColorGray}{line.Substring(dash + 1)}{ColorReset}");
            }
            else
                Console.Write(line);
            Console.WriteLine();
        }

        private static string FormatSize(long bytes)
        {
            if (bytes < 1024)
                return $"{bytes} B";
            if (bytes < 1024 * 1024)
                return $"{bytes / 1024.0:F2} KiB";
            if (bytes < 1024 * 1024 * 1024)
                return $"{bytes / (1024.0 * 1024):F2} MiB";
            return $"{bytes / (1024.0 * 1024 * 1024):F2} GiB";
        }

        private static async Task<bool> ConfirmAction(string prompt = ":: 是否继续安装? [Y/n] ", bool defaultYes = true)
        {
            Console.Write($"{ColorCyan}{prompt}{ColorReset}");
            var key = Console.ReadKey(true);
            Console.WriteLine();
            if (defaultYes)
                return key.Key != ConsoleKey.N;
            else
                return key.Key == ConsoleKey.Y;
        }

        private static async Task<bool> DownloadAndInstall(PackageInfo pkg, CancellationToken token)
        {
            string downloadUrl = $"{PackageDownloadBase}{pkg.Name}.dll";
            string tempFile = Path.Combine(DownloadDir, $"{pkg.Name}.dll");
            string targetFile = Path.Combine(ExpandDir, $"{pkg.Name}.dll");

            // 调试信息已移除，下载进度条会显示包名
            try
            {
                using (var response = await _httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, token))
                {
                    response.EnsureSuccessStatusCode();
                    var totalBytes = response.Content.Headers.ContentLength ?? pkg.Size;
                    using (var stream = await response.Content.ReadAsStreamAsync(token))
                    using (var fileStream = File.Create(tempFile))
                    {
                        var buffer = new byte[8192];
                        long totalRead = 0;
                        var sw = Stopwatch.StartNew();
                        int lastPercent = -1;
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
                                string timeLeft = sw.Elapsed.TotalSeconds > 0 ? $"{(totalBytes - totalRead) / speed:F0}s" : "?s";
                                int barLength = 35;
                                int filled = (int)(barLength * percent / 100);
                                string bar = new string('#', filled) + new string('-', barLength - filled);
                                Console.Write($"\r {pkg.Name}.dll... {FormatSize(totalRead)} / {FormatSize(totalBytes)} {speedStr} {timeLeft} [{bar}] {percent}%");
                            }
                        }
                        Console.WriteLine();
                    }
                }

                if (File.Exists(targetFile))
                    File.Delete(targetFile);
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
                print.success("ept", $"{pkg.Name} 安装成功");
                return true;
            }
            catch (Exception ex)
            {
                print.error("ept", $"安装 {pkg.Name} 失败: {ex.Message}", print.ErrorCodes.FILE_WRITE_FAILED);
                return false;
            }
        }

        private static async Task<bool> UninstallPackage(string name, bool purge, CancellationToken token)
        {
            var installed = await LoadInstalled();
            if (!installed.ContainsKey(name))
                return true;

            string pluginFile = Path.Combine(ExpandDir, $"{name}.dll");
            if (File.Exists(pluginFile))
            {
                try
                {
                    File.Delete(pluginFile);
                }
                catch (Exception ex)
                {
                    print.error("ept", $"删除文件 {pluginFile} 失败: {ex.Message}", print.ErrorCodes.FILE_DELETE_FAILED);
                    return false;
                }
            }
            if (purge)
            {
                string configDir = Path.Combine(BaseDir, "config", name);
                if (Directory.Exists(configDir))
                    Directory.Delete(configDir, true);
            }
            installed.Remove(name);
            await SaveInstalled(installed);
            print.success("ept", $"已删除 {(purge ? "并清除配置" : "")} {name}");
            return true;
        }
    }
}