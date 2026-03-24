# 完全部加载后拖动卡顿修复 - The Implementation Plan

## [ ] Task 1: 进一步优化 ElementPrepared，减少快速拖动时的压力
- **Priority**: P0
- **Depends On**: None
- **Description**: 
  - 增大缓冲延迟到 50-60ms，更激进地过滤中间请求
  - 确保 Task.Delay 和取消令牌正确工作
  - 检查是否还有其他可以优化的点
- **Success Criteria**: 
  - 完全部加载后拖动也不卡
- **Test Requirements**:
  - `programmatic` TR-1.1: 项目编译无错误
  - `human-judgement` TR-1.2: 完全部加载后拖动流畅
- **Notes**: 10,000+ 张图片时拖动不卡是关键

## [ ] Task 2: 验证 ElementClearing 是否正确取消所有任务
- **Priority**: P1
- **Depends On**: Task 1
- **Description**: 
  - 确保 ElementClearing 能立即取消正在进行的 Task.Delay 和缩略图加载
  - 验证没有僵尸任务继续运行
- **Success Criteria**: 
  - 滑出视口的任务立即取消
- **Test Requirements**:
  - `human-judgement` TR-2.1: 验证资源释放正常

## [ ] Task 3: 集成测试和验证
- **Priority**: P1
- **Depends On**: Task 1-2
- **Description**: 
  - 编译项目确保无错误
  - 测试完全部加载后拖动滚动条
  - 验证缩略图正确加载
- **Success Criteria**: 
  - 项目编译成功
  - 完全部加载后拖动流畅不卡顿
  - 缩略图能正常加载显示
- **Test Requirements**:
  - `programmatic` TR-3.1: 项目编译无错误
  - `human-judgement` TR-3.2: 验证功能正常工作
