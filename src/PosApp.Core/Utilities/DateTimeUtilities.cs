namespace PosApp.Core.Utilities;

/// <summary>Converts user-selected local calendar dates to UTC database boundaries.</summary>
public static class DateTimeUtilities
{
    public static DateTime LocalDateStartUtc(DateTime localDate)
    {
        var unspecified = DateTime.SpecifyKind(localDate.Date, DateTimeKind.Unspecified);
        return TimeZoneInfo.ConvertTimeToUtc(unspecified, TimeZoneInfo.Local);
    }

    public static (DateTime FromUtc, DateTime ToUtcExclusive) InclusiveLocalDateRange(DateTime from, DateTime to)
    {
        var fromDate = from.Date;
        var toDate = to.Date;
        if (toDate < fromDate)
            throw new ArgumentException("The end date cannot be earlier than the start date.", nameof(to));
        return (LocalDateStartUtc(fromDate), LocalDateStartUtc(toDate.AddDays(1)));
    }

    public static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => TimeZoneInfo.ConvertTimeToUtc(value, TimeZoneInfo.Local)
    };

    public static DateTime ToLocal(DateTime value) => value.Kind switch
    {
        DateTimeKind.Local => value,
        DateTimeKind.Utc => value.ToLocalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc).ToLocalTime()
    };
}
