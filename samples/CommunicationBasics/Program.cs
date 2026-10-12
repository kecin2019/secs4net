using Microsoft.Extensions.Options;
using Secs4Net;
using Secs4Net.Sml;
using static Secs4Net.Item;

// 一个进程内的两个独立通信端，通过真实的本机 TCP 连接交换消息。
if (args.Length > 1 || (args.Length == 1 && (!int.TryParse(args[0], out var value) || value is < 1 or > 65535)))
{
    Console.Error.WriteLine("用法：dotnet run --project samples/CommunicationBasics -- [端口，默认 5000]");
    return 2;
}
var port = args.Length == 0 ? 5000 : int.Parse(args[0]);
using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(30));
Console.CancelKeyPress += (_, e) => { e.Cancel = true; lifetime.Cancel(); };
var token = lifetime.Token;
var deviceLogger = new TeachingLogger("设备");
var hostLogger = new TeachingLogger("Host");
var deviceOptions = Options.Create(new SecsGemOptions
{
    DeviceId = 0, IsActive = false, IpAddress = "127.0.0.1", Port = port, T3 = 5000,
});
var hostOptions = Options.Create(new SecsGemOptions
{
    DeviceId = 0, IsActive = true, IpAddress = "127.0.0.1", Port = port, T3 = 5000, T5 = 500,
});
await using var deviceConnection = new HsmsConnection(deviceOptions, deviceLogger);
await using var hostConnection = new HsmsConnection(hostOptions, hostLogger);
using var device = new SecsGem(deviceOptions, deviceConnection, deviceLogger);
using var host = new SecsGem(hostOptions, hostConnection, hostLogger);
var deviceSelected = NewSignal();
var hostSelected = NewSignal();
var eventReceived = NewSignal();
var eventAcknowledged = NewSignal();

deviceConnection.ConnectionChanged += (_, state) =>
{
    Console.WriteLine($"[设备] 连接状态：{state}");
    if (state == ConnectionState.Selected) deviceSelected.TrySetResult(true);
};
hostConnection.ConnectionChanged += (_, state) =>
{
    Console.WriteLine($"[Host] 连接状态：{state}");
    if (state == ConnectionState.Selected) hostSelected.TrySetResult(true);
};

// 两端都必须接收主动消息：设备接命令，Host 接设备上报的事件。
var deviceReader = ReceiveDeviceMessagesAsync();
var hostReader = ReceiveHostMessagesAsync();
try
{
    Console.WriteLine($"步骤 1：设备监听 127.0.0.1:{port}，Host 主动连接，等待 HSMS Selected。");
    deviceConnection.Start(token);
    hostConnection.Start(token);
    await Task.WhenAll(deviceSelected.Task, hostSelected.Task).WaitAsync(token);

    Console.WriteLine("\n步骤 2：Host 发 S1F1（你在吗？），设备回 S1F2（型号和版本）。");
    using var areYouThere = new SecsMessage(1, 1);
    using var identity = await host.SendAsync(areYouThere, token);
    Require(identity.S == 1 && identity.F == 2 && identity.SecsItem?.Count == 2
        && identity.SecsItem[0].GetString() == "TrainingDevice", "S1F2 设备身份错误");

    Console.WriteLine("\n步骤 3：Host 发 S2F41 START，设备回 S2F42。回复 HCACK=4 表示接受，动作稍后完成。");
    using var start = new SecsMessage(2, 41) { SecsItem = L(A("START"), L()) };
    using var accepted = await host.SendAsync(start, token);
    Require(accepted.S == 2 && accepted.F == 42 && accepted.SecsItem?[0].FirstValue<byte>() == 4,
        "START 没有被设备接受");
    Console.WriteLine("[Host] 已确认命令被接受，继续等待完成事件。");

    await Task.WhenAll(eventReceived.Task, eventAcknowledged.Task).WaitAsync(token);
    Console.WriteLine("\n演示通过：连接 → 身份查询 → 命令接受 → 动作完成事件 → 事件确认。");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"演示失败：{ex.Message}。请检查端口占用及上面的通信日志。");
    return 1;
}
finally
{
    lifetime.Cancel();
    try { await Task.WhenAll(deviceReader, hostReader); }
    catch (OperationCanceledException) { }
    catch (Exception ex) { Console.Error.WriteLine($"接收循环结束：{ex.Message}"); }
}

