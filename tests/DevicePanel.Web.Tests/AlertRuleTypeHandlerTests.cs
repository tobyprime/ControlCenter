using DevicePanel.Web.Alerting;
using DevicePanel.Web.Devices;
using DevicePanel.Web.Metrics;
using DevicePanel.Web.Targets;
using Xunit;

namespace DevicePanel.Web.Tests;

/// <summary>规则类型处理器语义（约束 B）：防抖沿用一期语义，阈值/无数据/状态不符三类参数化到规则。</summary>
public class AlertRuleTypeHandlerTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-09-01T00:00:00Z");

    private static readonly TargetInfo DeviceTarget = new(1, TargetTypes.Device, "host-1", 11, T0, T0);

    private static readonly MetricKeyInfo CpuKey = new("cpu", MetricValueType.Number, "%", "CPU 使用率");

    private static readonly MetricKeyInfo StatusKey = new("status", MetricValueType.Enum, null, "服务状态");

    private static AlertRuleContext SampleCtx(
        AlertRule rule,
        string? stateJson = null,
        double? num = null,
        string? text = null,
        DateTimeOffset? now = null,
        DateTimeOffset? lastData = null,
        MetricKeyInfo? metric = null) =>
        new(rule, DeviceTarget, metric ?? (rule.Metric is null ? null : CpuKey), now ?? T0, stateJson,
            SampleNum: num, SampleText: text, LastDataUtc: lastData);

    private static AlertRule ThresholdRule(string paramsJson, string? metric = "cpu") =>
        new(7, DeviceTarget.Id, metric, "x", paramsJson, true, T0, T0);

    private static AlertRule NewThresholdRule(double threshold, int sustainSeconds = 60, int repeatMinutes = 0) =>
        ThresholdRule(AlertRuleParamsSerializer.SerializeThreshold(threshold, sustainSeconds, repeatMinutes));

    // ---- threshold_above ----

    [Fact]
    public void ThresholdAbove_Waits_Sustain_Window_Before_Firing()
    {
        var handler = new ThresholdAboveRuleHandler(new AlertOptions());
        var rule = NewThresholdRule(90);

        // 未越限：无动作
        Assert.Equal(AlertRuleAction.None, handler.Evaluate(SampleCtx(rule, num: 50)).Action);
        // 首次越限：记录首见时间，等待
        var first = handler.Evaluate(SampleCtx(rule, num: 95, now: T0));
        Assert.Equal(AlertRuleAction.None, first.Action);
        Assert.NotNull(first.StateJson);
        // 持续不足：继续等待
        var waiting = handler.Evaluate(SampleCtx(rule, num: 95, now: T0.AddSeconds(30), stateJson: first.StateJson));
        Assert.Equal(AlertRuleAction.None, waiting.Action);
        // 持续满窗口：告警，状态带最近告警时间
        var fired = handler.Evaluate(SampleCtx(rule, num: 95, now: T0.AddSeconds(61), stateJson: first.StateJson));
        Assert.Equal(AlertRuleAction.Fire, fired.Action);
        Assert.NotNull(fired.Message);
        Assert.Contains("超过阈值 90.0%", fired.Message!.Content);
        Assert.Contains("CPU 使用率当前 95.0%", fired.Message!.Content);
        Assert.Contains("已持续 61 秒", fired.Message!.Content);
    }

    [Fact]
    public void ThresholdAbove_Recovery_Clears_State_And_New_Event_Retains()
    {
        var handler = new ThresholdAboveRuleHandler(new AlertOptions());
        var rule = NewThresholdRule(90);
        var first = handler.Evaluate(SampleCtx(rule, num: 95, now: T0));
        var fired = handler.Evaluate(SampleCtx(rule, num: 96, now: T0.AddSeconds(61), stateJson: first.StateJson));

        // 回落：恢复（清状态），不发消息
        var recovered = handler.Evaluate(SampleCtx(rule, num: 80, now: T0.AddSeconds(120), stateJson: fired.StateJson));
        Assert.Equal(AlertRuleAction.Clear, recovered.Action);
        Assert.Null(recovered.Message);

        // 恢复后再次越限 = 全新事件，重新等待防抖
        var secondEvent = handler.Evaluate(SampleCtx(rule, num: 95, now: T0.AddSeconds(150), stateJson: null));
        Assert.Equal(AlertRuleAction.None, secondEvent.Action);
        Assert.NotNull(secondEvent.StateJson);
    }

    [Fact]
    public void ThresholdAbove_RepeatMinutes_ReAlerts_While_Sustained()
    {
        var handler = new ThresholdAboveRuleHandler(new AlertOptions());
        var rule = NewThresholdRule(90, sustainSeconds: 0, repeatMinutes: 10);

        var first = handler.Evaluate(SampleCtx(rule, num: 95, now: T0));
        Assert.Equal(AlertRuleAction.Fire, first.Action);

        // 10 分钟内不重发
        var soon = handler.Evaluate(SampleCtx(rule, num: 95, now: T0.AddMinutes(5), stateJson: first.StateJson));
        Assert.Equal(AlertRuleAction.None, soon.Action);
        // 满重发间隔：再次告警
        var later = handler.Evaluate(SampleCtx(rule, num: 95, now: T0.AddMinutes(10), stateJson: first.StateJson));
        Assert.Equal(AlertRuleAction.Fire, later.Action);
    }

    [Fact]
    public void ThresholdAbove_State_Json_Is_Phase1_Compatible()
    {
        // 一期 ThresholdAlertEvaluator 的状态形状（PascalCase 序列化），迁移后必须能无缝续接
        var phase1State = """{"FirstSeenUtc":"2026-09-01T00:00:00+00:00","LastAlertedUtc":null}""";
        var handler = new ThresholdAboveRuleHandler(new AlertOptions());
        var rule = NewThresholdRule(90);

        var fired = handler.Evaluate(SampleCtx(rule, num: 95, now: T0.AddSeconds(61), stateJson: phase1State));

        Assert.Equal(AlertRuleAction.Fire, fired.Action);
    }

    [Fact]
    public void ThresholdAbove_ValidateParams()
    {
        var handler = new ThresholdAboveRuleHandler(new AlertOptions());
        Assert.Null(handler.ValidateParams("""{"threshold": 85}"""));
        Assert.Null(handler.ValidateParams("""{"threshold": 85, "sustainSeconds": 30, "repeatMinutes": 5}"""));
        Assert.NotNull(handler.ValidateParams("""{}"""));
        Assert.NotNull(handler.ValidateParams("""{"threshold": 85, "sustainSeconds": -1}"""));
    }

    // ---- threshold_below ----

    [Fact]
    public void ThresholdBelow_Fires_When_Sustained_Below()
    {
        var handler = new ThresholdBelowRuleHandler(new AlertOptions());
        var rule = ThresholdRule(AlertRuleParamsSerializer.SerializeThreshold(10, 0, 0));

        var fired = handler.Evaluate(SampleCtx(rule, num: 5, now: T0));

        Assert.Equal(AlertRuleAction.Fire, fired.Action);
        Assert.Contains("低于阈值 10.0%", fired.Message!.Content);
        // 恢复：回升到阈值及以上
        var recovered = handler.Evaluate(SampleCtx(rule, num: 12, now: T0.AddSeconds(1), stateJson: fired.StateJson));
        Assert.Equal(AlertRuleAction.Clear, recovered.Action);
    }

    // ---- status_mismatch ----

    [Fact]
    public void StatusMismatch_Fires_Immediately_And_Recovers_On_Match()
    {
        var handler = new StatusMismatchRuleHandler(new AlertOptions());
        var rule = ThresholdRule(AlertRuleParamsSerializer.SerializeStatusMismatch("online", 0, 0), metric: "status");
        var ctx = SampleCtx(rule, text: "offline", metric: StatusKey);

        var fired = handler.Evaluate(ctx with { NowUtc = T0 });
        Assert.Equal(AlertRuleAction.Fire, fired.Action);
        Assert.Contains("当前为「offline」", fired.Message!.Content);
        Assert.Contains("不符合期望「online」", fired.Message!.Content);

        // 恢复：回到期望值
        var recovered = handler.Evaluate(ctx with { NowUtc = T0.AddSeconds(5), StateJson = fired.StateJson, SampleText = "online" });
        Assert.Equal(AlertRuleAction.Clear, recovered.Action);
    }

    [Fact]
    public void StatusMismatch_Supports_Sustain_Window()
    {
        var handler = new StatusMismatchRuleHandler(new AlertOptions());
        var rule = ThresholdRule(AlertRuleParamsSerializer.SerializeStatusMismatch("online", 30, 0), metric: "status");
        var ctx = SampleCtx(rule, text: "offline", metric: StatusKey);

        var waiting = handler.Evaluate(ctx);
        Assert.Equal(AlertRuleAction.None, waiting.Action);
        var fired = handler.Evaluate(ctx with { NowUtc = T0.AddSeconds(31), StateJson = waiting.StateJson });
        Assert.Equal(AlertRuleAction.Fire, fired.Action);
    }

    // ---- no_data ----

    [Fact]
    public void NoData_NeverReported_DoesNotAlert()
    {
        var handler = new NoDataRuleHandler(new AgentOptions());
        var rule = ThresholdRule(AlertRuleParamsSerializer.SerializeNoData(60), metric: null);

        var decision = handler.Evaluate(SampleCtx(rule, now: T0, lastData: null));

        Assert.Equal(AlertRuleAction.None, decision.Action);
    }

    [Fact]
    public void NoData_Fires_Once_Per_Event_And_Recovers()
    {
        var handler = new NoDataRuleHandler(new AgentOptions());
        var rule = ThresholdRule(AlertRuleParamsSerializer.SerializeNoData(60), metric: null);

        // 窗口内：无动作
        var healthy = handler.Evaluate(SampleCtx(rule, now: T0, lastData: T0.AddSeconds(-30)));
        Assert.Equal(AlertRuleAction.None, healthy.Action);
        // 断流：告警一次（离线文案与一期一致）
        var fired = handler.Evaluate(SampleCtx(rule, now: T0.AddSeconds(120), lastData: T0));
        Assert.Equal(AlertRuleAction.Fire, fired.Action);
        Assert.Equal("设备离线告警", fired.Message!.Title);
        Assert.Contains("host-1」已离线（超过 60 秒未上报心跳）", fired.Message!.Content);
        // 已告警（含重启后重放）：不重复
        var again = handler.Evaluate(SampleCtx(rule, now: T0.AddSeconds(180), lastData: T0, stateJson: fired.StateJson));
        Assert.Equal(AlertRuleAction.None, again.Action);
        // 恢复：关态
        var recovered = handler.Evaluate(SampleCtx(rule, now: T0.AddSeconds(240), lastData: T0.AddSeconds(230), stateJson: fired.StateJson));
        Assert.Equal(AlertRuleAction.Clear, recovered.Action);
    }

    [Fact]
    public void NoData_With_Metric_Uses_Metric_Message()
    {
        var handler = new NoDataRuleHandler(new AgentOptions());
        var rule = ThresholdRule(AlertRuleParamsSerializer.SerializeNoData(600));

        var fired = handler.Evaluate(SampleCtx(rule, now: T0.AddMinutes(20), lastData: T0));

        Assert.Equal(AlertRuleAction.Fire, fired.Action);
        Assert.Equal("指标无数据告警", fired.Message!.Title);
        Assert.Contains("指标 CPU 使用率 已超过 600 秒无数据", fired.Message!.Content);
    }

    [Fact]
    public void NoData_ValidateParams()
    {
        var handler = new NoDataRuleHandler(new AgentOptions());
        Assert.Null(handler.ValidateParams("""{"windowSeconds": 300}"""));
        Assert.Null(handler.ValidateParams("{}"));
        Assert.NotNull(handler.ValidateParams("""{"windowSeconds": 0}"""));
    }

    [Fact]
    public void NoData_Scans_On_Schedule_Others_Do_Not()
    {
        Assert.True(new NoDataRuleHandler(new AgentOptions()).ScansOnSchedule);
        Assert.False(new ThresholdAboveRuleHandler(new AlertOptions()).ScansOnSchedule);
        Assert.False(new ThresholdBelowRuleHandler(new AlertOptions()).ScansOnSchedule);
        Assert.False(new StatusMismatchRuleHandler(new AlertOptions()).ScansOnSchedule);
    }
}
