using FastPick.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using System.Diagnostics;
using Windows.System;

namespace FastPick.Services;

/// <summary>
/// 键盘快捷键服务
/// 处理全局键盘事件，映射到 ViewModel 方法
/// </summary>
public class KeyboardService
{
    private readonly MainViewModel _viewModel;
    private readonly Dictionary<VirtualKey, Func<Task>> _keyHandlers;
    private readonly Dictionary<VirtualKey, Action> _keyHandlersSync;
    private bool _isEnabled = true;

    public KeyboardService(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        _keyHandlers = new Dictionary<VirtualKey, Func<Task>>();
        _keyHandlersSync = new Dictionary<VirtualKey, Action>();
        InitializeKeyHandlers();
    }

    /// <summary>
    /// 是否启用快捷键
    /// </summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        set => _isEnabled = value;
    }

    /// <summary>
    /// 初始化按键处理映射
    /// </summary>
    private void InitializeKeyHandlers()
    {
        // 数字键 0-5：设置评级
        _keyHandlers[VirtualKey.Number0] = async () => await _viewModel.SetRatingForSelectedAsync(0);
        _keyHandlers[VirtualKey.NumberPad0] = async () => await _viewModel.SetRatingForSelectedAsync(0);
        
        _keyHandlers[VirtualKey.Number1] = async () => await _viewModel.SetRatingForSelectedAsync(1);
        _keyHandlers[VirtualKey.NumberPad1] = async () => await _viewModel.SetRatingForSelectedAsync(1);
        
        _keyHandlers[VirtualKey.Number2] = async () => await _viewModel.SetRatingForSelectedAsync(2);
        _keyHandlers[VirtualKey.NumberPad2] = async () => await _viewModel.SetRatingForSelectedAsync(2);
        
        _keyHandlers[VirtualKey.Number3] = async () => await _viewModel.SetRatingForSelectedAsync(3);
        _keyHandlers[VirtualKey.NumberPad3] = async () => await _viewModel.SetRatingForSelectedAsync(3);
        
        _keyHandlers[VirtualKey.Number4] = async () => await _viewModel.SetRatingForSelectedAsync(4);
        _keyHandlers[VirtualKey.NumberPad4] = async () => await _viewModel.SetRatingForSelectedAsync(4);
        
        _keyHandlers[VirtualKey.Number5] = async () => await _viewModel.SetRatingForSelectedAsync(5);
        _keyHandlers[VirtualKey.NumberPad5] = async () => await _viewModel.SetRatingForSelectedAsync(5);

        // Delete 键：标记/取消预删除（同步）
        _keyHandlersSync[VirtualKey.Delete] = () => _viewModel.ToggleMarkForDeletionForSelected();

        // 方向键：切换图片
        _keyHandlersSync[VirtualKey.Left] = () => _viewModel.NavigatePrevious();
        _keyHandlersSync[VirtualKey.Right] = () => _viewModel.NavigateNext();

        // ESC 键：清除选择（同步）
        _keyHandlersSync[VirtualKey.Escape] = () => _viewModel.DeselectAll();
    }

    /// <summary>
    /// 处理键盘按下事件
    /// </summary>
    /// <param name="sender">事件发送者</param>
    /// <param name="e">按键事件参数</param>
    /// <returns>是否处理了该按键</returns>
    public bool HandleKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!_isEnabled)
            return false;

        var key = e.Key;
        var ctrlPressed = IsCtrlPressed();
        var shiftPressed = IsShiftPressed();

        // Ctrl+A：全选
        if (ctrlPressed && key == VirtualKey.A)
        {
            _viewModel.SelectAll();
            e.Handled = true;
            return true;
        }

        // Ctrl+Shift+Delete：清空预删除列表
        if (ctrlPressed && shiftPressed && key == VirtualKey.Delete)
        {
            _viewModel.ClearMarkedForDeletion();
            e.Handled = true;
            return true;
        }

        // 处理数字键评级（不需要修饰键）
        if (_keyHandlers.TryGetValue(key, out var handler))
        {
            // 避免在文本输入框中触发
            if (!IsTextInputControlFocused(sender))
            {
                _ = handler(); // 异步执行，不等待
                e.Handled = true;
                return true;
            }
        }

        // 处理同步快捷键
        if (_keyHandlersSync.TryGetValue(key, out var syncHandler))
        {
            // 避免在文本输入框中触发
            if (!IsTextInputControlFocused(sender))
            {
                syncHandler();
                e.Handled = true;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 处理指针滚轮事件（用于缩放）
    /// </summary>
    /// <param name="sender">事件发送者</param>
    /// <param name="e">指针事件参数</param>
    /// <returns>是否处理了该事件</returns>
    public bool HandlePointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (!_isEnabled)
            return false;

        var ctrlPressed = IsCtrlPressed();
        if (!ctrlPressed)
            return false;

        var pointer = e.GetCurrentPoint(null);
        var delta = pointer.Properties.MouseWheelDelta;

        // TODO: 实现缩放功能
        // if (delta > 0) ZoomIn();
        // else if (delta < 0) ZoomOut();

        e.Handled = true;
        return true;
    }

    /// <summary>
    /// 处理缩略图点击事件（支持 Shift 和 Ctrl 多选）
    /// </summary>
    /// <param name="clickedItem">被点击的图片项</param>
    /// <param name="isCtrlPressed">Ctrl 是否按下</param>
    /// <param name="isShiftPressed">Shift 是否按下</param>
    public void HandleThumbnailClick(Models.PhotoItem clickedItem, bool isCtrlPressed, bool isShiftPressed)
    {
        if (clickedItem == null)
            return;

        if (isCtrlPressed)
        {
            // Ctrl+ 点击：切换选中状态（不改变锚点）
            _viewModel.ToggleSelection(clickedItem);
        }
        else if (isShiftPressed)
        {
            // Shift+ 点击：范围选择（使用锚点）
            _viewModel.SelectRange(clickedItem);
        }
        else
        {
            // 普通点击：单选（设置锚点）
            _viewModel.SelectWithAnchor(clickedItem);
        }
    }

    /// <summary>
    /// 检查 Ctrl 键是否按下
    /// </summary>
    private bool IsCtrlPressed()
    {
        var ctrlState = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control);
        return ctrlState.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
    }

    /// <summary>
    /// 检查 Shift 键是否按下
    /// </summary>
    private bool IsShiftPressed()
    {
        var shiftState = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift);
        return shiftState.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
    }

    /// <summary>
    /// 检查当前焦点是否在文本输入控件上
    /// </summary>
    private bool IsTextInputControlFocused(object sender)
    {
        if (sender is FrameworkElement element)
        {
            // 获取当前焦点元素
            var focusedElement = FocusManager.GetFocusedElement(element.XamlRoot);
            
            // 检查是否为文本输入控件
            return focusedElement is Microsoft.UI.Xaml.Controls.TextBox 
                || focusedElement is Microsoft.UI.Xaml.Controls.PasswordBox
                || focusedElement is Microsoft.UI.Xaml.Controls.AutoSuggestBox;
        }
        return false;
    }

    /// <summary>
    /// 注册到窗口的键盘事件
    /// </summary>
    /// <param name="window">目标窗口</param>
    public void Register(Window window)
    {
        if (window.Content is FrameworkElement rootElement)
        {
            rootElement.KeyDown += OnKeyDown;
            rootElement.PointerWheelChanged += OnPointerWheelChanged;
        }
    }

    /// <summary>
    /// 从窗口注销键盘事件
    /// </summary>
    /// <param name="window">目标窗口</param>
    public void Unregister(Window window)
    {
        if (window.Content is FrameworkElement rootElement)
        {
            rootElement.KeyDown -= OnKeyDown;
            rootElement.PointerWheelChanged -= OnPointerWheelChanged;
        }
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        HandleKeyDown(sender, e);
    }

    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        HandlePointerWheelChanged(sender, e);
    }
}

