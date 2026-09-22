# website/ — FocusCapture 官网维护指南

> 给未来维护本站的 Agent（和人）。本站是纯静态多页站：无框架、无构建、无依赖，
> 改完 HTML/MD 直接发布即可。这是刻意选择——维护成本最低，Agent 抓取最友好。

## 站点结构

| 文件 | 作用 |
|---|---|
| index.html / index.md | 首页（定位 + 核心能力） |
| features.html / features.md | 功能一览 + 热键表 + 数据存储位置 |
| download.html / download.md | 下载页（exe 链接 + 系统要求） |
| faq.html / faq.md | 常见问题（10 条） |
| changelog.html / changelog.md | 更新日志 |
| downloads/FocusCapture.exe | 安装包放这里，下载页链接不用改 |
| llms.txt | Agent 版站点地图（页面索引 + token 数 + 核心事实速查） |
| robots.txt | 全放行（含 AI 爬虫白名单） |
| sitemap.xml | 搜索引擎站点地图 |
| css/style.css | 全站唯一样式表 |

## 更新规则（每次改内容都要遵守）

1. **html 和同名 md 必须同步改** —— 两者内容互为镜像，只改一个 = Agent 和人看到两个版本。
2. **新增/删除页面时**：llms.txt 加/删索引行（含描述和 token 数），sitemap.xml 加/删 url，robots.txt 不用动。
3. **llms.txt 的「核心事实速查」是全站事实锚点** —— 版本号、价格、平台等变化先改这里，再改各页。
4. **faq.md 新增问答**：html 里加 `<details>` 块，md 里加一段加粗问题 + 答案，顺序保持一致。
5. **changelog 发新版时**：html 里加 `<h2>` 段落，md 里加 `##` 段落，内容取自仓库 CHANGELOG.md 的当期发版段。

## 部署前必查

- [ ] sitemap.xml 和 robots.txt 里的 `REPLACE-AFTER-DEPLOY` 已替换为真实域名
- [ ] download 页的版本号、文件大小已替换为真实值
- [ ] exe 已放进 downloads/

## 统计（浏览量可查）

当前未接统计脚本。要接时在**每个 html 的 `</body>` 前**插入统计方（如 51.la）给的代码片段。
注意：接了统计就等于开始收集访客数据，需同步在页脚说明隐私政策（网站自身的数据采集与
软件「数据全本地」的承诺是两回事，别混淆宣传）。

## 内容事实来源纪律

**网站文案以最新代码为准，不以 README / 旧文档为准。** 改功能相关文案前先核对：
- 功能清单 → `Services/`、`Windows/` 目录 + 仓库 CHANGELOG.md
- 收费状态 → `Services/Licensing/LicenseGate.cs` 的 `IsLicensingEnabled`（当前 false = 全免费）
- AI 问答配置 → `Models/AppSettings.cs` 的 Ai* 字段
- 数据路径 → `Services/FocusCapturePaths.cs`

## 文案红线

- 不承诺「永久免费」（收费开关已预埋在代码里，将来可能变）
- 不写「已保存到网盘」类未验证表述；同步功能只说「可选、默认关闭」
- 面向用户的页面不出现命令行、终端、构建步骤
