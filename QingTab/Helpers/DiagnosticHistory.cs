using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace QingTab.Helpers;

public enum OpenTabStage
{
    RequestReceived,
    QueueStarted,
    ExplorerReady,
    TabCommandSent,
    TabHandleFound,
    ShellRegistrationFound,
    NavigationSent,
    NavigationStarted,
    NavigationCompleted
}

public enum OpenTabOutcome
{
    OpenedInTab,
    OpenedInWindowFallback,
    StoppedWithoutFallback,
    DuplicateIgnored,
    QueueFullFallback,
    Failed
}

public sealed class DiagnosticHistory
{
    private readonly int _capacity;
    private readonly object _sync = new();
    private readonly Queue<CompletedOperation> _operations = new();
    private long _nextSequence;

    public DiagnosticHistory(int capacity = 20)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    public int Count
    {
        get
        {
            lock (_sync)
                return _operations.Count;
        }
    }

    public OpenTabOperationTrace Begin(string path, nint preferredWindow)
    {
        var sequence = System.Threading.Interlocked.Increment(ref _nextSequence);
        return new OpenTabOperationTrace(
            this,
            sequence,
            ClassifyTarget(path),
            preferredWindow != 0,
            DateTimeOffset.Now);
    }

    public string BuildReport(string version, bool directOpenEnabled, bool autoStartEnabled)
    {
        CompletedOperation[] snapshot;
        lock (_sync)
            snapshot = _operations.ToArray();

        var builder = new StringBuilder();
        builder.AppendLine($"轻页 QingTab v{version}");
        builder.AppendLine($"生成时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        builder.AppendLine($"Windows：{Environment.OSVersion.Version}");
        builder.AppendLine($"新标签接管：{(directOpenEnabled ? "开启" : "关闭")}");
        builder.AppendLine($"开机自启：{(autoStartEnabled ? "开启" : "关闭")}");

        try
        {
            using var process = Process.GetCurrentProcess();
            builder.AppendLine($"进程：专用内存 {process.PrivateMemorySize64 / 1024 / 1024} MB，句柄 {process.HandleCount}");
        }
        catch
        {
            builder.AppendLine("进程：状态暂不可用");
        }

        builder.AppendLine();
        builder.AppendLine($"最近请求：{snapshot.Length}/{_capacity}（不保存完整路径）");
        if (snapshot.Length == 0)
        {
            builder.AppendLine("暂无记录。");
            return builder.ToString();
        }

        var durations = snapshot
            .Select(operation => operation.TotalMilliseconds)
            .OrderBy(milliseconds => milliseconds)
            .ToArray();
        builder.AppendLine(
            $"延迟汇总：P50 {GetPercentile(durations, 0.50)} ms，" +
            $"P95 {GetPercentile(durations, 0.95)} ms");

        foreach (var operation in snapshot.Reverse())
        {
            builder.AppendLine(
                $"#{operation.Sequence} {operation.StartedAt:HH:mm:ss.fff}｜{operation.TargetKind}｜" +
                $"目标窗口：{(operation.PreferredWindowCaptured ? "已捕获" : "自动选择")}｜" +
                $"结果：{GetOutcomeText(operation.Outcome)}｜总耗时：{operation.TotalMilliseconds} ms");
            builder.AppendLine($"  阶段：{FormatStages(operation.Stages)}");
            if (!string.IsNullOrWhiteSpace(operation.FailureCode))
                builder.AppendLine($"  原因：{operation.FailureCode}");
        }

        return builder.ToString();
    }

    private static long GetPercentile(long[] sortedValues, double percentile)
    {
        if (sortedValues.Length == 0) return 0;

        var nearestRank = (int)Math.Ceiling(percentile * sortedValues.Length) - 1;
        var index = Math.Max(0, Math.Min(sortedValues.Length - 1, nearestRank));
        return sortedValues[index];
    }

    private static string FormatStages(StageTiming[] stages)
    {
        var previousElapsed = 0L;
        var parts = new List<string>(stages.Length);
        foreach (var stage in stages)
        {
            var incremental = Math.Max(0, stage.ElapsedMilliseconds - previousElapsed);
            parts.Add(
                $"{GetStageText(stage.Stage)} +{incremental} ms（累计 {stage.ElapsedMilliseconds} ms）");
            previousElapsed = stage.ElapsedMilliseconds;
        }

        return string.Join(" → ", parts);
    }

    internal void Add(CompletedOperation operation)
    {
        lock (_sync)
        {
            while (_operations.Count >= _capacity)
                _operations.Dequeue();
            _operations.Enqueue(operation);
        }
    }

    private static string ClassifyTarget(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "未知位置";
        if (path.StartsWith(@"\\", StringComparison.Ordinal)) return "UNC 网络文件夹";
        if (path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)) return "Shell 位置";

        try
        {
            var root = Path.GetPathRoot(path);
            if (!string.IsNullOrWhiteSpace(root)
                && string.Equals(
                    path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
            {
                return "磁盘根目录";
            }
        }
        catch
        {
            // Classification is diagnostic-only and must never block opening.
        }

        return "本地文件夹";
    }

    private static string GetStageText(OpenTabStage stage)
    {
        switch (stage)
        {
            case OpenTabStage.RequestReceived: return "收到请求";
            case OpenTabStage.QueueStarted: return "开始处理";
            case OpenTabStage.ExplorerReady: return "Explorer 就绪";
            case OpenTabStage.TabCommandSent: return "发送新标签命令";
            case OpenTabStage.TabHandleFound: return "发现标签窗口";
            case OpenTabStage.ShellRegistrationFound: return "发现 Shell 注册";
            case OpenTabStage.NavigationSent: return "发送导航";
            case OpenTabStage.NavigationStarted: return "开始导航";
            case OpenTabStage.NavigationCompleted: return "导航返回";
            default: return stage.ToString();
        }
    }

    private static string GetOutcomeText(OpenTabOutcome outcome)
    {
        switch (outcome)
        {
            case OpenTabOutcome.OpenedInTab: return "已在新标签打开";
            case OpenTabOutcome.OpenedInWindowFallback: return "已回退正常开窗";
            case OpenTabOutcome.StoppedWithoutFallback: return "已安全停止（未打开重复窗口）";
            case OpenTabOutcome.DuplicateIgnored: return "已忽略重复请求";
            case OpenTabOutcome.QueueFullFallback: return "队列繁忙并已回退";
            case OpenTabOutcome.Failed: return "失败";
            default: return outcome.ToString();
        }
    }

    internal sealed class CompletedOperation
    {
        public CompletedOperation(
            long sequence,
            string targetKind,
            bool preferredWindowCaptured,
            DateTimeOffset startedAt,
            OpenTabOutcome outcome,
            long totalMilliseconds,
            StageTiming[] stages,
            string failureCode)
        {
            Sequence = sequence;
            TargetKind = targetKind;
            PreferredWindowCaptured = preferredWindowCaptured;
            StartedAt = startedAt;
            Outcome = outcome;
            TotalMilliseconds = totalMilliseconds;
            Stages = stages;
            FailureCode = failureCode;
        }

        public long Sequence { get; }
        public string TargetKind { get; }
        public bool PreferredWindowCaptured { get; }
        public DateTimeOffset StartedAt { get; }
        public OpenTabOutcome Outcome { get; }
        public long TotalMilliseconds { get; }
        public StageTiming[] Stages { get; }
        public string FailureCode { get; }
    }

    internal sealed class StageTiming
    {
        public StageTiming(OpenTabStage stage, long elapsedMilliseconds)
        {
            Stage = stage;
            ElapsedMilliseconds = elapsedMilliseconds;
        }

        public OpenTabStage Stage { get; }
        public long ElapsedMilliseconds { get; }
    }
}

public sealed class OpenTabOperationTrace
{
    private readonly DiagnosticHistory _history;
    private readonly long _sequence;
    private readonly string _targetKind;
    private readonly bool _preferredWindowCaptured;
    private readonly DateTimeOffset _startedAt;
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private readonly object _sync = new();
    private readonly List<DiagnosticHistory.StageTiming> _stages = new();
    private bool _completed;

    internal OpenTabOperationTrace(
        DiagnosticHistory history,
        long sequence,
        string targetKind,
        bool preferredWindowCaptured,
        DateTimeOffset startedAt)
    {
        _history = history;
        _sequence = sequence;
        _targetKind = targetKind;
        _preferredWindowCaptured = preferredWindowCaptured;
        _startedAt = startedAt;
        Mark(OpenTabStage.RequestReceived);
    }

    public void Mark(OpenTabStage stage)
    {
        lock (_sync)
        {
            if (_completed) return;
            _stages.Add(new DiagnosticHistory.StageTiming(stage, _stopwatch.ElapsedMilliseconds));
        }
    }

    public void Complete(OpenTabOutcome outcome, string failureCode = "")
    {
        DiagnosticHistory.CompletedOperation? completed;
        lock (_sync)
        {
            if (_completed) return;
            _completed = true;
            _stopwatch.Stop();
            completed = new DiagnosticHistory.CompletedOperation(
                _sequence,
                _targetKind,
                _preferredWindowCaptured,
                _startedAt,
                outcome,
                _stopwatch.ElapsedMilliseconds,
                _stages.ToArray(),
                SanitizeFailureCode(failureCode));
        }

        _history.Add(completed);
    }

    private static string SanitizeFailureCode(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var builder = new StringBuilder(Math.Min(value.Length, 120));
        foreach (var character in value.Take(120))
        {
            if (char.IsLetterOrDigit(character) || character == '-' || character == '_' || character == '.')
                builder.Append(character);
            else
                builder.Append('-');
        }
        return builder.ToString();
    }
}
