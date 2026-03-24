# FastPick 缩略图重构 - Verification Checklist

- [ ] XAML 结构检查：ItemsRepeater 能正确获取 ScrollViewer 视口信息
- [ ] 所有 Image 控件（XAML 和代码）都设置了 DecodePixelHeight/DecodePixelWidth
- [ ] 使用 SemaphoreSlim 限制了并发解码线程数
- [ ] 实现了三级查找缓存策略（内存 → Shell → WIC）
- [ ] 使用 ItemsRepeater.ElementPrepared 而非 ViewChanged 触发加载
- [ ] ItemsRepeater.ElementClearing 事件中正确释放缩略图资源
- [ ] 文件夹切换时显式调用了 GC.Collect()
- [ ] 所有文件 IO 和解码操作都在非 UI 线程执行
- [ ] 项目成功编译，无错误
- [ ] 滚动时 UI 保持流畅，无明显卡顿
- [ ] 加载 10,000+ 图片时内存使用合理，无 OOM
