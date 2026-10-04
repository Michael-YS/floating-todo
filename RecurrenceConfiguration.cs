using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace FloatingTodo;

public sealed class RecurrenceConfiguration
{
    public static readonly string[] Weekdays = ["MO", "TU", "WE", "TH", "FR", "SA", "SU"];
    public string Frequency { get; set; } = "WEEKLY";
    public int Interval { get; set; } = 1;
    public string[] Days { get; set; } = [];
    public string MonthDays { get; set; } = string.Empty;
    public int? Position { get; set; }
    public int? Count { get; set; }
    public DateTime? Until { get; set; }

    public string Build(bool timedAnchor)
    {
        if (Frequency is not ("DAILY" or "WEEKLY" or "MONTHLY" or "YEARLY"))
            throw new FormatException("请选择重复频率");
        if (Interval < 1) throw new FormatException("重复间隔必须为正整数");
        if (Count is < 1) throw new FormatException("重复次数必须为正整数");
        if (Count is not null && Until is not null) throw new FormatException("次数与结束日期不能同时设置");
        var parts = new List<string> { "FREQ=" + Frequency };
        if (Interval != 1) parts.Add("INTERVAL=" + Interval.ToString(CultureInfo.InvariantCulture));
        if (Days.Any(day => !Weekdays.Contains(day)) || Days.Distinct().Count() != Days.Length)
            throw new FormatException("星期配置无效");
        if (Days.Length > 0)
        {
            if (Frequency is not ("WEEKLY" or "MONTHLY")) throw new FormatException("此频率不支持星期配置");
            parts.Add("BYDAY=" + string.Join(",", Days));
        }
        if (!string.IsNullOrWhiteSpace(MonthDays))
        {
            if (Frequency != "MONTHLY" || Days.Length > 0) throw new FormatException("每月日期与星期模式不能同时设置");
            var dates = MonthDays.Split(',', StringSplitOptions.TrimEntries);
            if (dates.Any(value => !int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var day)
                                   || day == 0 || day < -31 || day > 31))
                throw new FormatException("每月日期应为 1～31 或 -1～-31，用逗号分隔");
            parts.Add("BYMONTHDAY=" + string.Join(",", dates.Select(value => int.Parse(value, CultureInfo.InvariantCulture)).Distinct()));
        }
        if (Position is int position)
        {
            if (Frequency != "MONTHLY" || Days.Length != 1 || position is not (1 or 2 or 3 or 4 or 5 or -1))
                throw new FormatException("请选择每月第几个星期几");
            parts.Add("BYSETPOS=" + position.ToString(CultureInfo.InvariantCulture));
        }
        if (Count is int count) parts.Add("COUNT=" + count.ToString(CultureInfo.InvariantCulture));
        if (Until is DateTime until)
        {
            // Timed DTSTART is serialized in UTC; UNTIL must use UTC too.
            parts.Add("UNTIL=" + (timedAnchor
                ? DateTime.SpecifyKind(until.Date.AddDays(1).AddSeconds(-1), DateTimeKind.Local).ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture)
                : until.ToString("yyyyMMdd", CultureInfo.InvariantCulture)));
        }
        return string.Join(";", parts);
    }

    public static bool TryParse(string rule, bool timedAnchor, out RecurrenceConfiguration configuration)
    {
        configuration = new RecurrenceConfiguration();
        try
        {
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var part in rule.Split(';'))
            {
                var pair = part.Split('=');
                if (pair.Length != 2 || !fields.TryAdd(pair[0], pair[1])) return false;
            }
            // Rules outside the editor's subset stay in raw mode instead of losing fields.
            if (fields.Keys.Any(key => key is not ("FREQ" or "INTERVAL" or "BYDAY" or "BYMONTHDAY" or "BYSETPOS" or "COUNT" or "UNTIL"))) return false;
            configuration.Frequency = fields["FREQ"];
            if (fields.TryGetValue("INTERVAL", out var interval)) configuration.Interval = int.Parse(interval, CultureInfo.InvariantCulture);
            if (fields.TryGetValue("BYDAY", out var days)) configuration.Days = days.Split(',');
            if (fields.TryGetValue("BYMONTHDAY", out var dates)) configuration.MonthDays = dates;
            if (fields.TryGetValue("BYSETPOS", out var position)) configuration.Position = int.Parse(position, CultureInfo.InvariantCulture);
            if (configuration.Frequency == "MONTHLY" && configuration.Days.Length == 1 && configuration.Days[0].Length > 2)
            {
                if (configuration.Position is not null) return false;
                var ordinalDay = configuration.Days[0];
                configuration.Position = int.Parse(ordinalDay[..^2], CultureInfo.InvariantCulture);
                configuration.Days = [ordinalDay[^2..]];
            }
            if (fields.TryGetValue("COUNT", out var count)) configuration.Count = int.Parse(count, CultureInfo.InvariantCulture);
            if (fields.TryGetValue("UNTIL", out var until))
            {
                if (timedAnchor)
                {
                    var local = DateTime.ParseExact(until, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal).ToLocalTime();
                    if (local.TimeOfDay != new TimeSpan(23, 59, 59)) return false;
                    configuration.Until = local.Date;
                }
                else configuration.Until = DateTime.ParseExact(until, "yyyyMMdd", CultureInfo.InvariantCulture);
            }
            if (configuration.Frequency == "MONTHLY" && configuration.Days.Length > 0 && configuration.Position is null) return false;
            _ = configuration.Build(timedAnchor);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException or KeyNotFoundException)
        {
            return false;
        }
    }
}
