using UavPlatform.Core.Devices;

namespace UavPlatform.Tests;

/// <summary>
/// 尾迹时长（ui.trailSeconds）→ 后端轨迹保留窗口的换算。
///
/// 以前这是两个各自独立的旋钮（工具条上的尾迹 / 相对位置页的 trackHistorySeconds）：
/// 尾迹拉到 600 s 而后端只留 60 s 时，画面看着正常，一刷新页面就只剩 60 s 的数据。
/// 现在保留窗口由尾迹推出（DeviceManager.TrackRetention），这里钉住映射关系与边界。
/// </summary>
public class TrackRetentionTests
{
    [Theory]
    [InlineData(60, 60)]
    [InlineData(120, 120)]
    [InlineData(600, 600)]
    [InlineData(31, 31)]
    [InlineData(1, 30)]     // 尾迹设得很短，也要留够刷新后回看的数据
    [InlineData(0, 30)]     // 0 = 不画尾迹，但后端仍保留半分钟
    [InlineData(-5, 30)]    // 手输负数：夹到下限而不是当成「不清理」
    [InlineData(9999, 600)] // 手输超大值：夹到界面上限
    public void 保留窗口跟随尾迹时长并夹在30到600秒(int trailSeconds, int expectedSeconds)
    {
        var (retentionSeconds, pointLimit) = DeviceManager.TrackRetention(trailSeconds);

        Assert.Equal(expectedSeconds, retentionSeconds);
        Assert.Equal(Math.Max(10, expectedSeconds * 5), pointLimit);
    }
}
