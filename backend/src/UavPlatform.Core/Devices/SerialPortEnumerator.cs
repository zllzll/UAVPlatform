namespace UavPlatform.Core.Devices;

/// <summary>
/// 本机串口枚举（供 REST <c>GET /api/serial-ports</c> 使用）。
/// 放在 Core 工程是为了复用已引用的 <c>System.IO.Ports</c> 包，
/// 避免 Api 工程新增 PackageReference（离线环境无法 restore）。
/// </summary>
public static class SerialPortEnumerator
{
    /// <summary>
    /// 枚举本机可用串口名：去重后按自然顺序排序（COM2 排在 COM10 之前）。
    /// 无串口、非 Windows 或串口驱动异常时返回空数组，不抛异常（避免接口 500）。
    /// </summary>
    public static string[] GetPortNames()
    {
        try
        {
            return System.IO.Ports.SerialPort.GetPortNames()
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, PortNameComparer.Instance)
                .ToArray();
        }
        catch
        {
            // 注册表不可用 / 驱动异常 / 非 Windows：一律按「没有串口」处理。
            return [];
        }
    }

    /// <summary>串口名自然排序：字母部分不区分大小写，数字部分按数值大小比较。</summary>
    private sealed class PortNameComparer : IComparer<string>
    {
        public static readonly PortNameComparer Instance = new();

        public int Compare(string? left, string? right)
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left is null) return -1;
            if (right is null) return 1;

            int i = 0, j = 0;
            while (i < left.Length && j < right.Length)
            {
                if (char.IsDigit(left[i]) && char.IsDigit(right[j]))
                {
                    // 跳过前导零，再按「有效位数 → 字典序」比较，避免 Parse 溢出。
                    var startLeft = i;
                    while (startLeft < left.Length && left[startLeft] == '0') startLeft++;
                    var endLeft = startLeft;
                    while (endLeft < left.Length && char.IsDigit(left[endLeft])) endLeft++;

                    var startRight = j;
                    while (startRight < right.Length && right[startRight] == '0') startRight++;
                    var endRight = startRight;
                    while (endRight < right.Length && char.IsDigit(right[endRight])) endRight++;

                    var lengthLeft = endLeft - startLeft;
                    var lengthRight = endRight - startRight;
                    if (lengthLeft != lengthRight) return lengthLeft - lengthRight;

                    var numeric = string.CompareOrdinal(left, startLeft, right, startRight, lengthLeft);
                    if (numeric != 0) return numeric;

                    i = endLeft;
                    j = endRight;
                    continue;
                }

                var letters = char.ToUpperInvariant(left[i]).CompareTo(char.ToUpperInvariant(right[j]));
                if (letters != 0) return letters;
                i++;
                j++;
            }

            return (left.Length - i) - (right.Length - j);
        }
    }
}
