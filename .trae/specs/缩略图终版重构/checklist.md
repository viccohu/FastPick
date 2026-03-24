# 缩略图终版重构 - Verification Checklist

- [ ] ThumbnailWidth 已从 260 改为 256
- [ ] ThumbnailHeight 已移除
- [ ] 代码中包含 decoder.GetPreviewAsync() 调用（策略 1）
- [ ] 代码中包含原图解码逻辑（策略 2）
- [ ] 调试日志记录使用了哪个策略
- [ ] 代码中包含 BitmapInterpolationMode.Fant
- [ ] 代码中在 GetSoftwareBitmapAsync 时传入 BitmapTransform
- [ ] 代码中包含 ExifOrientationMode.RespectExifOrientation
- [ ] GetSystemThumbnailAsync() 方法已移除
- [ ] GetThumbnailAsync() 中不再调用系统缩略图 API
- [ ] Windows.Storage.FileProperties using 引用已移除
- [ ] 项目编译成功，无错误
- [ ] 缩略图无黑边、无挤压、比例正确
- [ ] 缩略图方向正确
- [ ] 并发控制正常工作
- [ ] 取消机制正常工作
- [ ] 内存缓存正常工作
