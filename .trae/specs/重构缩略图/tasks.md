# FastPick 缩略图重构 - The Implementation Plan (Decomposed and Prioritized Task List)

## [ ] Task 1: 检查并优化 MainPage.xaml 的 XAML 结构
- **Priority**: P0
- **Depends On**: None
- **Description**: 
  - 检查当前 ScrollViewer → Grid → ItemsRepeater 的嵌套结构
  - 评估是否需要移除中间的 Grid 或确保它不会破坏虚拟化
  - 检查 Image 控件是否设置了 DecodePixelHeight/DecodePixelWidth
  - 考虑添加 CanContentRenderOutsideBounds="False"
- **Acceptance Criteria Addressed**: [AC-1, AC-4]
- **Test Requirements**:
  - `programmatic` TR-1.1: 检查 XAML 结构是否确保 ItemsRepeater 正确获取视口信息
  - `programmatic` TR-1.2: 验证所有 Image 控件都设置了解码尺寸
- **Notes**: 参考文档中的优化建议，当前结构可能需要微调

## [ ] Task 2: 分析并优化 ThumbnailService
- **Priority**: P0
- **Depends On**: None
- **Description**: 
  - 分析当前 ThumbnailService 的实现
  - 实现三级查找缓存策略（内存 → Shell → WIC）
  - 添加 SemaphoreSlim 并发控制（建议 Environment.ProcessorCount）
  - 确保 BitmapImage 设置 DecodePixelHeight/DecodePixelWidth
- **Acceptance Criteria Addressed**: [AC-2, AC-3, AC-4]
- **Test Requirements**:
  - `programmatic` TR-2.1: 验证三级查找逻辑的实现
  - `programmatic` TR-2.2: 验证 SemaphoreSlim 正确限制并发
  - `programmatic` TR-2.3: 验证所有 BitmapImage 都设置了解码尺寸
- **Notes**: 基于现有 ThumbnailService 优化而非完全重写

## [ ] Task 3: 优化 MainPage.xaml.cs 中的 ElementPrepared 事件
- **Priority**: P0
- **Depends On**: Task 1, Task 2
- **Description**: 
  - 优化 ThumbnailRepeater_ElementPrepared 事件处理
  - 确保使用 ElementPrepared 而非 ViewChanged 触发加载
  - 实现事件驱动的按需加载，仅加载可见区域的缩略图
- **Acceptance Criteria Addressed**: [AC-5]
- **Test Requirements**:
  - `programmatic` TR-3.1: 验证 ElementPrepared 事件正确触发加载
  - `programmatic` TR-3.2: 验证仅加载可见区域的缩略图
- **Notes**: 当前已有 ElementPrepared 事件，需要优化其逻辑

## [ ] Task 4: 优化 ElementClearing 事件和资源释放
- **Priority**: P0
- **Depends On**: Task 3
- **Description**: 
  - 优化 ThumbnailRepeater_ElementClearing 事件处理
  - 确保滚出视口时及时释放缩略图资源
  - 防止内存泄漏
- **Acceptance Criteria Addressed**: [AC-6]
- **Test Requirements**:
  - `programmatic` TR-4.1: 验证 ElementClearing 事件中的资源释放逻辑
  - `programmatic` TR-4.2: 验证缩略图资源被正确释放
- **Notes**: 当前已有 ElementClearing 事件，需要优化

## [ ] Task 5: 在 MainViewModel 中添加文件夹切换时的 GC 调用
- **Priority**: P1
- **Depends On**: Task 4
- **Description**: 
  - 在 LoadPhotosAsync 方法中，清空数据后显式调用 GC.Collect()
  - 清理缩略图缓存
- **Acceptance Criteria Addressed**: [AC-7]
- **Test Requirements**:
  - `programmatic` TR-5.1: 验证文件夹切换时调用 GC.Collect()
  - `programmatic` TR-5.2: 验证缩略图缓存被正确清理
- **Notes**: 确保在清空 PhotoItems 后调用 GC

## [ ] Task 6: 集成测试和性能验证
- **Priority**: P1
- **Depends On**: Task 1-5
- **Description**: 
  - 编译并运行项目
  - 测试 10,000+ 图片文件夹的加载性能
  - 验证滚动流畅性
  - 检查内存使用情况
- **Acceptance Criteria Addressed**: [AC-8]
- **Test Requirements**:
  - `programmatic` TR-6.1: 项目成功编译，无错误
  - `human-judgement` TR-6.2: UI 流畅，无明显卡顿
  - `human-judgement` TR-6.3: 内存使用合理，无 OOM
- **Notes**: 建议使用真实的大量照片进行测试
