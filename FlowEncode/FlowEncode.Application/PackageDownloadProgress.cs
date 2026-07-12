namespace FlowEncode.Application;

public sealed record PackageDownloadProgress(long BytesReceived, long? TotalBytes);
