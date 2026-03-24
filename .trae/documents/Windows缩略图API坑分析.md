# Windows 缩略图 API 坑分析

## 问题观察

### 用户发现的关键现象

1. **ThumbnailMode.SingleItem**
   - 被系统打开过的图：无黑边、无挤压
   - 没开过的图：有黑边挤压（横构图上下挤压）

2. **ThumbnailMode.PicturesView**
   - 有些图固定有黑边

## 问题根源分析

### Windows 缩略图缓存机制的问题

Windows 的缩略图系统（thumbcache.db）有以下特性：

1. **缓存生成条件**
   - 只有当文件被 Windows 资源管理器或其他应用访问过时，才会生成高质量缩略图
   - 未被访问过的文件，系统会快速生成一个"占位缩略图"，这个缩略图可能：
     - 比例不对
     - 有黑边
     - 尺寸不正确

2. **ThumbnailMode.PicturesView 的问题**
   - 这个模式是为"图片库视图"设计的
   - 它返回的缩略图可能是预先裁剪好的正方形或固定比例
   - 不会根据原图比例调整

3. **ThumbnailMode.SingleItem 的优点**
   - 为"单个项目"设计，会尽量保持原图比例
   - 但如果缩略图缓存中没有，系统会快速生成一个低质量版本

## 解决方案

### 方案 1：优先使用 ThumbnailMode.SingleItem + UseCurrentScale
- 使用 `ThumbnailMode.SingleItem` 替代 `ThumbnailMode.PicturesView`
- 添加 `ThumbnailOptions.UseCurrentScale` 选项
- 这个组合会尽量保持原图比例

### 方案 2：完全不依赖系统缩略图，使用 WIC 解码
- 跳过系统缩略图，直接使用 `GenerateWicThumbnailAsync`
- 这样可以完全控制解码尺寸和比例
- 缺点：性能稍慢，但质量可控

### 方案 3：混合策略
- 先尝试系统缩略图（SingleItem 模式）
- 如果检测到比例不对或有黑边，回退到 WIC 解码
- 需要判断缩略图是否"正常"

### 方案 4：强制让 Windows 生成高质量缩略图
- 在获取缩略图前，先调用一些 API 让 Windows 认为文件已被访问
- 例如：`GetBasicPropertiesAsync()`、`GetThumbnailAsync(ThumbnailMode.ListView)`
- 但这可能会很慢

## 推荐方案

### 推荐：方案 1（最简单）+ 方案 3（兜底）
1. 优先使用 `ThumbnailMode.SingleItem` + `UseCurrentScale`
2. 如果返回的缩略图宽高比与原图明显不符，回退到 WIC 解码
3. WIC 解码时精确计算比例

## 需要确认
您希望采用哪个方案？
