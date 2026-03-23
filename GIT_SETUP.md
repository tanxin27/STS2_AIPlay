# Git 使用指南

## 📦 仓库已初始化

本地 Git 仓库已创建，包含：
- 所有源代码（src/）
- 配置文件（Data/）
- 项目文件（.csproj, .sln）
- 文档（README.md）

已排除（.gitignore）：
- build/ （编译输出）
- Releases/ （发布包）
- packages/ （NuGet包）
- gameplay_log/ （游戏日志）

## 🚀 推送到 GitHub

### 1. 在 GitHub 创建仓库

访问 https://github.com/new
- 仓库名：STS2_AIPlay
- 选择 Private 或 Public
- 不要勾选 "Initialize with README"

### 2. 推送本地仓库

```bash
cd ~/Tanxin/Project/STS2_Auto

# 添加远程仓库（替换 YOUR_USERNAME）
git remote add origin https://github.com/YOUR_USERNAME/STS2_AIPlay.git

# 推送到 GitHub
git push -u origin main
```

### 3. 验证

```bash
git remote -v
# 应该显示：
# origin  https://github.com/YOUR_USERNAME/STS2_AIPlay.git (fetch)
# origin  https://github.com/YOUR_USERNAME/STS2_AIPlay.git (push)
```

## 📝 日常使用

### 查看状态
```bash
git status
```

### 提交更改
```bash
# 添加修改的文件
git add .

# 提交
git commit -m "描述你的更改"

# 推送到 GitHub
git push
```

### 创建版本标签
```bash
# 打标签（发布新版本时）
git tag -a v1.5.0 -m "Release version 1.5.0"

# 推送标签到 GitHub
git push origin v1.5.0
```

### 查看历史
```bash
git log --oneline --graph
```

## 🔀 分支管理（可选）

```bash
# 创建开发分支
git checkout -b develop

# 切换分支
git checkout main

# 合并分支
git merge develop
```

## 📤 发布新版本流程

```bash
# 1. 确保代码已提交
git add .
git commit -m "v1.5.0: 新增功能XXX"

# 2. 打标签
git tag v1.5.0

# 3. 推送代码和标签
git push
git push --tags

# 4. 在 GitHub 创建 Release
# 访问 https://github.com/YOUR_USERNAME/STS2_AIPlay/releases
# 点击 "Draft a new release"
# 选择标签 v1.5.0
# 上传 Releases/v1.5.0/STS2_AIPlay_v1.5.0.zip
```

## 💡 最佳实践

1. **频繁提交**：小步快跑，每次完成一个功能就提交
2. **写清楚的提交信息**：说明"做了什么"和"为什么"
3. **使用标签标记版本**：v1.0.0, v1.1.0 等
4. **不要把编译输出提交到git**：已经通过 .gitignore 排除

## 🆘 常见问题

### 忘记添加远程仓库
```bash
git remote add origin https://github.com/YOUR_USERNAME/STS2_AIPlay.git
```

### 推送被拒绝
```bash
# 先拉取最新代码
git pull origin main

# 然后再推送
git push
```

### 撤销修改
```bash
# 撤销未暂存的修改
git checkout -- filename

# 撤销已暂存但未提交的修改
git reset HEAD filename
```
