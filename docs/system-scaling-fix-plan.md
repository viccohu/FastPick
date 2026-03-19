# Windows 系统缩放修复方案

## 问题描述

在 Windows 系统设置了缩放比例（如 125%）时，FastPick 应用中预览图片的 100% 显示大小并不是实际像素尺寸的 100%，而是被系统缩放后的尺寸。

### 根本原因
WinUI 3 应用会自动适应系统 DPI 缩放，导致：
- 1 个逻辑像素 = 系统缩放比例 × 物理像素
- 例如：系统缩放 125% 时，1 个逻辑像素 = 1.25 个物理像素
- 当我们将 `_zoomScale` 设置为 1.0 来显示原图 100% 时，实际显示的是原图尺寸 × 系统缩放比例

## 解决方案

### 核心思路
获取系统 DPI 缩放比例，在所有缩放计算中进行补偿，使得：
- 预览图的 100% = 实际物理像素的 100%
- 无论系统缩放比例是多少，都保持这一关系

### 具体实现步骤

#### 1. 添加获取 DPI 缩放比例的辅助方法
```csharp
private double GetDpiScale()
{
    if (_window?.Content?.XamlRoot != null)
    {
        return _window.Content.XamlRoot.RasterizationScale;
    }
    return 1.0;
}
```

#### 2. 修改 `CalculateFitToScreenScale` 方法
在计算适应屏幕比例时，考虑 DPI 缩放：
- 容器尺寸已经是逻辑像素尺寸
- 原图尺寸是物理像素尺寸
- 需要将原图尺寸转换为逻辑像素尺寸来计算

#### 3. 修改 `CalculateOriginalScale` 方法
在计算原图 100% 比例时，除以 DPI 缩放比例进行补偿。

#### 4. 更新 `UpdateZoomIndicator` 方法
确保缩放指示器的计算也考虑 DPI 缩放。

#### 5. 更新 `ZoomComboBox_SelectionChanged` 方法
确保下拉框缩放选择也考虑 DPI 缩放。

## 修改文件列表

1. `d:\FastPick\Views\MainPage.xaml.cs`
   - 添加 `GetDpiScale()` 辅助方法
   - 修改 `CalculateFitToScreenScale()`
   - 修改 `CalculateOriginalScale()`
   - 修改 `UpdateZoomIndicator()`
   - 修改 `ZoomComboBox_SelectionChanged()`

## 验证方案

### 测试场景
1. 系统缩放 100%：确保功能正常
2. 系统缩放 125%：验证 100% 显示为实际尺寸
3. 系统缩放 150%：验证 100% 显示为实际尺寸
4. 系统缩放 200%：验证 100% 显示为实际尺寸

### 验证步骤
1. 打开一张已知尺寸的图片（如 1920×1080）
2. 点击"实际尺寸"按钮
3. 测量屏幕上显示的图片宽度和高度
4. 验证是否与原图物理像素尺寸一致

## 优点

1. **优雅简洁**：只修改核心缩放计算逻辑，不影响其他功能
2. **向后兼容**：系统缩放 100% 时行为与之前一致
3. **自适应**：自动适应任意系统缩放比例
4. **性能好**：DPI 缩放比例获取成本极低

## 注意事项

1. 需要确保在窗口加载后再获取 DPI 缩放比例
2. 监听 DPI 缩放变化事件（可选，未来扩展）
3. 高分辨率图片加载逻辑不受影响
