# 云端自动构建 EXE（GitHub Actions，无需你自备 Windows）

本项目已内置 GitHub Actions 工作流 `.github/workflows/build.yml`。
它会在 GitHub 托管的 **真实 Windows + Visual Studio 2022** 上自动编译出
`Plain Craft Launcher 2.exe`（就是 GloriaCloud 修改版），并把成品作为构件（Artifact）提供下载。

> 为什么需要云端 Windows？PCL2 是 WPF + .NET Framework 4.8 程序，Linux / Wine
> 均无法编译（缺 MSBuild 与 WPF XAML 编译器，已实测）。只有 Windows 上的
> MSBuild 才能产出 EXE。

## 步骤（约 3 分钟，全程网页操作）

1. 把本目录（`PCL2-GloriaCloud-源码`）完整上传到你自己的 **GitHub 仓库**
   （新建一个私有/公开仓库，把文件夹内容推上去即可，包含 `.github/` 子目录与 `MeloongCore` 子模块）。
   - 推送时请用 `git clone --recurse-submodules` 方式，确保 `MeloongCore` 子模块一并上传；
     或直接把已在本地的 `MeloongCore` 目录内容一起提交。
2. 进入仓库页面 → **Settings → Actions → General**，确认
   “Allow all actions and reusable workflows” 已开启。
3. 进入仓库页面 → **Actions** 标签 → 左侧选择 **构建 PCL GloriaCloud EXE**
   → 右侧 **Run workflow** → 确认分支后点运行。
4. 等待约 2–5 分钟（构建 + 还原 NuGet 包），状态变绿后点进入该次运行，
   在底部 **Artifacts** 区域下载 `PCL-GloriaCloud-exe` 压缩包。
5. 解压得到的 `Plain Craft Launcher 2.exe` 即为成品。

## 把下载源指向你自己的服务器（发布前必做）

用文本编辑器打开这两个文件，把第 3 行的地址改成你的服务器：

- `Plain Craft Launcher 2/Modules/Minecraft/ModDownload.vb`
- `Plain Craft Launcher 2/Modules/Minecraft/ModJava.vb`

```vb
Private Const GLORIA_DOWNLOAD_BASE As String = "http://你的公网IP:3888"
```

改完提交并重新跑一次工作流即可。

## 可选：启用微软/ CurseForge 登录等高级功能

工作流里有一步会自动注入以下两项；**不配置也不影响编译**，只是对应功能不启用：

- `Settings → Secrets and variables → Actions → New repository secret`
  - 名称 `CLIENT_ID`（微软账号登录用，可选）
  - 名称 `CURSEFORGE_API_KEY`（CurseForge 用，可选）

## 备选：本地 Windows + Visual Studio 编译

若你本机有 Windows + Visual Studio 2022（勾选「.NET 桌面开发」），
直接双击根目录的 `build.bat` 即可，成品在
`Plain Craft Launcher 2\bin\Plain Craft Launcher 2.exe`。
