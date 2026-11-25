using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace FlowEncode;

public sealed class WipeHandleGrid : Grid
{
    public WipeHandleGrid()
    {
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
    }
}
