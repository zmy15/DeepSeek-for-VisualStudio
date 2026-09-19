using System;
using System.Collections.Generic;

namespace DeepSeek_v4_for_VisualStudio.Utils
{
    /// <summary>
    /// 中国法定节假日及调休放假日历。
    /// 数据来自国务院办公厅公布的 2026 年节假日安排；周末由调用方另行处理。
    /// </summary>
    internal static class ChineseHolidayCalendar
    {
        private static readonly HashSet<DateTime> PublicHolidayDates = CreatePublicHolidayDates();

        /// <summary>判断指定北京时间日期是否为法定节假日或调休放假日。</summary>
        internal static bool IsPublicHoliday(DateTime beijingDate)
            => PublicHolidayDates.Contains(beijingDate.Date);

        private static HashSet<DateTime> CreatePublicHolidayDates()
        {
            var dates = new HashSet<DateTime>();

            // 2026 年国务院办公厅节假日安排
            AddRange(dates, new DateTime(2026, 1, 1), 3);   // 元旦
            AddRange(dates, new DateTime(2026, 2, 15), 9);  // 春节
            AddRange(dates, new DateTime(2026, 4, 4), 3);   // 清明节
            AddRange(dates, new DateTime(2026, 5, 1), 5);   // 劳动节
            AddRange(dates, new DateTime(2026, 6, 19), 3);  // 端午节
            AddRange(dates, new DateTime(2026, 9, 25), 3);  // 中秋节
            AddRange(dates, new DateTime(2026, 10, 1), 7);  // 国庆节

            return dates;
        }

        private static void AddRange(HashSet<DateTime> dates, DateTime start, int days)
        {
            for (int i = 0; i < days; i++)
                dates.Add(start.AddDays(i));
        }
    }
}