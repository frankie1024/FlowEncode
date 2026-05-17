namespace FlowEncode.Domain;

public static class DateTimeOffsetExtensions
{
    public static string ToPublishedLabel(this DateTimeOffset value) =>
        value.LocalDateTime.ToString("yyyy-MM-dd HH:mm");
}
