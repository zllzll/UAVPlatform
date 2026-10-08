using UavPlatform.Core.Models;

namespace UavPlatform.Tests;

/// <summary>
/// 参数持久化：运行时配置的路径解析、首次播种与「保存后再打开还在」的往返。
///
/// 这些用例对应一次实测出来的两个坑：配置以前放在 ContentRoot 下，导致
/// <c>dotnet run</c>（工程目录）与直接跑产物 exe（<c>bin\Release\net9.0</c>）读写的是两份文件；
/// 而产物目录那一份会被 <c>dotnet build</c> 用工程默认值覆盖（SDK 默认 Content + PreserveNewest）。
/// </summary>
public class ConfigPersistenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "UavPlatformTests", "config-" + Guid.NewGuid().ToString("N"));

    public ConfigPersistenceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* 清理失败不影响结论 */ }
    }

    private string TempFile(string name) => Path.Combine(_root, name);

    [Fact]
    public void 配置文件不存在时读取会落盘一份默认配置()
    {
        var path = TempFile("platform.json");
        var store = new ConfigStore(path);

        var config = store.Load();

        Assert.True(File.Exists(path), "首次启动应当把默认配置写盘，否则「文件不存在」这条路径永远不留下可编辑的配置");
        var reloaded = new ConfigStore(path).Load();
        Assert.Equal(config.Name, reloaded.Name);
        Assert.Equal(DeviceKind.BaseStation, reloaded.Devices[0].Kind);
        Assert.Equal(3, reloaded.Devices.Count);
    }

    [Fact]
    public void 保存的参数在重新打开配置存取器后仍在()
    {
        var path = TempFile("platform.json");
        var store = new ConfigStore(path);
        var config = store.Load();
        config.Name = "持久化测试-0001";
        config.Storage.FlushIntervalMs = 1500;
        store.Save(config);

        // 模拟「关掉后端再打开」：新建一个存取器读同一个文件
        var reopened = new ConfigStore(path).Load();

        Assert.Equal("持久化测试-0001", reopened.Name);
        Assert.Equal(1500, reopened.Storage.FlushIntervalMs);
    }

    [Fact]
    public void 运行时配置落在用户目录下而不是产物目录()
    {
        var path = ConfigLocator.ResolveConfigPath(@"D:\some\content\root");

        Assert.EndsWith(Path.Combine("UavPlatform", "config", "platform.json"), path);
        Assert.DoesNotContain("bin", path, StringComparison.OrdinalIgnoreCase);

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            Assert.StartsWith(localAppData, path, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void 环境变量可以指定运行时配置文件位置()
    {
        var previous = Environment.GetEnvironmentVariable(ConfigLocator.OverrideEnvironmentVariable);
        try
        {
            var custom = TempFile("custom.json");
            Environment.SetEnvironmentVariable(ConfigLocator.OverrideEnvironmentVariable, custom);

            Assert.Equal(Path.GetFullPath(custom), ConfigLocator.ResolveConfigPath(@"D:\some\content\root"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(ConfigLocator.OverrideEnvironmentVariable, previous);
        }
    }

    [Fact]
    public void 首次运行会从出厂默认配置播种()
    {
        var factory = TempFile("factory.json");
        var config = PlatformConfig.CreateDefault();
        config.Name = "出厂默认";
        File.WriteAllText(factory, ConfigJson.Serialize(config), new System.Text.UTF8Encoding(false));
        var target = Path.Combine(_root, "home", "platform.json");

        var seeded = ConfigLocator.SeedIfMissing(target, factory);

        Assert.True(seeded);
        Assert.Equal("出厂默认", new ConfigStore(target).Load().Name);
    }

    [Fact]
    public void 已经存在的用户配置不会被出厂默认配置覆盖()
    {
        var factory = TempFile("factory.json");
        var factoryConfig = PlatformConfig.CreateDefault();
        factoryConfig.Name = "出厂默认";
        File.WriteAllText(factory, ConfigJson.Serialize(factoryConfig), new System.Text.UTF8Encoding(false));

        var target = TempFile("platform.json");
        var saved = PlatformConfig.CreateDefault();
        saved.Name = "运维保存的参数";
        new ConfigStore(target).Save(saved);

        var seeded = ConfigLocator.SeedIfMissing(target, factory);

        Assert.False(seeded);
        Assert.Equal("运维保存的参数", new ConfigStore(target).Load().Name);
    }

    [Fact]
    public void 界面配置里不再有地图开关()
    {
        var json = ConfigJson.Serialize(new UiSettings());

        Assert.DoesNotContain("showMap", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void 界面配置有无人机开关且默认显示()
    {
        // m06464 第 2 点：工具条上要能单独关掉无人机本身。默认 true——升级后老配置里没有这个键，
        // 反序列化取默认值，不能因为「少了开关」就把无人机藏起来。
        var json = ConfigJson.Serialize(new UiSettings());

        Assert.True(new UiSettings().ShowDrone);
        Assert.Contains("showDrone", json, StringComparison.Ordinal);
    }

    [Fact]
    public void 轨迹保留参数已经并入尾迹时长且旧配置仍能加载()
    {
        // m06464 第 5 点：relative.trackHistorySeconds 与 ui.trailSeconds 是同一个意思（轨迹活多久），
        // 两个旋钮必然对不上——尾迹拉到 600 s、后端只留 60 s，刷新页面就只能回看 60 s。现在只留尾迹。
        var json = ConfigJson.Serialize(PlatformConfig.CreateDefault());
        Assert.DoesNotContain("trackHistorySeconds", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("trailSeconds", json, StringComparison.Ordinal);

        // 老配置文件（或老会话快照）里残留的键必须被忽略，而不是让整份配置加载失败
        var legacy = ConfigJson.Deserialize<RelativeSettings>(
            "{\"relativeIntervalMs\":120,\"trackHistorySeconds\":600}");
        Assert.NotNull(legacy);
        Assert.Equal(120, legacy!.RelativeIntervalMs);
    }
}
