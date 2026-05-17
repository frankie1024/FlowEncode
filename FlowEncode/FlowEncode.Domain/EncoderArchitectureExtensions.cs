namespace FlowEncode.Domain;

public static class EncoderArchitectureExtensions
{
    public static string ToDisplayName(this EncoderArchitecture architecture) =>
        architecture == EncoderArchitecture.X64 ? "x64" : "x86";
}
