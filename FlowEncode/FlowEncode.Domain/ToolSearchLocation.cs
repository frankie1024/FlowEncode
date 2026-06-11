namespace FlowEncode.Domain;

[Flags]
public enum ToolSearchLocation
{
    None = 0,
    EnvironmentVariables = 1 << 0,
    LocalToolsRoot = 1 << 1,
    Path = 1 << 2,
    VspipeSidecar = 1 << 3,
    ProgramFilesVapourSynth = 1 << 4,
    PythonScripts = 1 << 5,
    KnownPythonInstallations = 1 << 6
}
