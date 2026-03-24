# 缓存管理功能实施计划

## 目标
在设置界面增加缓存管理功能，包括：
1. 按钮点击跳转到缓存目录（资源管理器打开）
2. 按钮手动清除缓存

## 当前状态分析

### 已有功能
- `ThumbnailService.ClearCacheAsync()` - 清理内存缓存
- `ThumbnailService.GetCacheStatsAsync()` - 获取缓存统计
- `ThumbnailService.CleanupLocalCacheAsync()` - 清理本地缓存（过期和超出大小限制）
- `_cachePath` - 缓存目录路径（私有字段）

### 需要新增
1. 公开方法获取缓存目录路径
2. 公开方法清除所有本地缓存文件
3. 设置界面UI和交互逻辑

---

## [x] 任务1: 在 ThumbnailService 中添加缓存管理公开方法
- **优先级**: P0
- **依赖**: 无
- **描述**:
  - 添加 `GetCachePathAsync()` 方法，返回缓存目录路径
  - 添加 `ClearLocalCacheAsync()` 方法，删除所有本地缓存文件
- **成功标准**:
  - 方法能正确返回缓存目录路径
  - 方法能正确删除所有本地缓存文件
- **测试要求**:
  - `programmatic` TR-1.1: 调用 `GetCachePathAsync()` 返回非空路径
  - `programmatic` TR-1.2: 调用 `ClearLocalCacheAsync()` 后缓存目录为空或不存在

---

## [x] 任务2: 在设置界面添加缓存管理UI
- **优先级**: P0
- **依赖**: 任务1
- **描述**:
  - 在 SettingsPage.xaml 左列"删除设置"下方添加"缓存管理"区域
  - 显示缓存大小信息
  - 添加"打开缓存目录"按钮
  - 添加"清除缓存"按钮
- **成功标准**:
  - UI正确显示缓存管理区域
  - 按钮可点击
- **测试要求**:
  - `human-judgement` TR-2.1: UI布局合理，与现有设置风格一致
  - `programmatic` TR-2.2: 按钮点击事件正确绑定

---

## [x] 任务3: 实现缓存管理交互逻辑
- **优先级**: P0
- **依赖**: 任务2
- **描述**:
  - 实现"打开缓存目录"按钮点击事件，使用 `Launcher.LaunchFolderPathAsync` 打开资源管理器
  - 实现"清除缓存"按钮点击事件，调用 ThumbnailService 清除缓存
  - 添加确认对话框，防止误操作
  - 显示缓存大小信息
- **成功标准**:
  - 点击"打开缓存目录"能正确打开资源管理器
  - 点击"清除缓存"能正确清除缓存
  - 有确认对话框防止误操作
- **测试要求**:
  - `programmatic` TR-3.1: 点击"打开缓存目录"后资源管理器打开正确目录
  - `programmatic` TR-3.2: 点击"清除缓存"并确认后，缓存被清除
  - `human-judgement` TR-3.3: 确认对话框提示清晰

---

## [x] 任务4: 添加缓存大小显示功能
- **优先级**: P1
- **依赖**: 任务1
- **描述**:
  - 在 ThumbnailService 中添加 `GetLocalCacheSizeAsync()` 方法
  - 计算缓存目录的总大小
  - 在设置界面显示缓存大小
- **成功标准**:
  - 能正确计算并显示缓存大小
- **测试要求**:
  - `programmatic` TR-4.1: 返回的缓存大小与实际文件大小一致

---

## [x] 任务5: 测试和验证
- **优先级**: P0
- **依赖**: 任务1-4
- **描述**:
  - 构建项目确保无错误
  - 测试所有功能正常工作
- **成功标准**:
  - 项目构建成功
  - 所有功能正常工作
- **测试要求**:
  - `programmatic` TR-5.1: `dotnet build` 无错误
  - `human-judgement` TR-5.2: 功能测试通过
