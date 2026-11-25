# 台账审查工具（Ledger Review）v1.1

单机离线、本机网页界面的台账审查工具。导入整册扫描 PDF，自动一页一单拆分、分类、检查勾选完整性、检测漏签、给出签名归属候选，并导出单页 PDF。

v1.1 已修复：第 27 项双高行误判、N/A 识别、客户签名误归属、设备编号格式误报；新增“线路/站点/垂梯·扶梯/梯号/单据类型”归档层级与字段区域 OCR。

## 运行（已构建版本）

```
dist\LedgerReview\启动台账审查工具.bat
```

默认打开 http://127.0.0.1:5180 。程序只监听本机，不需要联网。
详细使用说明见 `dist\LedgerReview\使用说明.txt`。

## 开发与构建

需要 .NET 9 SDK（开发机）；目标机不需要安装任何运行时。

```powershell
cd app\LedgerReview.Web
dotnet restore
dotnet run -c Debug

# 发布自包含版本
dotnet publish -c Release -r win-x64 --self-contained true -o ..\..\dist\LedgerReview
```

## 技术栈

- .NET 9 + ASP.NET Core Minimal API
- SQLite（Microsoft.Data.Sqlite）
- PdfPig（拆页/单页导出）、Docnet.Core（PDFium 渲染）
- SkiaSharp（图像处理）
- Windows.Media.Ocr（系统内置中文 OCR，离线）
- 原生 HTML/CSS/ES Modules 前端（无 npm、无 CDN）

## 目录

```
app/LedgerReview.Web/
  Domain/          领域对象、模板几何、单据类型
  Infrastructure/  工作区、SQLite、数据访问
  Pdf/             PDF 渲染与单页导出
  Vision/          图像处理、OCR、分类、勾选、签名
  Audit/           规则引擎与跨单审查
  Pipeline/        后台处理流水线、导出
  Api/             REST 接口
  wwwroot/         网页界面
  Assets/          模板参考图与人员签名字迹表
```

## 已知限制

- 手写日期、手写设备编号、手写“第 N 次”识别不稳定；系统标为“未识别”，不猜测。
- 签名归属为“候选排序 + 人工确认”，不是司法笔迹鉴定；客户签名只校有无。
- 当前交付 Windows 10/11 x64。

需求与架构文档见 `docs/`。
