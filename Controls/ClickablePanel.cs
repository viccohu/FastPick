using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Input;

namespace FastPick.Controls;

public class ClickablePanel : ContentControl
{
    public ClickablePanel()
    {
        DefaultStyleKey = typeof(ClickablePanel);
        PointerEntered += OnPointerEntered;
        PointerExited += OnPointerExited;
    }

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Hand);
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Arrow);
    }
}
