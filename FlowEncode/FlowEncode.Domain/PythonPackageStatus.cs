namespace FlowEncode.Domain;

public sealed record PythonPackageStatus(string Name, string InstalledVersion, bool IsReady);
