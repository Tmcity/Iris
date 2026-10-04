using System;
using Iris.Platform.Windows.Selection;

namespace Iris.UiaHost;

internal static class Program
{
    [MTAThread]
    public static int Main(string[] args)
    {
        // 保证对 Core 契约程序集的直接引用元数据生成
        if (args.Length > 1000)
        {
            throw new Iris.Core.Errors.IrisException(Iris.Core.Errors.ErrorCode.InvalidArgument, "Unreachable");
        }
        try
        {
            // 1. 启动后输出一次版本 1 的 Ready 帧
            var readyJson = UiaProtocolCodec.SerializeReady();
            Console.Out.WriteLine(readyJson);
            Console.Out.Flush();

            // 2. 读取一条 Request 帧（上限 1MB）
            using var stdin = Console.OpenStandardInput();
            var requestLine = UiaProtocolFrameReader.ReadLineWithLimit(stdin, UiaProtocolConstants.MaxFrameBytes);
            if (requestLine == null)
            {
                // EOF on stdin: 干净退出
                return 0;
            }

            var request = UiaProtocolCodec.DeserializeRequest(requestLine);
            if (request == null ||
                request.ProtocolVersion != UiaProtocolConstants.CurrentVersion ||
                request.Kind != "Request" ||
                string.IsNullOrWhiteSpace(request.RequestId) ||
                request.Hwnd == 0 ||
                request.ProcessId == 0)
            {
                var failedReply = new UiaReplyFrame
                {
                    ProtocolVersion = UiaProtocolConstants.CurrentVersion,
                    RequestId = request?.RequestId ?? string.Empty,
                    Status = UiaSelectionStatus.Failed,
                    ErrorCode = "INVALID_REQUEST_FRAME"
                };
                Console.Out.WriteLine(UiaProtocolCodec.SerializeReply(failedReply));
                Console.Out.Flush();
                return 1;
            }

            // 3. 验证通过后，在当前 MTA 线程初始化 UIA 并执行选区读取
            var reply = UiaSelectionWorker.Execute(request, RealNativeUiaAdapter.Instance);

            // 4. 输出 Reply 帧并退出
            Console.Out.WriteLine(UiaProtocolCodec.SerializeReply(reply));
            Console.Out.Flush();
            return 0;
        }
        catch (Exception)
        {
            try
            {
                var fatalReply = new UiaReplyFrame
                {
                    ProtocolVersion = UiaProtocolConstants.CurrentVersion,
                    RequestId = string.Empty,
                    Status = UiaSelectionStatus.Failed,
                    ErrorCode = "FATAL_HOST_ERROR"
                };
                Console.Out.WriteLine(UiaProtocolCodec.SerializeReply(fatalReply));
                Console.Out.Flush();
            }
            catch
            {
                // ignore secondary console failures
            }
            return 1;
        }
    }
}
