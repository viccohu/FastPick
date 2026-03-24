# 缩略图终版重构 - The Implementation Plan (Decomposed and Prioritized Task List)

## \[ ] Task 1: 修改缩略图尺寸为 256px

- **Priority**: P0
- **Depends On**: None
- **Description**:
  - 将 ThumbnailService 中的 ThumbnailWidth 从 260 改为 256
  - 移除 ThumbnailHeight 配置（只按宽度缩放）
- **Acceptance Criteria Addressed**: \[AC-9]
- **Test Requirements**:
  - `programmatic` TR-1.1: 项目编译无错误
- **Notes**: 简单的配置修改

## \[ ] Task 2: 实现 WIC 二级获取策略 - Preview

- **Priority**: P0
- **Depends On**: Task 1
- **Description**:
  - 在 GenerateWicThumbnailAsync 中优先尝试 decoder.GetPreviewAsync()
  - 如果成功获取 Preview，使用 Preview 解码
  - 添加调试日志，记录使用了哪个策略
- **Acceptance Criteria Addressed**: \[AC-1, AC-9]
- **Test Requirements**:
  - `programmatic` TR-2.1: 代码中包含 GetPreviewAsync() 调用
  - `human-judgement` TR-2.2: 调试日志显示使用了哪个策略
- **Notes**: 策略 1：Preview

## \[ ] Task 3: 实现 WIC 二级获取策略 - 原图保底

- **Priority**: P0
- **Depends On**: Task 2
- **Description**:
  - 如果 Preview 失败，使用原图解码（当前的逻辑）
  - 确保使用 Fant 过滤算法
  - 确保解码时缩放（BitmapTransform）
  - 确保使用 RespectExifOrientation
- **Acceptance Criteria Addressed**: \[AC-1, AC-4, AC-5, AC-9]
- **Test Requirements**:
  - `programmatic` TR-3.1: 代码中包含 BitmapInterpolationMode.Fant
  - `programmatic` TR-3.2: 代码中在 GetSoftwareBitmapAsync 时传入 BitmapTransform
  - `programmatic` TR-3.3: 代码中包含 ExifOrientationMode.RespectExifOrientation
- **Notes**: 策略 2：原图保底

## \[ ] Task 4: 完全移除系统缩略图 API

- **Priority**: P0
- **Depends On**: Task 3
- **Description**:
  - 移除 GetSystemThumbnailAsync() 方法
  - 从 GetThumbnailAsync() 中移除对系统缩略图的调用
  - 直接调用 WIC 方法
  - 移除相关的 using 引用（Windows.Storage.FileProperties）
- **Acceptance Criteria Addressed**: \[AC-9]
- **Test Requirements**:
  - `programmatic` TR-4.1: 项目编译无错误
  - `programmatic` TR-4.2: 代码中不再有 GetThumbnailAsync()（系统 API）调用
- **Notes**: 完全移除系统缩略图

## \[ ] Task 5: 测试缩略图质量（无黑边、无挤压、比例正确）

- **Priority**: P1
- **Depends On**: Task 4
- **Description**:
  - 运行应用，加载各种比例的图片
  - 验证缩略图无黑边、无挤压、比例正确
- **Acceptance Criteria Addressed**: \[AC-2]
- **Test Requirements**:
  - `human-judgement` TR-5.1: 验证缩略图无黑边、无挤压、比例正确
- **Notes**: 手动测试

## \[ ] Task 6: 测试缩略图方向正确

- **Priority**: P1
- **Depends On**: Task 5
- **Description**:
  - 运行应用，加载带有 Exif 方向标记的图片
  - 验证缩略图方向正确
- **Acceptance Criteria Addressed**: \[AC-3]
- **Test Requirements**:
  - `human-judgement` TR-6.1: 验证缩略图方向正确
- **Notes**: 手动测试

## \[ ] Task 7: 测试并发控制和取消机制

- **Priority**: P1
- **Depends On**: Task 6
- **Description**:
  - 快速滚动，验证并发控制正常
  - 快速滚动，验证取消机制正常
- **Acceptance Criteria Addressed**: \[AC-6, AC-7]
- **Test Requirements**:
  - `human-judgement` TR-7.1: 验证并发控制正常
  - `human-judgement` TR-7.2: 验证取消机制正常
- **Notes**: 手动测试

## \[ ] Task 8: 测试内存缓存

- **Priority**: P1
- **Depends On**: Task 7
- **Description**:
  - 滚动加载缩略图
  - 再次滚动到该位置
  - 验证缩略图立即显示，不需要重新加载
- **Acceptance Criteria Addressed**: \[AC-8]
- **Test Requirements**:
  - `human-judgement` TR-8.1: 验证内存缓存正常
- **Notes**: 手动测试