async Task ReceiveDeviceMessagesAsync()
{
    try
    {
        await foreach (var incoming in device.GetPrimaryMessageAsync(token))
        {
            using var message = incoming.PrimaryMessage;
            if (message.S == 1 && message.F == 1)
            {
                using var reply = new SecsMessage(1, 2, false) { SecsItem = L(A("TrainingDevice"), A("1.0")) };
                Require(await incoming.TryReplyAsync(reply, token), "身份回复失败");
            }
            else if (message.S == 2 && message.F == 41)
            {
                var knownCommand = message.SecsItem is { Count: 2 } item
                    && item[0].GetString() == "START" && item[1].Count == 0;
                using var reply = new SecsMessage(2, 42, false)
                {
                    // HCACK=4：接受，稍后通过事件报告完成；HCACK=1：未知命令。
                    SecsItem = L(B((byte)(knownCommand ? 4 : 1)), L()),
                };
                Require(await incoming.TryReplyAsync(reply, token), "命令回复失败");
                if (!knownCommand) continue;

                Console.WriteLine("[设备] 开始模拟动作，耗时 1 秒。没有驱动真实硬件。");
                await Task.Delay(1000, token);
                // 教学约定：CEID=1001 表示完成，RPTID=1 包含结果字符串。
                using var completed = new SecsMessage(6, 11)
                {
                    SecsItem = L(U4(0), U4(1001), L(L(U4(1), L(A("COMPLETED"))))),
                };
                using var ack = await device.SendAsync(completed, token);
                Require(ack.S == 6 && ack.F == 12 && ack.SecsItem?.FirstValue<byte>() == 0,
                    "Host 没有正确确认完成事件");
                eventAcknowledged.TrySetResult(true);
            }
            else if (message.ReplyExpected)
            {
                await incoming.TryReplyAsync(cancellation: token);
            }
        }
    }
    catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    catch (Exception ex)
    {
        deviceSelected.TrySetException(ex);
        eventAcknowledged.TrySetException(ex);
        Console.Error.WriteLine($"[设备] 接收失败：{ex.Message}");
        lifetime.Cancel();
    }
}

async Task ReceiveHostMessagesAsync()
{
    try
    {
        await foreach (var incoming in host.GetPrimaryMessageAsync(token))
        {
            using var message = incoming.PrimaryMessage;
            if (message.S == 6 && message.F == 11)
            {
                var item = message.SecsItem ?? throw new InvalidOperationException("事件缺少消息体");
                Require(item[1].FirstValue<uint>() == 1001
                    && item[2][0][0].FirstValue<uint>() == 1
                    && item[2][0][1][0].GetString() == "COMPLETED", "收到的完成事件内容错误");
                Console.WriteLine("[Host] 收到 CEID=1001：动作现在才完成。回复 S6F12 确认事件。");
                using var reply = new SecsMessage(6, 12, false) { SecsItem = B((byte)0) };
                Require(await incoming.TryReplyAsync(reply, token), "事件回复失败");
                eventReceived.TrySetResult(true);
            }
            else if (message.ReplyExpected)
            {
                await incoming.TryReplyAsync(cancellation: token);
            }
        }
    }
    catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    catch (Exception ex)
    {
        hostSelected.TrySetException(ex);
        eventReceived.TrySetException(ex);
        Console.Error.WriteLine($"[Host] 接收失败：{ex.Message}");
        lifetime.Cancel();
    }
}

static TaskCompletionSource<bool> NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
static void Require(bool condition, string description)
{
    if (!condition) throw new InvalidOperationException(description);
}

sealed class TeachingLogger(string role) : ISecsGemLogger
{
    // System Bytes（id）把请求和对应回复关联起来；看日志时比较它们。
    public void MessageIn(SecsMessage msg, int id) => Console.WriteLine($"[{role}] 收到 0x{id:X8}\n{msg.ToSml()}");
    public void MessageOut(SecsMessage msg, int id) => Console.WriteLine($"[{role}] 发出 0x{id:X8}\n{msg.ToSml()}");
    public void Info(string msg) { }
    public void Debug(string msg) { }
    public void Warning(string msg) => Console.Error.WriteLine($"[{role}] 警告：{msg}");
    public void Error(string msg, SecsMessage? message, Exception? ex) => Console.Error.WriteLine($"[{role}] {msg} {ex?.Message}");
}
