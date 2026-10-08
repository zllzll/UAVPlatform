using System;
using System.IO;

namespace UavPlatform.Core.Models;

/// <summary>
/// 运行时配置文件「唯一权威路径」的解析与首次播种。
///
/// 背景（实测出来的两个坑）：
/// 一是配置原先放在 ContentRoot 下的 <c>config/platform.json</c>，而 ContentRoot 取决于启动方式——
/// <c>dotnet run</c>（开发模式与 run.ps1 的生产模式）是工程目录，直接跑产物 exe 是
/// <c>bin\Release\net9.0</c>，同一台机器上于是有两份互不相干的配置；
/// 二是产物目录那一份会被 <c>dotnet build</c> 用工程里的默认值覆盖（SDK 默认把 <c>**\*.json</c>
/// 以 PreserveNewest 拷进产物目录），运维保存的参数在下一次构建之后就没了。
///
/// 现在改成：运行时配置固定在用户目录
/// <c>%LOCALAPPDATA%\UavPlatform\config\platform.json</c>，三种启动方式共用同一份，构建碰不到它；
/// 工程/产物目录下的 <c>config\platform.json</c> 退化为「出厂默认值 + 首次运行的播种来源」。
/// 需要整体换位置时，设置环境变量 <see cref="OverrideEnvironmentVariable"/> 即可。
/// </summary>
public static class ConfigLocator
{
    /// <summary>指定运行时配置文件路径的环境变量（整路径，可用相对路径）。</summary>
    public const string OverrideEnvironmentVariable = "UAVPLATFORM_CONFIG";

    /// <summary>用户目录下的平台配置文件夹名。</summary>
    public const string ConfigDirectoryName = "UavPlatform";

    /// <summary>配置文件名。</summary>
    public const string ConfigFileName = "platform.json";

    /// <summary>出厂默认配置相对 ContentRoot 的位置。</summary>
    public static string FactoryDefaultRelativePath => Path.Combine("config", ConfigFileName);

    /// <summary>ContentRoot 下的出厂默认配置全路径（也是旧版本真正保存参数的位置）。</summary>
    public static string FactoryDefaultPath(string baseDirectory) =>
        Path.Combine(baseDirectory, FactoryDefaultRelativePath);

    /// <summary>
    /// 解析运行时配置的权威路径：环境变量优先，否则用用户目录下的固定位置。
    /// 取不到用户目录时退回 ContentRoot，保证任何环境下都有可用的落点。
    /// </summary>
    public static string ResolveConfigPath(string baseDirectory)
    {
        var overridePath = Environment.GetEnvironmentVariable(OverrideEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return Path.GetFullPath(overridePath.Trim());
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var root = string.IsNullOrWhiteSpace(localAppData) ? baseDirectory : localAppData;
        return Path.Combine(root, ConfigDirectoryName, FactoryDefaultRelativePath);
    }

    /// <summary>
    /// 运行时配置不存在时，从出厂默认配置播种一份过来。
    ///
    /// 幂等：只要目标文件已存在就什么都不做，绝不覆盖用户保存过的参数。
    /// 播种来源同时兼顾了从旧版本升级：旧版本一直把运维参数写在 ContentRoot 下的
    /// <c>config\platform.json</c>，所以第一次升级运行时那些参数会被原样搬到新位置。
    /// 返回是否真的播种（来源文件不存在或没权限时为 false，由调用方继续走默认配置兜底）。
    /// </summary>
    public static bool SeedIfMissing(string configPath, string factoryDefaultPath)
    {
        if (string.IsNullOrWhiteSpace(configPath) || File.Exists(configPath))
        {
            return false;
        }

        // 目标与来源是同一个文件时不必自拷贝
        if (string.Equals(Path.GetFullPath(configPath), Path.GetFullPath(factoryDefaultPath), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!File.Exists(factoryDefaultPath))
        {
            return false;
        }

        try
        {
            var dir = Path.GetDirectoryName(configPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.Copy(factoryDefaultPath, configPath, overwrite: false);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
