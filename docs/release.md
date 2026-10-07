# 发布

推送 `v*` 格式的 Git tag 后，GitHub Actions 会在 Windows runner 上运行完整测试并创建 Release。

~~~powershell
git tag v1.0.0
git push origin v1.0.0
~~~

每个 Release 包含两种 Windows x64 便携包：

| 文件 | 内容 | 适用场景 |
| --- | --- | --- |
| `SerialWorkbench-vX.Y.Z-win-x64-lite.zip` | `SW_CLI.exe`、`SW_TUI.exe`、`SW_HOST.exe` 及自包含依赖、Schema、说明和许可证 | 终端操作、脚本和 Agent |
| `SerialWorkbench-vX.Y.Z-win-x64-full.zip` | 轻量包全部内容，以及 `SW.exe`、WinUI 资源和 Windows App SDK 文件 | 完整图形工作台 |

两种包都不依赖目标机器预装 .NET 运行时。完整包额外携带 GUI 所需的 WinUI 资源，因此体积更大。每个压缩包旁边会提供同名 `.sha256` 校验文件。

GitHub Actions 还会保留一份 workflow artifact，便于在 Release 创建失败时下载检查。Release 使用仓库内置的 `GITHUB_TOKEN` 创建，不需要额外配置发布密钥。

本地可以使用相同脚本生成两种包：

~~~powershell
.\eng\release.ps1 -Version v1.0.0
~~~

输出位于 `artifacts/release/`。脚本先执行 `eng/test.ps1`，再生成完整包和轻量包；轻量包发布阶段复用已完成的测试结果。硬件串口测试仍遵循测试脚本的 `SERIALWORKBENCH_TEST_PORT` 约定。

CI 工作流位于：

- `.github/workflows/ci.yml`：`main` push、面向 `main` 的 pull request 和手动运行
- `.github/workflows/release.yml`：推送 `v*` tag
