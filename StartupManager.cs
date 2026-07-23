using System;
using System.IO;
using Microsoft.Win32;

namespace FloatingTodo;

internal static class StartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "FloatingTodo";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(ValueName) is string value && !string.IsNullOrWhiteSpace(value);
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("无法打开 Windows 启动项注册表。");

        if (enabled)
        {
            key.SetValue(ValueName, $"\"{GetExecutablePath()}\"", RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    private static string GetExecutablePath()
    {
        var bundledPath = Path.Combine(AppContext.BaseDirectory, "FloatingTodo.exe");
        if (File.Exists(bundledPath))
        {
            return bundledPath;
        }

        return Environment.ProcessPath
            ?? throw new InvalidOperationException("无法确定 Floating Todo 的程序路径。");
    }
}
