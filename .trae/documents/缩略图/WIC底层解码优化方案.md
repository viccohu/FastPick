# WIC 底层解码优化方案

## 核心原则
**能不全解码，就绝不全解码**

## 技术方案

### 1. 使用 IWICBitmapSourceTransform 底层缩放
- 跳过 GetPreviewAsync 和 GetThumbnailAsync
- 直接在解码阶段就进行缩放
- 利用 WIC 的硬件加速能力

### 2. 解码时缩放 vs 解码后缩放
- **解码时缩放**：在 GetPixelDataAsync 时传入 BitmapTransform
  - 性能：⭐⭐⭐⭐⭐（最高）
  - 原理：WIC 只读取需要的像素，跳过不必要的数据
  - 文档建议：性能提升 5-10 倍

- **解码后缩放**：先完整解码，再缩小
  - 性能：⭐⭐（较低）
  - 原理：读取全部像素，浪费 I/O 和内存

### 3. 目标尺寸
- 建议：256px（长边）
- 自适应：根据原图宽高比计算
- 保持原始比例

### 4. 具体优化点

#### 解码时缩放（必须）
```csharp
var transform = new BitmapTransform
{
    InterpolationMode = BitmapInterpolationMode.Fant, // 最高质量
    ScaledWidth = scaledWidth,
    ScaledHeight = scaledHeight
};

var softwareBitmap = await decoder.GetSoftwareBitmapAsync(
    BitmapPixelFormat.Bgra8,
    BitmapAlphaMode.Premultiplied,
    transform, // 解码时就缩放！
    ExifOrientationMode.RespectExifOrientation,
    ColorManagementMode.DoNotColorManage);
```

#### 使用 BitmapInterpolationMode.Fant
- 质量最高，但开销稍大
- 对于缩略图来说，质量优先

#### 保持现有优化
- LRU 内存缓存（1000 张）
- 并发控制（SemaphoreSlim）
- 取消令牌机制

## 预期性能提升

| 指标 | 当前方案 | 优化方案 | 提升 |
|------|---------|---------|------|
| 解码性能 | 原图解码 + 后缩放 | 解码时缩放 | 5-10 倍 |
| 内存占用 | 高（完整解码） | 低（按需读取） | 显著降低 |
| I/O 压力 | 高（读取全图） | 低（只读取需要的部分） | 显著降低 |

## 实施计划

### Phase 1: 验证方案可行性
- 确认当前代码已在解码时缩放
- 检查是否使用 BitmapInterpolationMode.Fant
- 确认 ExifOrientationMode.RespectExifOrientation

### Phase 2: 基准测试
- 测量当前性能
- 测试不同目标尺寸（256px vs 512px）
- 测试不同插值算法

### Phase 3: 优化调整
- 根据测试结果微调参数
- 可能的优化：
  - 调整目标尺寸
  - 调整插值算法
  - 调整缓存大小

## 关键验证点

1. ✅ 解码时缩放（在 GetSoftwareBitmapAsync 时传入 BitmapTransform）
2. ✅ BitmapInterpolationMode.Fant（最高质量）
3. ✅ ExifOrientationMode.RespectExifOrientation（自动纠正方向）
4. ✅ 目标尺寸约 256px（长边）
5. ✅ 保持原始宽高比
