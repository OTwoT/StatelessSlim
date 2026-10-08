# Stateless.Slim

一个单文件、同步（“无协程”）版的 Stateless 状态机库。

上游项目：<https://github.com/dotnet-state-machine/stateless>（Apache-2.0）

---

## 与标准 Stateless 5.x 源码相比移除的内容

- **所有 async/await（“协程”）支持**：`Fire`/`Activate`/`Deactivate`、`PermittedTriggers`、进入/退出/激活/停用动作、守卫以及未处理触发器处理器均仅为同步。不依赖 `System.Threading.Tasks`，每次转换不分配 Task，因此对 Unity 帧循环、IL2CPP 和 AOT 构建更友好。
- **图形导出**（`Stateless.Graph`：`StateGraph`/`UmlDotGraph`/`MermaidGraph`）。
- **反射/内省 API**（`Stateless.Reflection`、`GetInfo()`）。
- **.resx 资源**：异常消息已内联，因此没有附属程序集，也没有运行时资源查找。

---

## 保留的内容（完整的同步公共 API）

- `Permit` / `PermitIf` / `PermitReentry` / `PermitReentryIf` / `PermitDynamic` / `PermitDynamicIf`
- `Ignore` / `IgnoreIf`
- `InternalTransition` / `InternalTransitionIf`
- `SubstateOf`、`InitialTransition`
- `OnEntry` / `OnEntryFrom` / `OnExit` / `OnActivate` / `OnDeactivate`
- `Fire`（以及最多三个触发器参数的类型化重载）
- `CanFire`（以及未满足守卫的描述）
- `PermittedTriggers` / `GetPermittedTriggers` / `GetDetailedPermittedTriggers`
- `IsInState`
- `OnTransitioned` / `OnTransitionCompleted`（以及注销变体）
- `OnUnhandledTrigger`
- `FiringMode.Immediate` / `FiringMode.Queued`
- `Activate` / `Deactivate`

---

## 在 Unity 中的用法

将此单文件放到 `Assets/` 下的任意位置即可（例如 `Assets/Plugins/Stateless/Stateless.Slim.cs`）。无需包、无需 asmdef，也无需其他文件。语言级别：C# 7.3 / .NET Standard 2.0，因此可从 Unity 2018.4 一直到最新版本编译，支持 Mono 和 IL2CPP。

```csharp
using Stateless;

enum State { Off, On }
enum Trigger { Toggle }

var machine = new StateMachine<State, Trigger>(State.Off);
machine.Configure(State.Off).Permit(Trigger.Toggle, State.On);
machine.Configure(State.On).Permit(Trigger.Toggle, State.Off);

machine.Fire(Trigger.Toggle);       // machine.State == State.On
bool canFire = machine.CanFire(Trigger.Toggle);
```

---

## 因移除而产生的 API 差异

- `GetDetailedPermittedTriggers()` 在此为同步方法（过去返回 `Task<IEnumerable<TriggerDetails<..>>>`）。
- 描述参数（`entryActionDescription`、`guardDescription`、`destinationStateSelectorDescription` 等）仍被接受，以便现有调用点继续编译；未满足守卫的报告使用守卫描述，而动作/选择器描述不再存储，因为只有内省 API 会使用它们。
- `PermitDynamic`/`PermitDynamicIf` 不再接受可选的 `DynamicStateInfos possibleDestinationStates` 参数（内省类型）。
- `RetainSynchronizationContext` 已移除：它仅控制 await 延续的 `SynchronizationContext`。

---

## 许可证

Licensed under the Apache License, Version 2.0.