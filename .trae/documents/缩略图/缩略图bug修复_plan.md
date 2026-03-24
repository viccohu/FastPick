# 缩略图Bug修复 - The Implementation Plan

## [ ] Task 1: 优化 ThumbnailService - 添加智能解码尺寸限制
- **Priority**: P0
- **Depends On**: None
- **Description**: 
  - 在 GetSystemThumbnailAsync 中，只设置 DecodePixelWidth，让系统自动计算高度保持原图比例
  - 设置 DecodePixelWidth 为 260px（UI宽度的2倍，适配高DPI）
  - 移除同时设置 Width 和 Height 的做法
- **Success Criteria**: 
  - 缩略图按原图比例加载，无挤压
  - 解码尺寸有限制，内存占用降低
- **Test Requirements**:
  - `programmatic` TR-1.1: 验证只设置 DecodePixelWidth
  - `human-judgement` TR-1.2: 验证缩略图比例正确
- **Notes**: 参考文档建议，只设置其中一项保持比例

## [ ] Task 2: 优化 ElementClearing - 更积极的内存释放
- **Priority**: P0
- **Depends On**: None
- **Description**: 
  - 在 ThumbnailRepeater_ElementClearing 中，不仅清空 Image.Source
  - 考虑释放 PhotoItem.Thumbnail（但要平衡缓存效果）
  - 确保滚动时内存及时释放
- **Success Criteria**: 
  - 滚动时内存占用不会持续增长
  - 重新进入视口时能正确重新加载
- **Test Requirements**:
  - `programmatic` TR-2.1: 验证 ElementClearing 中的资源释放
  - `human-judgement` TR-2.2: 验证内存使用合理
- **Notes**: 利用现有的 ThumbnailService LRU 缓存机制

## [ ] Task 3: 验证并优化 Image 控件的 Stretch 属性
- **Priority**: P1
- **Depends On**: None
- **Description**: 
  - 检查 ThumbnailItemTemplate 中的 Image 控件
  - 确保使用 Stretch="Uniform" 保持比例
  - 确保 HorizontalAlignment/VerticalAlignment 都是 Center
- **Success Criteria**: 
  - 图片保持原图比例，不会被挤压
  - 多余部分留白，不会裁切
- **Test Requirements**:
  - `programmatic` TR-3.1: 验证 Stretch 属性设置
  - `human-judgement` TR-3.2: 验证视觉效果正确
- **Notes**: 当前代码可能已经正确，需要验证

## [ ] Task 4: 集成测试和验证
- **Priority**: P1
- **Depends On**: Task 1-3
- **Description**: 
  - 编译项目确保无错误
  - 测试不同比例图片（3:2 单反、16:9 手机等）
  - 验证滚动性能和内存占用
- **Success Criteria**: 
  - 项目编译成功
  - 所有比例图片显示正确
  - 滚动流畅，内存合理
- **Test Requirements**:
  - `programmatic` TR-4.1: 项目编译无错误
  - `human-judgement` TR-4.2: 验证所有功能正常工作
- **Notes**: 使用真实的不同比例照片测试
