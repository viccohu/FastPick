# 缩略图终版重构 - Product Requirement Document

## Overview
- **Summary**: 完全重构 FastPick 的缩略图系统，使用 WIC (Windows Imaging Component) 二级获取策略，完全移除系统缩略图 API，解决黑边、比例不对的问题，同时提供高性能的加载调度和缓存管理。
- **Purpose**: 解决当前系统缩略图 API 的各种问题（黑边、挤压、比例不对），实现高质量、高性能的缩略图显示，达到 Windows 原生照片应用的体验。
- **Target Users**: FastPick 的所有用户，特别是处理大量照片（10,000+）的专业摄影师和摄影爱好者。

## Goals
- **Goal 1**: 完全移除系统缩略图 API，只使用 WIC 获取缩略图
- **Goal 2**: 实现 WIC 二级获取策略（Preview → 原图）
- **Goal 3**: 使用 Fant 过滤算法，256px 目标尺寸，RespectExifOrientation 自动纠正方向
- **Goal 4**: 第一阶段完成核心 WIC 缩略图获取，后续分阶段实现优先级调度和本地缓存
- **Goal 5**: 保持当前的并发控制和取消机制

## Non-Goals (Out of Scope)
- **不做**：第一阶段不实现优先级队列（可见区/周边区/全局区）
- **不做**：第一阶段不实现本地磁盘缓存（WEBP 格式）
- **不做**：第一阶段不实现内存管理增强（WeakReference、内存压力预警）
- **不做**：修改现有的 UI 虚拟化和 ItemsRepeater 实现

## Background & Context
- **当前问题**：
  - 系统缩略图 API（GetThumbnailAsync）有各种坑：
    - ThumbnailMode.PicturesView：有些图固定有黑边
    - ThumbnailMode.SingleItem：没被系统打开过的图有黑边挤压
    - Windows 缩略图缓存（thumbcache.db）只有文件被访问过才生成高质量缩略图
  - 当前实现同时使用系统缩略图和 WIC，逻辑复杂
- **技术约束**：
  - 基于 WinUI 3 (Windows App SDK)
  - 使用 WIC (Windows Imaging Component)
  - 需要支持 JPG 和各种 RAW 格式
- **用户选择**：
  - 完全移除系统缩略图
  - 缩略图尺寸：256px
  - 分阶段实现
  - 本地缓存：WEBP 格式（后续阶段）

## Functional Requirements
- **FR-1**: 实现 WIC 二级获取策略
  - 策略 1：尝试获取 Preview（内嵌 HD 图片）
  - 策略 2：终极保底，从原图解码，使用 Fant 过滤算法缩放到 256px
- **FR-2**: 使用 RespectExifOrientation 自动纠正图片方向
- **FR-3**: 解码时缩放（GetPixelDataAsync 时传入 BitmapTransform），而不是先解码再缩小
- **FR-4**: 使用 BitmapInterpolationMode.Fant 获得最高质量缩放
- **FR-5**: 保持现有的内存缓存（LRU，1000 张）
- **FR-6**: 保持现有的并发控制（SemaphoreSlim，Environment.ProcessorCount）
- **FR-7**: 保持现有的取消令牌机制（快速滚动时取消）

## Non-Functional Requirements
- **NFR-1**: 缩略图质量：无黑边、无挤压、比例正确、方向正确
- **NFR-2**: 加载性能：与当前实现相当或更好
- **NFR-3**: 内存占用：与当前实现相当（1000 张缩略图缓存）
- **NFR-4**: 滚动流畅度：与当前实现相当（防抖 + 取消机制）

## Constraints
- **Technical**: WinUI 3, Windows App SDK, WIC
- **Business**: 分阶段实现，第一阶段只完成核心 WIC 缩略图获取
- **Dependencies**: 无外部依赖，使用 Windows 内置 WIC

## Assumptions
- **Assumption 1**: WIC 的 GetPreviewAsync() 能获取到大多数 RAW 文件的内嵌预览
- **Assumption 2**: 解码时缩放（BitmapTransform）能显著提升性能（5-10倍）
- **Assumption 3**: RespectExifOrientation 能正确处理所有 Exif 方向标记

## Acceptance Criteria

### AC-1: WIC 二级获取策略正常工作
- **Given**: 一个包含各种 JPG 和 RAW 文件的文件夹
- **When**: 加载缩略图
- **Then**: 
  - 优先尝试获取 Preview
  - 如果没有 Preview，从原图解码
- **Verification**: `human-judgment`
- **Notes**: 可以通过调试日志确认使用了哪个策略

### AC-2: 缩略图无黑边、无挤压、比例正确
- **Given**: 各种比例的图片（3:2, 16:9, 4:3, 1:1 等）
- **When**: 查看缩略图
- **Then**: 缩略图保持原图比例，无黑边，无挤压
- **Verification**: `human-judgment`

### AC-3: 缩略图方向正确
- **Given**: 带有 Exif 方向标记的图片（旋转过的照片）
- **When**: 查看缩略图
- **Then**: 缩略图方向正确，不需要手动旋转
- **Verification**: `human-judgment`

### AC-4: 使用 Fant 过滤算法
- **Given**: 需要缩放的图片
- **When**: 生成缩略图
- **Then**: 使用 BitmapInterpolationMode.Fant 获得最高质量
- **Verification**: `programmatic`（代码检查）

### AC-5: 解码时缩放
- **Given**: 大尺寸原图
- **When**: 生成缩略图
- **Then**: 在 GetSoftwareBitmapAsync 时传入 BitmapTransform，而不是先解码再缩小
- **Verification**: `programmatic`（代码检查）

### AC-6: 并发控制正常工作
- **Given**: 快速滚动触发大量缩略图加载
- **When**: 观察系统负载
- **Then**: 同时加载的任务数不超过 Environment.ProcessorCount
- **Verification**: `programmatic`（代码检查 + 调试日志）

### AC-7: 取消机制正常工作
- **Given**: 快速滚动，缩略图还没加载完就滚出视口
- **When**: 观察加载行为
- **Then**: 滚出视口的缩略图加载任务被立即取消
- **Verification**: `human-judgment` + `programmatic`（代码检查）

### AC-8: 内存缓存正常工作
- **Given**: 已加载过的缩略图
- **When**: 再次滚动到该位置
- **Then**: 缩略图立即显示，不需要重新加载
- **Verification**: `human-judgment`

### AC-9: 项目编译成功
- **Given**: 重构完成的代码
- **When**: 编译项目
- **Then**: 编译成功，无错误
- **Verification**: `programmatic`

## Open Questions
- [ ] 后续阶段何时实现优先级队列？
- [ ] 后续阶段何时实现本地 WEBP 缓存？
- [ ] 后续阶段何时实现内存管理增强？
