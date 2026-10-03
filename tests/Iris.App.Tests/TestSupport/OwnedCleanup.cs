using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;

namespace Iris.App.Tests.TestSupport;

/// <summary>
/// 测试自有资源清理记录器（仅测试使用）。
/// 每个清理步骤的失败都会被记录并在最后传播；依赖未退出时跳过的释放步骤也会被列出。
/// 清理失败绝不会被吞掉后当作清理完成。
/// </summary>
public sealed class OwnedCleanup
{
    private readonly List<Exception> _errors = [];
    private readonly List<string> _skipped = [];

    public IReadOnlyList<Exception> Errors => _errors;

    public IReadOnlyList<string> Skipped => _skipped;

    public bool HasFailures => _errors.Count > 0;

    public bool Step(string name, Action step)
    {
        try
        {
            step();
            return true;
        }
        catch (Exception ex)
        {
            Record(name, ex);
            return false;
        }
    }

    public async Task<bool> StepAsync(string name, Func<Task> step)
    {
        try
        {
            await step().ConfigureAwait(true);
            return true;
        }
        catch (Exception ex)
        {
            Record(name, ex);
            return false;
        }
    }

    /// <summary>因前置退出步骤失败而不具备释放资格的步骤：不执行，只记录诊断。</summary>
    public void Skip(string name, string reason) => _skipped.Add($"{name}（{reason}）");

    public void Record(string name, Exception ex) => _errors.Add(new OwnedCleanupStepException(name, ex));

    /// <summary>
    /// 按测试主体结果与清理结果决定最终异常：
    /// 仅主体失败 → 原样重抛主体异常；仅清理失败 → 抛出清理聚合异常；
    /// 两者都失败 → 聚合异常中第一个是主体异常，其后为全部清理错误。
    /// </summary>
    public void ThrowIfFailed(Exception? primary)
    {
        if (_errors.Count == 0)
        {
            if (primary != null)
            {
                ExceptionDispatchInfo.Capture(primary).Throw();
            }
            return;
        }

        string skipped = _skipped.Count == 0 ? "无" : string.Join("；", _skipped);
        if (primary == null)
        {
            throw new AggregateException(
                $"自有资源清理失败（{_errors.Count} 项）；跳过的释放步骤：{skipped}",
                _errors);
        }

        throw new AggregateException(
            $"测试主体失败，且自有资源清理失败（{_errors.Count} 项）；跳过的释放步骤：{skipped}",
            new[] { primary }.Concat(_errors));
    }

    /// <summary>
    /// 先执行测试主体，再无条件执行自有清理；<paramref name="outermost"/> 位于最外层 finally，
    /// 即使清理委托本身抛出也会执行（用于恢复静态委托、关闭旁观窗口等）。
    /// </summary>
    public static async Task RunAsync(
        Func<Task> body,
        Func<OwnedCleanup, Task> cleanup,
        Func<OwnedCleanup, Task>? outermost = null)
    {
        Exception? primary = null;
        try
        {
            await body().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            primary = ex;
        }

        var owned = new OwnedCleanup();
        try
        {
            await cleanup(owned).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            owned.Record("cleanup", ex);
        }
        finally
        {
            if (outermost != null)
            {
                try
                {
                    await outermost(owned).ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    owned.Record("outermost", ex);
                }
            }
        }

        owned.ThrowIfFailed(primary);
    }
}

public sealed class OwnedCleanupStepException(string stepName, Exception inner)
    : Exception($"清理步骤失败：{stepName}：{inner.GetType().Name}: {inner.Message}", inner)
{
    public string StepName { get; } = stepName;
}
