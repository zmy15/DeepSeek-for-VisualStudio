using System.Globalization;

namespace DeepSeek_v4_for_VisualStudio.Utils
{
    /// <summary>
    /// 将字节数格式化为便于日志和界面展示的单位。
    /// </summary>
    internal static class FileSizeFormatter
    {
        public static string Format(long bytes)
        {
            const double Kilobyte = 1024.0;
            const double Megabyte = Kilobyte * 1024.0;

            if (bytes < Kilobyte)
                return string.Format(CultureInfo.InvariantCulture, "{0} B", bytes);
            if (bytes < Megabyte)
                return string.Format(CultureInfo.InvariantCulture, "{0:F1} KB", bytes / Kilobyte);
            return string.Format(CultureInfo.InvariantCulture, "{0:F1} MB", bytes / Megabyte);
        }
    }
}
