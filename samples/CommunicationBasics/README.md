# SECS 通信入门：Host 与设备模拟器

一个控制台程序启动两个独立通信端，通过真实 TCP/HSMS 连接交换消息。
Host 代表工厂上位系统；设备模拟器代表整台设备对外提供的通信接口。
设备用等待 1 秒模拟动作，不连接 EFEM 机器人或其他硬件。

## 运行

需要 .NET 10 SDK。在仓库根目录执行：

```bash
dotnet run --project samples/CommunicationBasics/CommunicationBasics.csproj
```

在已配置的云环境中，先激活工具链，使用已有的构建输出目录：

```bash
source /workspace/.onboarding/env.sh
cd /workspace/secs4net
dotnet run --project samples/CommunicationBasics/CommunicationBasics.csproj -p:ArtifactsPath=/workspace/.onboarding/secs-artifacts
```

默认仅连接本机的 5000 端口。如果端口已占用，可在运行命令末尾加 `-- 5001`。
程序完成后自动退出；Ctrl+C 可以中止，整个演示最多运行 30 秒。
成功退出码为 0，失败为 1，命令行参数错误为 2。

## 先看这条流程

```text
Host                                设备模拟器
  |---- TCP 连接、HSMS Select ---------->|
  |---- S1F1：你在吗？ ----------------->|
  |<--- S1F2：型号、版本 ----------------|
  |---- S2F41：START ------------------>|
  |<--- S2F42：HCACK=4，接受，稍后完成 ---|
  |                                    | 模拟执行 1 秒
  |<--- S6F11：动作完成事件 -------------|
  |---- S6F12：事件已收到 -------------->|
```

**命令被接受，与动作已完成，是两个阶段。**
这里 S2F42 的 HCACK=4 表示接受，并将通过后续事件报告完成。
S6F12 表示 Host 确认收到事件，本身没有触发设备动作。

## 对照代码阅读

所有代码在 `Program.cs`，建议先阅读主流程，再阅读两个接收函数。

| 代码 | 职责 |
| --- | --- |
| `HsmsConnection` | 建立 TCP/HSMS 连接；等待 `Selected` 后交换数据消息 |
| `SecsGem` | 收发 SECS 消息、关联请求与回复 |
| `host.SendAsync(...)` | Host 发请求，并等待对应回复 |
| `ReceiveDeviceMessagesAsync` | 设备收到身份查询或 START，并作出回复 |
| `ReceiveHostMessagesAsync` | Host 接收设备主动发送的完成事件 |
| `TryReplyAsync(...)` | 使用原请求的 System Bytes 回复，不能随便用新请求代替 |
| `TeachingLogger` | 打印方向、关联编号和 SML 消息体 |

设备为 Passive，监听端口；Host 为 Active，主动建立连接。
Active/Passive 是连接方式，本身不规定哪一端必须是 Host。
日志可能交错，按照 `[Host]`、`[设备]` 和相同的十六进制 System Bytes 配对阅读。

## 看懂消息体

`L(...)` 是列表，`A(...)` 是字符串，`B(...)` 是字节，`U4(...)` 是无符号 32 位整数。
`new SecsMessage(2, 41)` 默认要求回复，日志中的 `W` 就是这个意思。

START 请求：

```csharp
L(A("START"), L()) // 命令名；空参数列表
```

完成事件：

```csharp
L(U4(0), U4(1001), L(L(U4(1), L(A("COMPLETED")))))
// DATAID=0，CEID=1001，报告列表：RPTID=1，数据值列表中有 COMPLETED。
```

本示例预先约定 CEID=1001 表示动作完成，RPTID=1 包含结果字符串。
这些 ID 和 START 的业务含义是示例约定，不是所有设备通用的规定。
实际项目应查设备接口文档，并处理事件报告的定义、关联和启用。

## 验证与练习

程序不仅打印消息，还检查身份内容、命令确认码、完成事件内容与事件确认码。
只有全部检查通过才打印“演示通过”。

可以先把模拟动作的 `Task.Delay(1000, token)` 改为 3000，观察“接受”和“完成”之间的等待。
再把 Host 的 START 改为一个未知命令，观察设备回复 HCACK=1，以及演示以失败退出。

这是最小通信教学示例，省略了 GEM 通信建立、Online/Offline/Local/Remote 状态管理、
事件订阅配置和生产任务管理，不是完整的 GEM 设备实现。
设备端按顺序处理动作，生产应用需要独立任务状态机处理忙碌、停止、异常与重连。
