# MoonMovie

Windows 上的影视聚合与本地播放器（WinUI 3 / .NET 10），Android 版 CineNest 的桌面版本。

- **找片**：TMDB 的海报、简介、演职员和剧集资料；电影 / 剧集 / 动漫分类浏览、搜索、收藏、继续观看
- **片源**：自动在资源站中检索、测速、挑最快的，失败自动换源，播放时去掉插播广告
- **播放器**：mpv（libmpv）内核，硬件解码、HDR、ASS 特效字幕、双字幕、音轨与音画同步、截图、逐帧、A-B 循环、跳过片头片尾
- **超分辨率**：Anime4K（流畅 / 高质量 / 低清增强），N 卡 RTX 视频超分
- **弹幕**：LogVar 弹幕服务器，智能密度、屏蔽词、合并重复
- **本地播放**：打开或拖入文件；本地媒体库自动扫描、识别片名并匹配 TMDB；和在线片源共用进度与详情页
- **离线下载**：单集或整季，去广告、可断点续传、可限速，下完出现在本地媒体库
- **系统集成**：媒体浮层与媒体键、任务栏缩略图按钮与进度、跳转列表、视频文件关联

## 安装（给朋友）

1. 下载 Releases 里的 `MoonMovie-x.y.z-x64-安装包.zip`，解压。
2. 双击 **「安装 MoonMovie.cmd」**，同意一次管理员权限。脚本会信任随包附带的签名证书，再安装 MoonMovie。
3. 以后更新：解压新版本，再双击一次即可。

不想安装的话，可以用 `便携版.zip`，解压后直接运行 `MoonMovie.exe`。

如果安装包里没有内置 TMDB 密钥，第一次打开会跳到设置页，在 [themoviedb.org](https://www.themoviedb.org/settings/api) 免费申请一个填进去，然后重启。

## 从源码构建

需要 Windows 10 19041+、.NET 10 SDK 和 7-Zip。

```powershell
.\build\get-libmpv.ps1            # 下载固定版本的 libmpv 到 lib\mpv
copy .env.example .env            # 填上 TMDB 密钥等（可选）
dotnet build src\MoonMovie\MoonMovie.csproj -p:Platform=x64
```

发布：

```powershell
.\build\new-cert.ps1 -Password <密码>     # 只需一次：生成签名证书（build\cert，不进 git）
.\build\package.ps1 -Version 1.0.0      # 输出 artifacts\release\ 下的安装包和便携版
```

推送 `v*` 标签后，GitHub Actions 会自动打包并发布到 Releases。需要在仓库 Secrets 里配置 `SIGNING_PFX_BASE64` 和 `SIGNING_PFX_PASSWORD`；`TMDB_API_KEY`、`LOGVAR_BASE_URL`、`LOGVAR_TOKEN` 可选，配置后会内置到安装包里，**任何拿到安装包的人都能读出来**。

## 许可

MoonMovie 以 [GPL-3.0](LICENSE) 开源（因为使用了 GPL 许可的 libmpv）。

- [mpv / libmpv](https://mpv.io)：GPL-2.0+；构建来自 [shinchiro/mpv-winbuild-cmake](https://github.com/shinchiro/mpv-winbuild-cmake)
- [Anime4K](https://github.com/bloc97/Anime4K)：MIT（`src/MoonMovie/Assets/Shaders/LICENSE-Anime4K.txt`）
- HarmonyOS Sans 字体：遵循其官方字体许可
- 影视资料与图片来自 [TMDB](https://www.themoviedb.org)。本产品使用 TMDB API，但未经 TMDB 认可或认证。

片源来自公开的资源站，MoonMovie 不存储、不提供任何影视内容。仅供个人学习与交流使用。
