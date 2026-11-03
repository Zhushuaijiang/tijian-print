# 体检报告自助打印系统 (PeisSelfPrint)

医院体检中心自助机解决方案：来访者扫描/输入**体检号**，自助机查询本次体检的报告列表并**打印 PDF 报告**。

- **前端**：WPF 触摸界面（全屏、60 秒无操作自动返回、大字号、扫码枪即输即查）
- **后端**：ASP.NET Core 本机 API（`127.0.0.1:5088`，Token 鉴权 + origin 白名单）
- **数据库**：PostgreSQL 体检视图（参数化查询、只读事务）
- **打印**：SumatraPDF portable（ portable，不依赖安装）
- **部署**：.NET 8 self-contained win-x64，**目标机无需安装任何运行时**

## 解决方案结构

```
src/
├── PeisSelfPrint.Core      规则库：体检号精确匹配、PDF origin 白名单、
│                           打印账本（持久化，重启可追溯）、报告选择规则
├── PeisSelfPrint.Api       本机 API：health / 报告查询 / 会话 / PDF 代理
│                           （受控跟随报告服务 302 重定向、SSRF 防护）
├── PeisSelfPrint.Desktop   自助机界面：扫码输入 → 报告列表（可打印/审核中）
│                           → 确认（核对项默认勾选锁定）→ 打印 → 结果页
└── tests/                  单元测试（24+ 项：规则/账本/调度）
```

## 构建与发布

```powershell
# 单元测试
dotnet test PeisSelfPrint.sln

# 离线发布（win-x64 self-contained，输出 artifacts\offline）
powershell -File Publish-Offline.ps1
```

发布产物包含 Api、Desktop、Tools(SumatraPDF)、启动脚本，整目录拷贝到自助机即可运行。

## 部署

配套交付 **环境检测与一键配置工具**（`Setup-Kiosk.ps1` + `一键安装检测.cmd`）：

- 自动检测：离线包完整性 / 配置文件 / API 服务 / 数据库与报告服务连通性 / 打印机 / 打印引擎路径 / 开机自启
- 图形界面内一键完成：选择并保存打印机、修复打印引擎路径、填写数据库连接串、
  启动/重启服务、安装开机自启计划任务、创建桌面快捷方式、运行 12 项 UI 自检

## 现场配置（不含于仓库，部署时填写）

| 文件 | 用途 |
|---|---|
| `Api/integration.local.json` | 数据库连接串、API Key（机器级私有配置，已被 .gitignore 排除） |
| `Desktop/kiosk.settings.json` | 打印机名、医院名称、全屏/出纸开关 |
| `Api/appsettings.json` | 报告服务地址、PDF origin 白名单（仓库内为空白模板） |

## 安全设计

- API 仅监听 `127.0.0.1`，外部不可达；Bearer Token 鉴权
- 数据库查询参数化 + 只读事务（`default_transaction_read_only=on`）
- PDF 下载强制 origin 白名单 + 内容校验，防 SSRF
- 打印账本持久化：每次出纸可追溯，服务中断恢复后状态可重现
- 45 秒硬超时（linked CTS），防止扫码枪误触发后的悬挂请求
