# 增量加载滚动条修复 - The Implementation Plan

## [ ] Task 1: 优化 LoadIncrementalAsync，减少 UI 线程阻塞
- **Priority**: P0
- **Depends On**: None
- **Description**: 
  - 优化增量加载，减少每批次对 UI 线程的阻塞
  - 考虑在后台线程准备好所有数据，然后一次性或分批添加
  - 减少 ApplyFilter() 的调用频率
- **Success Criteria**: 
  - 快速拉动滚动条到未加载区域不卡
- **Test Requirements**:
  - `programmatic` TR-1.1: 项目编译无错误
  - `human-judgement` TR-1.2: 快速拉动到未加载区域流畅
- **Notes**: 观察 Win11 看图软件的行为：快速拉动时滚动条会暂时停止，等增量完成后可以继续

## [ ] Task 2: 验证 ItemsRepeater 虚拟化与数据加载的配合
- **Priority**: P1
- **Depends On**: Task 1
- **Description**: 
  - 检查 ItemsRepeater 在数据动态加载时的行为
  - 确保没有竞态条件或布局抖动
- **Success Criteria**: 
  - 数据加载和滚动配合流畅
- **Test Requirements**:
  - `human-judgement` TR-2.1: 验证滚动和加载的协同

## [ ] Task 3: 集成测试和验证
- **Priority**: P1
- **Depends On**: Task 1-2
- **Description**: 
  - 编译项目确保无错误
  - 测试快速拉动滚动条到未加载区域
  - 验证整个加载流程
- **Success Criteria**: 
  - 项目编译成功
  - 快速拉动到未加载区域流畅
  - 增量加载正常工作
- **Test Requirements**:
  - `programmatic` TR-3.1: 项目编译无错误
  - `human-judgement` TR-3.2: 验证功能正常工作
