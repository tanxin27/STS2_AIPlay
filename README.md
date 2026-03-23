# TokenSpire2 - AI 驱动的杀戮尖塔 2 自动玩家

## 📁 项目结构

```
STS2_Auto/
├── README.md              # 本文件
├── TokenSpire2/           # 源代码目录
│   ├── src/              # C# 源码
│   │   ├── Llm/         # LLM 相关模块
│   │   └── Handlers/    # 游戏事件处理器
│   ├── Data/            # 配置文件和数据
│   └── ...              # 项目文件 (.csproj 等)
├── build/                # 构建输出 (不要手动修改)
│   ├── TokenSpire2.dll  # 编译后的 DLL
│   ├── TokenSpire2.json # Mod 配置
│   └── Data/            # 运行时数据
└── Releases/             # 发布包目录
    ├── README.md        # 版本说明
    ├── TokenSpire2_v1.4.1.zip      # 最新版本
    └── TokenSpire2_v1.x_README.md  # 各版本说明
```

## 🚀 快速开始

### 安装最新版本

```bash
# 1. 进入项目目录
cd ~/Tanxin/Project/STS2_Auto

# 2. 复制 DLL 到游戏 mods 目录
cp build/TokenSpire2.dll ~/sts2_mods/TokenSpire2/

# 3. 确保 .pck 文件存在（如果不存在，保留之前版本的）
ls ~/sts2_mods/TokenSpire2/TokenSpire2.pck
```

### 打包新版本

```bash
# 1. 编译
cd TokenSpire2
dotnet build -c Release

# 2. 复制到 build 目录
cp .godot/mono/temp/bin/Release/TokenSpire2.dll ../build/

# 3. 打包新版本
cd ..
zip -r Releases/TokenSpire2_v1.x.x.zip build/
```

## 📦 版本管理

所有发布版本都存放在 `Releases/` 目录：

| 版本 | 文件 | 说明 |
|-----|------|------|
| v1.4.1 | `TokenSpire2_v1.4.1.zip` | 当前最新 - 强制避免连续战斗 |
| v1.4 | `TokenSpire2_v1.4_README.md` | 状态驱动的路线决策 |
| v1.3 | `TokenSpire2_v1.3_README.md` | 路线多样化提示 |
| v1.2 | `TokenSpire2_v1.2_README.md` | Token 优化与限流处理 |
| v1.1 | `TokenSpire2_v1.1_Source.zip` | 源码包 |

## 🔧 开发

### 技术栈
- .NET 9
- Godot 4.5
- 依赖：游戏 DLL (sts2.dll), Harmony

### 主要模块
- `ConservativeStrategy.cs` - 路线策略核心
- `GameStateSerializer.cs` - 游戏状态序列化
- `RouteHistoryLogger.cs` - 路线历史记录
- `LlmClient.cs` - LLM 通信客户端

## 📄 许可证

本项目为个人学习研究用途。
