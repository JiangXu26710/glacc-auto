using Avalonia.Controls;
using Avalonia.Input;
using GlaccAuto.Gui.ViewModels;

namespace GlaccAuto.Gui.Views;

public partial class HomePage : UserControl
{
    public HomePage()
    {
        InitializeComponent();
    }

    private MainWindowViewModel Vm => (MainWindowViewModel)DataContext!;

    /// <summary>点击已登录账号行：切换 显示/隐藏 完整手机号</summary>
    private void AccountRow_OnTapped(object? sender, TappedEventArgs e)
        => Vm.TogglePhoneRevealCommand.Execute(null);

    /// <summary>点击未登录账号区：打开登录引导</summary>
    private void LoginEntry_OnTapped(object? sender, TappedEventArgs e)
        => Vm.OpenLoginCommand.Execute(null);
}
