# FCUAutoDesign

这是一个 Autodesk Revit 的插件项目，用于在 Revit 中布置风机盘管（FCU）并自动处理相关管线连接与避让逻辑。

## 项目简介

该插件主要包括：

- FCU 的放置与参数控制
- 房间 / 门 / 标高的几何判断
- 供水、回水、冷凝水管路的连接逻辑
- WPF 参数配置界面
- Revit 外接程序入口 `CmdPlaceFCUAndConnect`

## 运行要求

- Windows
- Autodesk Revit（对应版本，通常为 2022 或更高版本）
- Visual Studio 2022
- .NET Framework 4.8
- 需要安装 Revit SDK / 对应 API 参考库

## 关键说明

本项目是 Revit Add-in，不是 .NET Core 项目。编译时需要使用 Visual Studio 的 MSBuild 和对应版本的 Revit API DLL。

项目中的 Revit 引用路径可通过 MSBuild 属性覆盖，例如：

```powershell
msbuild FCUAutoDesign.csproj /p:RevitVersion=2022 /p:RevitInstallPath="C:\Program Files\Autodesk\Revit 2022"
```

如果没有显式指定 `RevitInstallPath`，默认会尝试查找标准安装目录。

## 目录说明

- `CmdPlaceFCUAndConnect.cs`：Revit 外接程序入口
- `FCUDesignWindow.xaml`：参数配置界面
- `FCUDesignWindow.xaml.cs`：界面逻辑
- `FCUAutoDesign.addin`：Revit 插件加载配置
- `FCUAutoDesign.csproj`：项目文件

## 编译建议

建议在安装好 Visual Studio 2022 和对应 Revit 版本后，使用 Visual Studio 打开 `.sln` 文件进行编译。

确保以下组件已安装：

- .NET desktop development
- C# 开发支持
- 对应 Revit 版本的 API 依赖

## 注意事项

- 这是 Revit 插件项目，不适合直接当普通 .NET Core 项目运行
- Revit 版本切换时，需要检查 DLL 路径和兼容性
- 某些环境可能需要管理员权限或对应 Autodesk SDK

## 许可说明

本项目仅供内部开发与学习使用，具体使用范围请遵循项目所属团队或组织的授权要求。
