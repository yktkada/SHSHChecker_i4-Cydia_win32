# SHSH Checker（WinForms / Win32）

原生 Windows 桌面版 SHSH 工具：可以按 ECID 查询并保存 **爱思（i4）** 与 **Cydia** 的 SHSH 备份，
支持现代 `shsh2`，也可以把本地 SHSH 上传到爱思服务器。

> 本工具用于**备份自己的设备**，依赖第三方服务，随时可能失效。

---

## 功能

- **ECID 输入**：十六进制（`0011AABBCCDDEEFF`、`0x00AABBCCDD`）或十进制（`1234567890123`，13 位以上自动识别），实时显示两种写法
- **机型选择**：按 SoC 显示（`A5 - iPad 2`、`M1 - iPad Pro (11-inch) (3rd generation)`）
  - 默认只显示 32-bit 设备
  - 勾「启用 arm64 设备查询」「启用 arm64e 设备查询」可逐步放开 64-bit 机型
- **ProductType 下拉**：默认「全部 ProductType」（查该机型所有变体），也可只查单个变体
- **查询爱思备份** / **查询 Cydia 备份**：结果合并到一张表，`来源` 列区分 `i4` / `cydia`
- **下载**：`下载选中`（或双击）/ `全部下载`
  - 文件名规则 `{ProductType}_{版本}_{build}_{ECID}_{来源}.shsh`
  - 现代设备（A7 及以后）保存为 `.shsh2`
- **上传到爱思服务器**：文件对话框支持多选，也支持文件夹 / UNC 网络共享
  - 解析：老格式看 plist，现代 `shsh2` 看 `ApImg4Ticket`（IMG4 的 `ECID` / `BNCH`）
  - 上传前二次确认；多选按顺序逐个上传并汇总成功/失败
- **原始响应 / 请求参数**：可查看返回 JSON、签名明文、Base64 密文与完整请求 URL

---

## 运行要求

- Windows 10 / 11（x64）
- **.NET 10 Desktop Runtime**（Windows 不自带，首次使用需安装）

下载地址：<https://dotnet.microsoft.com/download/dotnet/10.0>
→ 选 **Desktop Runtime 10.x** → **Windows x64**

---

## 构建

```powershell
# 构建
dotnet build SHSHCheckerWin\SHSHCheckerWin.csproj -c Release

# 发布单文件 exe（框架依赖，约 300 KB）
dotnet publish SHSHCheckerWin\SHSHCheckerWin.csproj -c Release -r win-x64 `
  --self-contained false -p:PublishSingleFile=true -p:DebugType=None -o dist
```

产物：`dist\SHSHCheckerI4.exe`

> 未做代码签名，首次运行可能出现 SmartScreen 提示。

### 自动构建 / 发布（GitHub Actions）

- `.github/workflows/build.yml`：push / PR 时构建并上传 exe 产物
- `.github/workflows/release.yml`：推送 `v*` tag 时自动构建单文件 exe，生成 `SHA256SUMS.txt` 并创建 GitHub Release

```powershell
git tag v0.7.4
git push origin v0.7.4     # 触发 release 工作流
```

---

## 使用

1. 填 ECID（十六进制或十进制均可）
2. 选机型（默认只列 32-bit；查现代设备请勾上 arm64 / arm64e）
3. 点「查询爱思 SHSH 备份」或「查询 Cydia 备份」
4. 选中结果行点「下载选中」，或点「全部下载」选择文件夹

### 上传 SHSH

窗口底部是独立的上传区：

- **选择文件**：点「浏览…」可多选；也可以直接粘贴路径
- **支持输入形态**
  - 单个文件：`D:\blobs\a.shsh2`
  - 带引号的路径（资源管理器「复制为路径」）：`"D:\shsh blobs\a.shsh2"`
  - 多个路径：分号或换行分隔
  - **文件夹**（含 UNC 网络共享）：按 **Enter** 或点到别处递归扫描其中的 `.shsh / .shsh2 / .plist`
- **Enter 只解析上传路径，不会触发查询**；路径写错改好后按 Enter 可重试
- 扫描在后台线程执行，界面不冻结；单次最多展开 **1000 个文件**
- **机型优先级**：优先用 SHSH 文件里识别出的机型（单文件时会同步上方下拉框）；识别不出时用上方选择的机型兜底，并用黄色提示

---

## 已知限制

- **上传给老设备（iOS 11 及以下）**：多数会被爱思服务器拒绝（返回 `message:"1"`）；即使上传成功，服务器也可能只保留 APTicket 核心，下载回来是精简版。**无论是否上传，都请保留本地原始 SHSH 文件**
- **现代 `shsh2` 上传**：可新增服务器记录，回读内容一致（去空白后一致）
- **Cydia**：A7 及以后设备（含 iPhone 5c）没有 SHSH 缓存，查询按钮会自动禁用
- 接口为第三方私有服务，协议或鉴权随时可能变化；必要时升级客户端版本号（`pcver`）

---

## 技术说明

- **i4 接口**：查询 `requestBackupSHSHList.xhtml`、下载 `downloadConvertSHSH.xhtml`、上传 `uploadSHSH.xhtml`，
  参数为 3DES/ECB/PKCS7 加密后的 JSON，Base64 直出（无 `Salted__`）
- **Cydia 接口**：`tss@home/api/check/<ecid>` 列表、`manifest.xml` 取 BuildManifest、`TSS/controller` 取 APTicket
- **`shsh2` 解析**：从 `ApImg4Ticket` 的 IMG4 属性读 `ECID` / `BNCH`，机型与版本取自文件名
- **机型库**：手工对照表 + [ipsw.me](https://api.ipsw.me/v4/devices) 生成数据，按 SoC 代际排序

---

## 免责声明

- 仅供个人备份自己的设备使用
- 不提供任何担保；使用本工具造成的任何后果由使用者自行承担
- 请遵守相关服务的使用条款

