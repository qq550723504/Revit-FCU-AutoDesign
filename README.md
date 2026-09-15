# FCUAutoDesign

这是一个 Autodesk Revit 的插件项目，用于在 Revit 中布置风机盘管（FCU）并自动处理相关管线连接与避让逻辑。

当前最小 PoC 的验证基线为 **Revit 2020 / .NET Framework 4.8**。已完成编译和窗口回归检查，真实接管尚未验收；模型条件、已知边界和用例见 [POC验证.md](POC验证.md)。当前路径为固定下翻规则，不包含碰撞检测或水力计算。

## 项目简介

该插件主要包括：

- FCU 的放置与参数控制
- 房间 / 门 / 标高的几何判断
- 供水、回水、冷凝水管路的连接逻辑
- WPF 参数配置界面
- Revit 外接程序入口 `CmdPlaceFCUAndConnect`

## 运行要求

- Windows
- Autodesk Revit 2020（本轮本机编译基线；其他版本需独立验证）
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

- `CmdPlaceFCUAndConnect.cs`：Revit 外接程序入口，负责参数确认、模型拾取和结果展示调用
- `Services/FcuDesignService.cs`：布置与接管流程，统一持有主事务和事务组，提交后复核模型
- `Services/FcuPlacementService.cs`：房间内定位、设备放置与出风接口朝向验证
- `Services/HydronicConnectionService.cs`：供回水固定路径支管、弯头与三通接入
- `Services/HydronicSeparationService.cs`：回水避让候选重试及供回水实体干涉检查
- `Services/CondensateDrainService.cs`：冷凝水按实际标高连接的支管、主管打断与三通接入
- `Services/FcuConnectorResolver.cs`、`Services/FcuTypeCatalog.cs`：设备接口识别和候选族类型查询
- `Services/ConnectionChainVerifier.cs`：按指定接口与元素链验证连接关系
- `Geometry/CondensateRoutePlanner.cs`：独立于 Revit 模型的重力排水路径计算
- `Models/`：参数快照、放置结果、连接结果与执行结果
- `Infrastructure/`：单位常量、拾取过滤器、管道接口访问和 Revit 失败处理器
- `Presentation/ExecutionReportPresenter.cs`：执行结果报告
- `FCUDesignWindow.xaml`：参数配置界面
- `FCUDesignWindow.xaml.cs`：界面逻辑
- `FCUAutoDesign.addin`：Revit 插件加载配置
- `FCUAutoDesign.csproj`：项目文件

服务不读取 WPF 窗口；命令确认参数后通过 `FcuDesignOptions` 传入。
主事务仅由 `FcuDesignService` 提交，提交后的位置、朝向与连接链复核仍在事务组内，失败时整组回滚。
三通和冷凝水服务保留局部子事务，其他服务不自行开启或提交主事务。
这些类保留在同一个程序集；新增源文件需加入项目文件的 `Compile` 列表。

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

当前 Debug 输出和本机 Revit 注册路径统一为 `bin/MultiRoom/FCUAutoDesign.dll`（旧 Debug DLL 被运行中的 Revit 占用）。修改后需重启 Revit 才能加载新版。

`CondensateSeparationService` 负责冷凝水候选重试；`CircuitInterferenceVerifier` 统一检查回路间管道与管件实体相交。

批量入口：`FcuBatchService` 按房间隔离事务；`RoomBatchContext` 管理已提交回路；`MainPipeRun` 跟踪主管分段；`BatchReportPresenter` 汇总逐房间结果。首版仅支持同楼层、单门房间及共用所选主管。
