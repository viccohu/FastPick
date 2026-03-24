# FastPick 缩略图重构 - Product Requirement Document

## Overview

* **Summary**: 对 FastPick 项目的缩略图加载系统进行重构，基于当前实现问题的深度分析，修复虚拟化失效、优化加载流水线、提升大批量（10,000+）图片的处理性能

* **Purpose**: 解决当前 ItemsRepeater 虚拟化可能失效、水平滚动卡顿、内存占用过高的问题，实现真正高性能的缩略图浏览体验

* **Target Users**: FastPick 的所有用户，特别是处理大量照片的专业摄影师和摄影爱好者

## Goals

* 修复 UI 虚拟化失效隐患，确保 ItemsRepeater 正确感知 ScrollViewer 视口

* 优化缩略图加载流水线，实现三级查找策略（内存缓存 → Shell 缓存 → WIC 解码）

* 使用信号量控制并发解码线程数，防止 IO 阻塞

* 确保所有 Image 控件设置 DecodePixelHeight/DecodePixelWidth，禁止原图解码

* 优化内存管理，在滚出视口时及时释放资源，切换文件夹时显式 GC

* 保持与现有代码的兼容性，不破坏评级、筛选、删除等核心功能

## Non-Goals (Out of Scope)

* 不修改现有文件扫描和配对逻辑（已有 ImageScanService）

* 不重写 PhotoItem 模型（基于现有模型优化）

* 不修改预览功能（已在上一步移除）

* 不修改评级、筛选、删除等核心业务功能

## Background & Context

### 当前实现的关键问题（来自文档分析）

1. **UI 虚拟化可能失效**：ScrollViewer → Grid → ItemsRepeater 的嵌套结构，中间 Grid 可能导致 ItemsRepeater 认为有无限空间
2. **水平滚动性能问题**：缺少 CanContentRenderOutsideBounds="False" 显式声明
3. **缩略图尺寸限制缺失**：Image 控件可能未设置 DecodePixelHeight，导致原图解码
4. **加载事件选择不当**：可能使用了 ViewChanged 而非 ElementPrepared 触发加载

### 现有代码基础

* 已有 PhotoItem 模型，包含 JPG 和 RAW 文件配对逻辑

* 已有 ThumbnailService 负责缩略图加载

* 使用 ItemsRepeater 实现虚拟化滚动

* 之前已移除预览功能，为缩略图重构扫清了障碍

## Functional Requirements

* **FR-1**: 检查并优化 XAML 结构，确保 ItemsRepeater 能正确获取 ScrollViewer 视口信息

* **FR-2**: 优化 ThumbnailService，实现三级查找缓存策略（内存 → Shell → WIC）

* **FR-3**: 在 ThumbnailService 中添加 SemaphoreSlim 并发控制

* **FR-4**: 确保所有 Image 控件（包括 XAML 和代码创建的）都设置 DecodePixelHeight/DecodePixelWidth

* **FR-5**: 优化 ItemsRepeater.ElementPrepared 事件，实现事件驱动的按需加载

* **FR-6**: 优化 ItemsRepeater.ElementClearing 事件，滚出视口时及时释放缩略图资源

* **FR-7**: 在 MainViewModel.LoadPhotosAsync 中，文件夹切换时显式调用 GC.Collect() 并清理缓存

## Non-Functional Requirements

* **NFR-1**: 所有文件 IO 和解码操作必须在非 UI 线程执行

* **NFR-2**: 加载 10,000+ 图片时内存使用保持合理，无 OOM

* **NFR-3**: 滚动时保持流畅，无明显卡顿

* **NFR-4**: 缩略图加载速度优化，优先使用缓存

## Constraints

* **Technical**: WinUI 3 (Windows App SDK)、.NET 9.0

* **Dependencies**: 必须保留与现有 PhotoItem、MainViewModel、MainPage 的兼容性

* **UI 兼容性**: 保持现有 UI 布局和视觉风格不变

## Assumptions

* 当前的文件配对逻辑（ImageScanService）工作正常

* ItemsRepeater 的基础虚拟化机制是正确的，问题在于嵌套结构

* 用户主要关注缩略图浏览性能，不影响其他功能

* 可以基于现有 ThumbnailService 优化而非完全重写

## Acceptance Criteria

### AC-1: UI 虚拟化正确工作

* **Given**: 用户打开包含 10,000+ 图片的文件夹

* **When**: 观察 ItemsRepeater 的行为

* **Then**: ItemsRepeater 只渲染可见区域的元素，不会一次性渲染所有元素

* **Verification**: `programmatic`

* **Notes**: 检查 XAML 结构和虚拟化行为

### AC-2: 三级缓存策略实现

* **Given**: 缩略图加载请求

* **When**: 加载缩略图时

* **Then**: 按顺序检查内存缓存 → Shell 缓存 → WIC 解码

* **Verification**: `programmatic`

* **Notes**: 验证 ThumbnailService 中的三级查找逻辑

### AC-3: 并发控制正确

* **Given**: 多个缩略图同时请求加载

* **When**: 达到最大并发数限制

* **Then**: 后续请求排队等待，不超过 SemaphoreSlim 限制的线程数

* **Verification**: `programmatic`

* **Notes**: 检查 SemaphoreSlim 是否正确限制并发（建议 Environment.ProcessorCount）

### AC-4: 解码尺寸限制

* **Given**: 任何 Image 控件或 BitmapImage 初始化

* **When**: 显示或加载缩略图时

* **Then**: 必须设置 DecodePixelHeight 或 DecodePixelWidth，禁止原图解码

* **Verification**: `programmatic`

* **Notes**: 检查 XAML 中的 Image 控件和代码中创建的 BitmapImage

### AC-5: 事件驱动加载

* **Given**: 缩略图项通过 ItemsRepeater.ElementPrepared 进入视口

* **When**: ElementPrepared 事件触发

* **Then**: 触发异步加载流程，仅加载可见区域的缩略图

* **Verification**: `programmatic`

* **Notes**: 验证使用 ElementPrepared 而非 ViewChanged

### AC-6: 资源释放及时

* **Given**: 缩略图项滚出视口

* **When**: ItemsRepeater.ElementClearing 事件触发

* **Then**: 释放缩略图资源，防止内存泄漏

* **Verification**: `programmatic`

* **Notes**: 检查 ElementClearing 事件中的资源释放逻辑

### AC-7: 内存管理优化

* **Given**: 用户切换到新文件夹

* **When**: 清空现有数据并加载新数据

* **Then**: 显式调用 GC.Collect() 并清理图片流缓存

* **Verification**: `programmatic`

* **Notes**: 验证文件夹切换时的 GC 调用

### AC-8: UI 流畅性

* **Given**: 包含 10,000+ 图片的文件夹

* **When**: 用户快速滚动缩略图列表

* **Then**: UI 保持流畅，无明显卡顿

* **Verification**: `human-judgment`

* **Notes**: 通过实际使用体验验证

## Open Questions

* [ ] 当前 XAML 结构中 ScrollViewer → Grid → ItemsRepeater 的嵌套是否真的导致虚拟化问题？

* [ ] 内存缓存的大小限制应该设置为多少？

* [ ] 并发线程数应该设置为多少（文档建议 Environment.ProcessorCount）？

* [ ] 是否需要添加 CanContentRenderOutsideBounds="False" 到 ScrollViewer？

