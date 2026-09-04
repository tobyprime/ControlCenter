using DevicePanel.Web.Metrics;

namespace DevicePanel.Web.Alerting;

/// <summary>
/// 告警规则类型扩展点（约束 B：告警规则化）——每种规则类型 = 实现本接口 + 注册 DI
/// （services.AddSingleton&lt;IAlertRuleType, XxxRuleType&gt;()），核心评估引擎只依赖本接口，
/// 不内置任何具体指标的业务含义，也不把告警模型硬绑上下限。
/// </summary>
public interface IAlertRuleType
{
    /// <summary>规则类型标识（存入 alert_rules.rule_type）。</summary>
    string TypeId { get; }

    /// <summary>中文名（UI 展示）。</summary>
    string DisplayName { get; }

    /// <summary>告警消息标题。</summary>
    string AlertTitle { get; }

    /// <summary>中文说明（UI 展示触发语义与参数）。</summary>
    string Description { get; }

    /// <summary>适用于哪些指标值类型（与注册的 metric key 类型匹配才允许建规则）。</summary>
    IReadOnlyList<MetricValueType> SupportedValueTypes { get; }

    /// <summary>true = 采样驱动（新样本写入时评估）；false = 时间驱动（后台周期扫描评估，如"无数据"）。</summary>
    bool SampleDriven { get; }

    /// <summary>校验用户参数 JSON；不合法返回中文错误信息，合法返回 null。</summary>
    string? ValidateParameters(string parametersJson);

    /// <summary>采样驱动评估：该样本是否满足触发条件。</summary>
    bool IsViolated(string parametersJson, MetricSample sample);

    /// <summary>时间驱动评估：数据缺失时长（从未上报 = null）是否满足触发条件。</summary>
    bool IsSweepViolated(string parametersJson, TimeSpan? dataAge);

    /// <summary>告警正文中的违规描述（引擎前置"目标「X」指标 Y"前缀，本方法生成其余部分，含已持续时长）。</summary>
    string DescribeViolation(string parametersJson, MetricSample? latestSample, string unit, TimeSpan sustained);
}
