# FCUAutoDesign

FCUAutoDesign 是 Autodesk Revit 2020 / .NET Framework 4.8 外接程序，用于同楼层多房间 FCU 布置、供回水和冷凝水接管、设计记录及受限修改重算。

当前是工程验证版。模型连接成功不代表正式水力、冷凝水坡度或施工净距验收通过。使用条件、安装步骤和客户验收边界见 [使用文档.md](使用文档.md)，实现演进证据见 [POC验证.md](POC验证.md)。

## 已实现能力

- 门侧墙内偏移定位，单台或按 L/5 向上取整的多台均布。
- 使用实际送风接口校正 FCU 朝向。
- 供水、可选回水和可选 `Sanitary` 冷凝水接管。
- 不下翻、下翻及有限横移候选，连接链、安装净长和本批次回路实体干涉检查。
- 当前平面视图内按房间名称关键词自动选房，并按可唯一覆盖的主管段组分区执行。
- 每房间事务组、同房间多台整体回滚和批量真实结果报告。
- Revit Extensible Storage 设计身份、版本和上次成功快照。
- 只允许更新冷指标和设计负荷记录的受限修改重算。
- OpenAI 兼容接口驱动的 AI 参数方案，严格 JSON 架构和本地二次校验。

## 明确未实现

- 正式设备型号与 Revit 族类型自动映射。
- 正式水力选径；界面开关保持禁用。
- 冷凝水坡度、重力排水和水力性能验算。
- 全模型、链接模型、保温、套管、检修空间和施工净距检查。
- 已有设备、型号、数量、位置及管线几何的自动重建。

## 代码边界

- `CmdPlaceFCUAndConnect.cs`：命令编排、范围确定、综合预览和批量结果。
- `FCUDesignWindow.xaml(.cs)`：参数窗口与 AI 方案回填；不直接写 Revit 模型。
- `Agent/`：兼容接口、提示词、严格输出契约和本地校验。
- `Domain/EquipmentSelection/`：独立于 Revit/WPF 的负荷、台数、预览型号和局部点位计算。
- `Services/AutomaticScopeDiscoveryService.cs`：当前平面视图房间和主管段组发现。
- `Services/FcuPlacementService.cs`：门侧定位、房间包含和送风方向校验。
- `Services/FcuDesignService.cs`：单房间事务组、放置、接管、提交复核和设计记录。
- `Services/HydronicConnectionService.cs`、`HydronicSeparationService.cs`：供回水候选与主管接入。
- `Services/CondensateSeparationService.cs`：冷凝水候选、试建预算、连通和回路干涉验证。
- `Services/DesignRecordRepository.cs`：Revit Extensible Storage 持久化。
- `Services/DesignReconciliationPreviewService.cs`：三方差异预览和纯计算记录更新。
- `tests/Verify-*.ps1`：可重复的独立逻辑与窗口回归检查。

`FcuDesignService` 持有每个房间的主事务和事务组。局部候选使用子事务，失败后回滚；提交后的设备位置、朝向、连接链和安装条件复核仍位于可整体回滚的事务组内。

## Release 编译

```powershell
& 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe' `
  FCUAutoDesign.csproj /t:Rebuild /p:Configuration=Release /p:Platform=x64 `
  /p:RevitVersion=2020 /nologo /verbosity:minimal
```

输出文件：`bin\Release\FCUAutoDesign.dll`。

## 安装

关闭 Revit，将 `FCUAutoDesign.dll` 和 `FCUAutoDesign.addin` 放到：

```text
%APPDATA%\Autodesk\Revit\Addins\2020\
```

清单使用同目录相对路径 `<Assembly>FCUAutoDesign.dll</Assembly>`。复制后重新启动 Revit。

## AI 方案助手配置

```powershell
setx FCU_AGENT_API_KEY "替换为客户自己的密钥"
```

可选覆盖：

```powershell
setx FCU_AGENT_BASE_URL "https://example.com/v1"
setx FCU_AGENT_MODEL "兼容模型名称"
```

设置后重启 Revit。API Key 不应进入源码、`.addin`、测试包、日志或截图。AI 仅生成候选参数并回填表单，不能绕过用户确认和现有 Revit 事务。
