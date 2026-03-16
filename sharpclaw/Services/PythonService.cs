using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace sharpclaw.Services;

/// <summary>
/// Python 服务 - 直接通过 Process 调用 Python 执行代码
/// 完全 AOT 兼容
/// </summary>
public class PythonService : IDisposable
{
    private string? _pythonPath;
    private bool _isInitialized;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private string? _workingDirectory = null;

    /// <summary>
    /// 初始化 Python 服务（查找 Python 路径）
    /// </summary>
    public void Init(string workingDirectory)
    {
        if (_isInitialized)
            return;

        _workingDirectory = workingDirectory;

        _pythonPath = FindPythonPath();

        if (string.IsNullOrEmpty(_pythonPath))
        {
            Console.WriteLine("[PythonService] 警告: 未找到 Python，将在执行时再次尝试查找");
        }
        else
        {
            Console.WriteLine($"[PythonService] Python 路径: {_pythonPath}");
        }

        _isInitialized = true;
    }

    [Description("执行Python代码。代码应从数据目录读取@文件名对应文件。")]
    public string RunPython(
        [Description("python代码，建议打印出字符串，方便接收信息")] string code,
        [Description("执行这个代码的目的，需要达成什么效果（必填）")] string purpose,
        [Description("Working directory (optional)")] string workingDirectory = "")
    {
        if (string.IsNullOrWhiteSpace(code))
            return "ERROR: empty code";

        var result = ExecutePythonInternal(code, _workingDirectory ?? workingDirectory);

        if (result.success)
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrEmpty(result.data))
                sb.Append(result.data);
            if (!string.IsNullOrEmpty(result.error))
            {
                if (sb.Length > 0) sb.AppendLine();
                sb.Append("STDERR:\n").Append(result.error);
            }
            return sb.Length == 0 ? "OK" : sb.ToString();
        }
        else
        {
            return $"ERROR: {result.pex}\n\nSTDERR:\n{result.error}";
        }
    }

    private (bool success, string data, string pex, string error) ExecutePythonInternal(string code, string workingDirectory)
    {
        // 确保有 Python 路径
        if (string.IsNullOrEmpty(_pythonPath))
        {
            _pythonPath = FindPythonPath();
            if (string.IsNullOrEmpty(_pythonPath))
            {
                return (false, "", "Python not found. Please install Python and add it to PATH.", "");
            }
        }

        _lock.Wait();
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _pythonPath,
                Arguments = "-u",  // -u: unbuffered, 从 stdin 读取代码
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardInputEncoding = Encoding.UTF8,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = workingDirectory
            };

            // 强制 Python 使用 UTF-8 编码，解决 Windows 上的中文乱码问题
            psi.Environment["PYTHONIOENCODING"] = "utf-8";
            psi.Environment["PYTHONUTF8"] = "1";  // Python 3.7+ UTF-8 模式

            using var process = new Process { StartInfo = psi };
            process.Start();

            // 使用同步方式：先写入代码，关闭 stdin，然后读取输出
            // 这样可以避免管道关闭的竞态条件
            try
            {
                process.StandardInput.Write(code);
                process.StandardInput.Close();
            }
            catch (IOException)
            {
                // 进程可能因为代码错误而提前退出
            }

            // 使用 Task 并行读取 stdout 和 stderr，避免死锁
            var stdoutTask = Task.Run(() =>
            {
                try { return process.StandardOutput.ReadToEnd(); }
                catch { return ""; }
            });

            var stderrTask = Task.Run(() =>
            {
                try { return process.StandardError.ReadToEnd(); }
                catch { return ""; }
            });

            // 等待执行完成（最多 5 分钟）
            var completed = process.WaitForExit(300000);

            if (!completed)
            {
                try { process.Kill(true); } catch { }

                // 给读取任务一点时间完成
                Task.WaitAll([stdoutTask, stderrTask], 1000);

                var partialStdout = stdoutTask.IsCompleted ? stdoutTask.Result : "";
                var partialStderr = stderrTask.IsCompleted ? stderrTask.Result : "";

                return (false, partialStdout.TrimEnd(), "Execution timeout (5 minutes)", partialStderr.TrimEnd());
            }

            // 等待读取任务完成
            Task.WaitAll([stdoutTask, stderrTask], 5000);

            var exitCode = process.ExitCode;
            var stdoutStr = stdoutTask.IsCompleted ? stdoutTask.Result.TrimEnd() : "";
            var stderrStr = stderrTask.IsCompleted ? stderrTask.Result.TrimEnd() : "";

            if (exitCode == 0)
            {
                return (true, stdoutStr, "", stderrStr);
            }
            else
            {
                return (false, stdoutStr, $"Exit code: {exitCode}", stderrStr);
            }
        }
        catch (Exception ex)
        {
            return (false, "", ex.Message, ex.ToString());
        }
        finally
        {
            _lock.Release();
        }
    }

    private static string? FindPythonPath()
    {
        // 常见 Python 可执行文件名
        var pythonNames = OperatingSystem.IsWindows()
            ? new[] { "python.exe", "python3.exe", "py.exe" }
            : new[] { "python3", "python" };

        // 0. 优先检查应用程序目录下的嵌入式 Python
        if (OperatingSystem.IsWindows())
        {
            var embedPaths = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "python-3.11.0-embed-amd64", "python.exe"),
                Path.Combine(AppContext.BaseDirectory, "python-embed", "python.exe"),
                Path.Combine(AppContext.BaseDirectory, "python", "python.exe"),
            };

            foreach (var embedPath in embedPaths)
            {
                if (File.Exists(embedPath))
                {
                    Console.WriteLine($"[PythonService] 使用嵌入式 Python: {embedPath}");
                    return embedPath;
                }
            }
        }

        // 1. 检查 PATH 环境变量
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        var pathDirs = pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        foreach (var dir in pathDirs)
        {
            foreach (var name in pythonNames)
            {
                var fullPath = Path.Combine(dir, name);
                if (File.Exists(fullPath))
                {
                    Console.WriteLine($"[PythonService] 在 PATH 中找到 Python: {fullPath}");
                    return fullPath;
                }
            }
        }

        // 2. Windows: 检查常见安装位置
        if (OperatingSystem.IsWindows())
        {
            var commonPaths = new[]
            {
                // Python Launcher
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Python"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Python"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Python"),
                // Anaconda / Miniconda
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "anaconda3"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "miniconda3"),
            };

            foreach (var basePath in commonPaths)
            {
                if (!Directory.Exists(basePath)) continue;

                // 直接检查
                var directPath = Path.Combine(basePath, "python.exe");
                if (File.Exists(directPath))
                {
                    Console.WriteLine($"[PythonService] 在常见位置找到 Python: {directPath}");
                    return directPath;
                }

                // 检查子目录（如 Python311, Python312 等）
                try
                {
                    foreach (var subDir in Directory.GetDirectories(basePath))
                    {
                        var subPath = Path.Combine(subDir, "python.exe");
                        if (File.Exists(subPath))
                        {
                            Console.WriteLine($"[PythonService] 在子目录找到 Python: {subPath}");
                            return subPath;
                        }
                    }
                }
                catch { /* ignore access errors */ }
            }
        }

        // 3. 尝试直接运行 python/python3 命令（依赖 PATH）
        foreach (var name in pythonNames)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = name,
                    Arguments = "--version",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = Process.Start(psi);
                if (process != null)
                {
                    process.WaitForExit(5000);
                    if (process.ExitCode == 0)
                    {
                        Console.WriteLine($"[PythonService] 通过命令找到 Python: {name}");
                        return name;
                    }
                }
            }
            catch { /* ignore */ }
        }

        Console.WriteLine("[PythonService] 未找到 Python");
        return null;
    }

    public void Dispose()
    {
        _lock.Dispose();
    }
}

