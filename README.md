# 补货预测工作台

Windows 桌面软件。输入当前库存和补货排期，即可预估未来销量、库存和断货时间。每个产品的方案可以单独保存。

## 下载与使用

1. 打开 [Releases](https://github.com/1175105075ly-dot/replenishment-workbench/releases/latest)，下载最新版本的 Windows ZIP。
2. 解压后双击「补货预测工作台.exe」。
3. 新建产品后手填库存和销量，或在「从领星导入」中使用自己的 MCP 密钥，按店铺和 ASIN 或 MSKU 查询数据。
4. 填写生产和运输周期、订购日与补货数量。修改库存基准日后，软件会计算当月剩余天数及之后六个完整月。

可在软件里保存或打开本地 JSON 方案。分享方案前请确认其中的库存、销量和补货排期可以公开给对方。

## 从源码构建

在装有 .NET Framework 4.8 的 64 位 Windows 上打开 PowerShell，从仓库目录运行以下命令。

    .\build.ps1

生成的程序在 dist 目录。构建会使用仓库里的图标。如果要重新生成图标，在装有 Pillow 的 Python 环境中运行以下命令。

    python .\src\build-icon.py .\src

源码按 [MIT 许可证](LICENSE) 发布。
