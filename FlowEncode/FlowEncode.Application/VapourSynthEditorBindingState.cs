namespace FlowEncode.Application;

public sealed record VapourSynthEditorDocumentBinding(
    string PaneId,
    string TabId,
    long LoadGeneration);

public sealed class VapourSynthEditorBindingState
{
    private readonly object _sync = new();
    private readonly string _paneId = Guid.NewGuid().ToString("N");
    private long _loadGeneration;

    public VapourSynthEditorDocumentBinding? PendingBinding { get; private set; }

    public VapourSynthEditorDocumentBinding? ConfirmedBinding { get; private set; }

    public VapourSynthEditorDocumentBinding BeginLoad(string tabId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tabId);

        lock (_sync)
        {
            var binding = new VapourSynthEditorDocumentBinding(
                _paneId,
                tabId,
                checked(++_loadGeneration));
            PendingBinding = binding;
            ConfirmedBinding = null;
            return binding;
        }
    }

    public bool TryConfirm(
        VapourSynthEditorDocumentBinding expectedBinding,
        VapourSynthEditorDocumentBinding? acknowledgedBinding)
    {
        ArgumentNullException.ThrowIfNull(expectedBinding);

        lock (_sync)
        {
            if (!IsValid(acknowledgedBinding)
                || !Equals(expectedBinding, acknowledgedBinding)
                || !Equals(PendingBinding, expectedBinding))
            {
                return false;
            }

            PendingBinding = null;
            ConfirmedBinding = expectedBinding;
            return true;
        }
    }

    public bool IsConfirmed(VapourSynthEditorDocumentBinding? binding)
    {
        lock (_sync)
        {
            return IsValid(binding) && Equals(ConfirmedBinding, binding);
        }
    }

    public bool IsPending(VapourSynthEditorDocumentBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);

        lock (_sync)
        {
            return Equals(PendingBinding, binding);
        }
    }

    public void Invalidate()
    {
        lock (_sync)
        {
            PendingBinding = null;
            ConfirmedBinding = null;
        }
    }

    public static bool IsValid(VapourSynthEditorDocumentBinding? binding)
    {
        return binding is
        {
            PaneId.Length: > 0,
            TabId.Length: > 0,
            LoadGeneration: > 0
        };
    }
}
