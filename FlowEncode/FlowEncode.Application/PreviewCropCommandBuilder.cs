namespace FlowEncode.Application;

/// <summary>
/// Builds the VapourSynth crop line the preview window copies to the clipboard.
/// </summary>
public static class PreviewCropCommandBuilder
{
    public static string BuildAbsolute(int left, int top, int width, int height, string clipToken = "clip")
    {
        return $"{clipToken} = core.std.CropAbs({clipToken}, x={left}, y={top}, width={width}, height={height})";
    }

    public static string BuildRelative(int left, int top, int right, int bottom, string clipToken = "clip")
    {
        return $"{clipToken} = core.std.CropRel({clipToken}, left={left}, top={top}, right={right}, bottom={bottom})";
    }

    public static string Build(PreviewCropMode mode, PreviewCropBounds bounds, string clipToken = "clip")
    {
        ArgumentNullException.ThrowIfNull(bounds);
        return mode == PreviewCropMode.Absolute
            ? BuildAbsolute(bounds.Left, bounds.Top, bounds.Width, bounds.Height, clipToken)
            : BuildRelative(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom, clipToken);
    }
}
