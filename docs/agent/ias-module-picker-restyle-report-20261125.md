# IAS 目标装备下拉重做报告（2026-11-25）

## 改动文件
- `HD2ModManager/Views/IasTargetPicker.xaml`：重写。闭合态 32px 白底输入框（SurfaceCard / StrokeControl / RadiusControl），左 22px 图标 + 名字（省略号）+ 右侧箭头；展开或键盘焦点时边框变 Accent，未选合法项时仍为警告色。弹层 320px、8px 圆角、6px 内边距；顶部 30px 搜索框（放大镜 + 占位「搜索名字或编号」）；分组标题 11px 次级色（8,8,8,3）；行 30px、无边框、5px 圆角、左对齐，悬停 SurfaceInteractiveHover，选中为 SurfaceSelected + Accent 文字 + 右侧对勾；最高 320px 后滚动。
- `HD2ModManager/Views/IasTargetPicker.xaml.cs`：打开时搜索框取焦并把已选项滚入可见；Up/Down 在搜索框与列表行间移动焦点；Enter 选中焦点行（搜索框内且有关键字时选第一个真实匹配）；Escape 关闭；行点击走 SelectCommand。
- `HD2ModManager/ViewModels/IasTargetPickerViewModel.cs`：新增 `SelectionIconPath`；选中/换列表时补发 `Selected` 与图标变更通知；「原来那套」行的名字改为「原来那套：<名字>」（无名字时保持旧文案）。
- `HD2ModManager/Views/IasPageView.xaml`：VPK 底子区改为标题块「求生模型装到哪套装备上」+ 说明（可换行）+ 带标签的「护甲」「头盔」两个选择器（WrapPanel，窄时换行）；逐角色转换面板标签改为「目标护甲」「目标头盔」，警告文字可换行。
- `HD2ModCore.Tests/IasPageViewModelTests.cs`：追加断言（默认行名字、无图标）。

## 使用情况
四个选择器（VPK 护甲/头盔、逐角色护甲/头盔）仍共用同一个 DataTemplate -> `IasTargetPicker`，数据源与过滤、持久化、失效警告未变。图标取自 `IasTargetChoice.IconPath`（IasModuleService 已按归档 id 填充）。`VpkTargetPicker` 未动。

## 验证
- `dotnet build -c Debug -nodeReuse:false`：0 警告 0 错误。
- `dotnet test -c Debug --no-build -nodeReuse:false`：全绿（HD2ModCore.Tests 1794 通过等）。

## 未做 / 注意
- 未启动应用，视觉与键盘交互未实机验证（仅编译与单测）。
- ToggleButton + `StaysOpen=False` 弹层：展开时再点闭合框可能出现关了又开的老问题（原实现即如此）。
- 未提交。