/// <summary>
/// 快捷键定义常量
/// </summary>
public static class KeyboardShortcuts
{
    // 评级快捷键
    public const string Rate0 = "0";
    public const string Rate1 = "1";
    public const string Rate2 = "2";
    public const string Rate3 = "3";
    public const string Rate4 = "4";
    public const string Rate5 = "5";

    // 操作快捷键
    public const string ToggleDelete = "Delete";
    public const string ClearAllDelete = "Ctrl+Shift+Delete";
    public const string SelectAll = "Ctrl+A";
    public const string ClearSelection = "Esc";

    // 导航快捷键
    public const string PreviousImage = "←";
    public const string NextImage = "→";

    // 缩放快捷键
    public const string ZoomIn = "Ctrl+滚轮上";
    public const string ZoomOut = "Ctrl+滚轮下";

    /// <summary>
    /// 获取所有快捷键说明
    /// </summary>
    public static Dictionary<string, string> GetAllShortcuts()
    {
        return new Dictionary<string, string>
        {
            ["0-5"] = "设置评级（0=取消评级）",
            ["Delete"] = "标记/取消预删除",
            ["Ctrl+Shift+Delete"] = "清空所有预删除标记",
            ["Ctrl+A"] = "全选",
            ["Shift+点击"] = "连续选择",
            ["Ctrl+点击"] = "多选",
            ["←"] = "上一张图片",
            ["→"] = "下一张图片",
            ["Ctrl+滚轮"] = "缩放预览",
            ["Esc"] = "清除选择"
        };
    }
}
