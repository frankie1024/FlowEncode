namespace FlowEncode.Domain;

public enum AutoCompressionExecutionStage
{
    Preparing,
    InputProbing,
    SceneDetection,
    ChunkPlanning,
    Probing,
    Encoding,
    Concatenating,
    Completed,
    Failed,
    Cancelled
}
