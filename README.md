# PotDanmaku

<img src="src/PotDanmaku.App/Assets/PotDanmaku.svg" width="72" alt="PotDanmaku 图标">

为 PotPlayer 添加本地弹幕。支持 XML、JSON 和 ASS，弹幕通过独立透明窗口显示，跟随播放、暂停、跳转和倍速，保留播放器原有的对白字幕。

[下载 Windows x64 便携版](https://github.com/1811960541/PotDanmaku/releases/download/v0.2.0-alpha.1/PotDanmaku-0.2.0-alpha.1-portable-win-x64.zip) · [发行说明](https://github.com/1811960541/PotDanmaku/releases/tag/v0.2.0-alpha.1) · [使用指南](docs/使用说明.md)

当前版本：**0.2.0-alpha.1**，预发布版本。

## 功能

- **三种文件格式**：导入 B 站 XML、`potdanmaku/v1` JSON 和 ASS 弹幕，支持选择文件或拖入。
- **播放同步**：同步暂停、恢复、进度跳转及倍速，支持日常 3 倍速观看。
- **自动加载与切集**：查找视频同目录的同名弹幕，切换下一集后自动更换；找不到匹配文件时清除旧弹幕。
- **视频设置记忆**：按视频路径保存弹幕关联、时间偏移、显示设置和 ASS 筛选选项。
- **显示调整**：支持滚动、反向、顶部和底部固定弹幕，可调整字号、颜色、透明度、区域、密度、速度和边距。
- **后台观看**：操作其他软件时，可见视频上的弹幕继续显示；支持鼠标穿透、托盘和弹幕显示开关。

## 下载与运行

在 [Releases](https://github.com/1811960541/PotDanmaku/releases) 下载 `PotDanmaku-…-portable-win-x64.zip`，完整解压后运行 `PotDanmaku.App.exe`。

| 项目 | 要求 |
| --- | --- |
| 系统 | Windows x64；已在 Windows 11 验证，其他系统兼容性仍待确认 |
| 播放器 | 另行安装 PotPlayer；便携包不包含播放器 |
| 图形环境 | 支持 D3D11 的显卡和驱动 |
| .NET | 便携包自带运行环境，无需安装 .NET 或 SDK |

程序需要完整的解压目录，移动或升级时请保留配套文件。版本页面同时提供 `.zip.sha256` 校验文件。

## 快速开始

1. 将弹幕与视频放在同一目录，并使用相同主文件名：

   ```text
   第01集.mkv
   第01集.xml
   ```

2. 在 PotPlayer 打开视频，再启动 PotDanmaku。只有一个播放器时默认自动连接；存在多个播放器时，选择目标并点击“连接并播放弹幕”。
3. 工具优先恢复此视频保存的弹幕关联，再按同名 XML → JSON → ASS 查找。也可以直接选择或拖入其他名称的弹幕文件。
4. 按需调整显示参数和时间偏移。保留 PotPlayer 的对白字幕，关闭播放器内同一份弹幕 ASS，避免重复显示。

切换下一集后会自动重新匹配弹幕。手动导入成功后会记住关联，下次打开同一路径的视频时恢复设置。

## 常用设置

| 需求 | 操作 |
| --- | --- |
| 弹幕提前或延后 | 调整“弹幕延后”，正数延后、负数提前，单位为秒 |
| 降低移动速度 | 增加“横穿时长”；默认 18 秒按视频时间计算，3×时约为 6 秒实际时间 |
| 调整覆盖区域 | 修改顶部显示区域和四边距；复杂播放器皮肤可能需要手动校准 |
| 3 倍速同步 | 保持“自动读取（含 3×）”，在 PotPlayer 设置倍速 |
| 后台运行 | 点击“收起到托盘”；双击托盘图标打开设置，关闭主窗口会退出 |
| 独立查看效果 | 使用“独立预览”，或导入 `samples` 中的合成示例 |

更多说明见 [使用指南](docs/使用说明.md)。

## 格式支持

| 格式 | 支持范围 |
| --- | --- |
| XML | 常见 B 站弹幕 XML；滚动、反向、顶部和底部固定类型 |
| JSON | `potdanmaku/v1` schema，时间单位为整数毫秒；[查看示例](samples/demo.json) |
| ASS | 导入时间、正文、基础颜色与透明度，重新排布；支持按样式筛选 |

ASS 的原坐标、字体和复杂动画不会完整复现，部分特效事件会跳过并显示在导入报告中。普通对白默认排除。工具只读原始文件。

## 已知限制

- 同时只维护一个播放器弹幕会话；可选择多个 PotPlayer 实例中的一个目标。
- 自动匹配依赖播放器返回本地视频完整路径；网络来源或不支持该接口的版本需要手动导入。
- JSON 仅支持上述 schema，不自动猜测其他导出器的字段和时间单位。
- 暂停后的首个缓存周期内，第一次小幅向前跳转可能被当作进度补报而忽略；稍后或再次调整、向后或大幅跳转仍生效。
- HDR、独占全屏、多屏 DPI、休眠恢复和长时间稳定性尚未完成完整兼容验证。

## 配置与升级

便携版首次运行后，在程序旁创建 `data` 文件夹，保存设置、视频关联、日志和诊断。目录不可写时，会提示并回退到 `%LOCALAPPDATA%\PotDanmaku`。

升级时先退出旧版，完整解压新包，再将旧版的 `data` 文件夹复制到新目录。视频与弹幕本身不会随配置迁移；移动或重命名视频后需要重新关联。

## 从源码运行

需要 Windows x64 和 .NET SDK **10.0.401**（允许同一补丁系列滚动）。首次还原依赖需要网络，正常播放可离线运行。

```powershell
git clone https://github.com/1811960541/PotDanmaku.git
cd PotDanmaku
dotnet test PotDanmaku.sln -c Release
dotnet run --project src/PotDanmaku.App -c Release
```

生成自带运行环境的便携包：

```powershell
.\tools\publish.ps1
.\tools\package-portable.ps1
```

产物生成到本地 `releases`，不纳入 Git。模块结构、诊断和验证步骤见 [开发指南](docs/开发指南.md)。

## 问题反馈

可在 [Issues](https://github.com/1811960541/PotDanmaku/issues) 提交问题，请提供工具版本、Windows 与 PotPlayer 版本、弹幕格式及复现步骤。附带诊断前，请移除文件名、路径等个人信息。

## 许可

项目源码尚未指定独立的开源许可证。第三方依赖及许可文本见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) 和 [licenses](licenses)。PotPlayer 不随项目分发。
