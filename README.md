# Setpiece 0.1.0

以项目为中心的可复现工作现场工具：按场景打开多个文件、按模板并排摆放，一键保存 / 恢复。

## 运行

| 文件 | 说明 |
|---|---|
| `Setpiece.App.exe` | 图形界面 |
| `setpiece.exe` | 命令行（`doctor` / `init` / `apply` / `capture` / `close` / `list`） |

系统要求：Windows 10 / 11 x64。

- **安装包**（`Setpiece-Setup-0.1.0.exe`）：双击安装，按用户安装、不需要管理员权限，
  默认装到 `%LOCALAPPDATA%\Programs\Setpiece`，创建开始菜单快捷方式，
  可选桌面快捷方式，并注册卸载项。
- **便携版**（`setpiece-0.1.0`）：需要安装 .NET 8 Desktop Runtime；
  内含 `run-app.cmd` / `run-setpiece.cmd`，通过 dotnet 宿主启动。
- **自包含版**（`setpiece-0.1.0-selfcontained`）：无需安装运行时，体积较大。

> 如果双击 exe 后窗口操作全部失败、`doctor` 显示 `完整性: Low`，说明程序被本机安全软件
> 降权到了低完整性级别。便携版请改用 `run-app.cmd`（走 dotnet 宿主，完整性正常）；
> 自包含版没有这个退路，需要代码签名，或在安全软件里给 exe 加白名单。

## 快速开始

1. 运行 `Setpiece-Setup-0.1.0.exe` 安装，或直接双击 `Setpiece.App.exe`；
2. 点「选择项目…」指向一个项目目录；
3. 没有配置时先建一个场景，或用命令行：

```bat
setpiece init --project D:\lab\exp-042 --files data\run-a.csv,data\run-b.csv,main.py
setpiece apply --project D:\lab\exp-042 main
```

## 配置文件

`<项目>/.setpiece/setpiece.json`，可手改，可随项目进 Git。

- `extensions`：扩展名 → 适配器（vscode / excel / browser / pdf / image / shell）
- `vscodeProfile`：VSCode 用户数据目录，默认留空
- `scenes[]`：`name` / `template` / `monitor` / `items[]`
- `items[].rect`：相对显示器工作区的归一化坐标 `[左, 上, 右, 下]`
- `items[].adapter`：单独覆盖打开方式

## 热键

| 热键 | 作用 |
|---|---|
| `Ctrl+Alt+W` | 唤起 / 隐藏窗口 |
| `Ctrl+Alt+1~9` | 打开当前项目第 1~9 个场景 |

关闭窗口会收进托盘，托盘菜单里选「退出」才是真正退出。

## 构建

需要 .NET 8 SDK；生成安装包还需要 Inno Setup 6（放在
`tools\innosetup6\tools\ISCC.exe`，或安装到默认位置）。

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
```

输出在 `dist\`：

| 文件 | 说明 |
|---|---|
| `setpiece-0.1.0-source.zip` | 源码 |
| `setpiece-0.1.0-win-x64.zip` | 便携版（需 .NET 8 Desktop Runtime） |
| `setpiece-0.1.0-selfcontained-win-x64.zip` | 自包含版 |
| `Setpiece-Setup-0.1.0.exe` | 安装包 |

## 已知限制

- **未签名**：Windows SmartScreen 会提示；部分安全软件会把未签名程序降权到低完整性级别，导致跨进程窗口操作全部失败。正式分发建议做代码签名。
- 安装器同样未签名。如果安装时提示“拒绝访问”或“无法创建临时目录”，
  说明安装器被安全软件降权了：**右键安装包 → 以管理员身份运行**即可绕过，
  或先把安装包加入安全软件的信任区。
- 浏览器网页窗口的标题是页面标题而非文件名，去重依赖运行时缓存
  `%LOCALAPPDATA%\Setpiece\runtime.json`。
- 活动窗口位置只支持单显示器布局，跨屏场景尚未实现。
