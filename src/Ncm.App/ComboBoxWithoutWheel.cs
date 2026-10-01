using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Ncm.App;

public sealed partial class ComboBoxWithoutWheel : ComboBox
{
    protected override void OnPointerWheelChanged(PointerRoutedEventArgs e)
    {
        // 不调用默认选项切换，让滚轮继续交给外层滚动区域。
    }
}
