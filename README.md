# LanFileTransfer

网线直连或局域网文件传输工具，界面使用 Avalonia 实现。

## 运行界面

推荐使用已打包好的单文件：

```text
Tools\LanFileTransfer\publish\win-x64-single\LanFileTransfer.exe
```

如果原 exe 正在运行导致无法覆盖，临时新版可能发布在：

```text
Tools\LanFileTransfer\publish\win-x64-single-v5\LanFileTransfer.exe
```

这个 exe 已包含 .NET 运行时和 Avalonia 依赖，拷到另一台 Windows x64 电脑上即可运行。`LanFileTransfer.pdb` 是调试符号文件，不需要一起拷贝。

开发构建产物也可以双击运行：

```text
Tools\LanFileTransfer\bin\Release\net8.0\LanFileTransfer.exe
```

界面里有五种操作：

- 接收文件：选择保存目录，点击“开始接收”。
- 发送全部：选择一个源目录，发送该目录里的全部内容，不额外套源目录名。
- 发送选择：添加多个文件或文件夹，只发送选择列表里的内容。
- 系统迁移：生成或导入迁移包，迁移环境变量和用户注册表。
- 开始菜单：扫描已拷贝应用目录里的 `.exe`，勾选后添加到当前用户开始菜单。

文件传输默认使用 12 路并行 TCP 连接，适合大量小文件；发送页签里可以把并行数调整到 `1` 到 `32`。界面会显示实时速度。日志刷新做了节流，传输大量文件时 UI 不应再卡死。

## 网线直连设置

两台电脑用网线直连后，手动设置同网段 IPv4：

- 电脑 A：`192.168.10.1`，子网掩码 `255.255.255.0`
- 电脑 B：`192.168.10.2`，子网掩码 `255.255.255.0`

接收端第一次运行时，如果 Windows 防火墙弹窗，请允许专用网络访问。

## 使用顺序

1. 接收文件的电脑先打开软件，切到“接收文件”，选择保存目录，点击“开始接收”。
2. 发送文件的电脑打开软件，点击“自动识别”，或者手动填写接收端 IP。
3. 选择“发送全部”或“发送选择”，点击发送。

默认端口是 `38383`，两端保持一致即可。

自动识别使用 UDP 广播，接收端必须已经点击“开始接收”。如果 Windows 防火墙弹窗，请允许专用网络访问。

同时连接 WiFi 和网线直连时，自动识别会收集多个候选 IP，并优先选择同网段的有线网卡、无默认网关的连接，尽量让传输走网线直连。

发送页签的“网络测速”只测试 TCP 网络吞吐，不读写磁盘。测速前接收端也需要先点击“开始接收”。

如果自动识别不到：

- 确认两台电脑都使用同一个最新版 exe。
- 确认接收端已经点击“开始接收”。
- 确认两台电脑 IPv4 在同一网段，例如 `192.168.10.1` 和 `192.168.10.2`。
- Windows 防火墙需要允许本程序的专用网络访问。
- 仍失败时，可以手动填写接收端界面日志里显示的本机 IPv4。

## 整盘迁移

可以在“发送全部”里选择磁盘根目录，例如 `D:\`。工具会尽量扫描并传输可读取的内容。

注意：

- 遇到无权限、被占用、路径过长的目录或文件时会跳过，并写入日志。
- 默认跳过 `$RECYCLE.BIN`、`System Volume Information`、`pagefile.sys`、`hiberfil.sys` 等系统目录/文件。
- 系统盘根目录通常包含大量系统文件，不建议直接覆盖到另一台机器的系统盘。
- 两端必须使用同一版工具。当前协议为 `LFT3`，支持可调并行连接、边扫描边发送、实时速度显示。

## 系统迁移

“系统迁移”页签可以生成一个 zip 迁移包，包含：

- 用户环境变量。
- 机器环境变量，可选，导入可能需要管理员权限。
- `HKCU\Software` 用户注册表，可选。

推荐流程：

1. 源电脑打开“系统迁移”，选择保存路径，点击“生成迁移包”。
2. 用“发送选择”把生成的 zip 发到目标电脑。
3. 目标电脑打开“系统迁移”，选择 zip，点击“导入迁移包”。

注册表导入会修改目标电脑设置。请确认目标电脑已安装对应软件，不建议把旧电脑注册表无脑导入到系统环境差异很大的电脑。

## 开始菜单

如果应用已经拷贝到目标电脑，但开始菜单里没有入口，可以在目标电脑打开“开始菜单”页签：

1. 在“开始菜单”里选择应用所在目录，例如 `D:\Apps` 或接收文件保存目录。
2. 点击“扫描应用”，工具只会递归扫描这一栏里选择的目录。
3. 勾选要添加的程序，点击“添加到开始菜单”。

也可以点击“扫描常用目录”，会扫描桌面、下载、`Program Files`、`Program Files (x86)`、`%LocalAppData%\Programs`，以及各固定磁盘根目录下的 `Apps`、`Programs`、`Software` 等常见应用目录。工具会默认跳过卸载器、安装器、更新器等常见辅助程序；如果过滤后没有结果，但确实发现了 `.exe`，会把这些候选显示出来，方便手动确认。

扫描时会在“开始菜单”页内显示独立进度。扫描结果列表支持滚动，并会尽量显示每个 `.exe` 自带图标。

快捷方式会写入当前用户的开始菜单程序目录，不需要管理员权限。可以点击“打开开始菜单目录”查看生成结果。

## 命令行兼容

界面之外仍支持命令行：

```powershell
.\LanFileTransfer.exe recv --out D:\Receive --port 38383 --overwrite
.\LanFileTransfer.exe send-all 192.168.10.2 D:\Data --port 38383
.\LanFileTransfer.exe send-selected 192.168.10.2 D:\Data\a.txt D:\Data\Folder --port 38383
```

## 构建

在工程根目录执行：

```powershell
dotnet build Tools\LanFileTransfer\LanFileTransfer.csproj -c Release
```

发布 Windows x64 单文件：

```powershell
dotnet publish Tools\LanFileTransfer\LanFileTransfer.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o Tools\LanFileTransfer\publish\win-x64-single
```
