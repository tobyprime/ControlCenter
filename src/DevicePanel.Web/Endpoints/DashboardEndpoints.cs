using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using DevicePanel.Web.Dashboard;
using Microsoft.AspNetCore.Mvc;

namespace DevicePanel.Web.Endpoints;

/// <summary>主页布局读写 API（TOB-366，前端 TOB-367 依赖本契约）：
/// GET /api/dashboard/layout 返回当前布局，未配置时返回服务端默认布局；
/// PUT 整份替换保存，载荷 { cards: [{ id, type, sort, visible, config }] }。
/// type 按服务端卡片目录校验，指标卡 config 校验来源结构（未知键仍透传，语义归前端），
/// 其余 config 为 JSON 透传对象，缺省/null 归一化为 {}；
/// 卡片按 sort 升序返回；非法载荷返回 400 + { error }。</summary>
public static class DashboardEndpoints
{
    private const int MaxCardFieldLength = 128;

    public static IEndpointRouteBuilder MapDashboardEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var dashboard = endpoints.MapGroup("/api/dashboard");

        dashboard.MapGet("/layout", (IDashboardLayoutStore store) =>
        {
            var layout = store.GetLayout() ?? DashboardDefaultLayout.Create();
            return Results.Ok(ToResponse(layout));
        });

        dashboard.MapPut("/layout", ([FromBody] JsonElement body, IDashboardLayoutStore store) =>
        {
            if (!TryParseLayout(body, out var layout, out var error))
            {
                return Results.BadRequest(new { error });
            }

            store.SaveLayout(layout);
            return Results.NoContent();
        });

        return endpoints;
    }

    private static object ToResponse(DashboardLayout layout) => new
    {
        cards = layout.Cards.OrderBy(c => c.Sort).Select(c => new
        {
            id = c.Id,
            type = c.Type,
            sort = c.Sort,
            visible = c.Visible,
            config = c.Config,
        }).ToArray(),
    };

    private static bool TryParseLayout(JsonElement body, out DashboardLayout layout, [NotNullWhen(false)] out string? error)
    {
        layout = new DashboardLayout([]);
        error = null;

        if (body.ValueKind != JsonValueKind.Object)
        {
            error = "布局载荷必须是 JSON 对象";
            return false;
        }

        if (!body.TryGetProperty("cards", out var cardsElement))
        {
            error = "布局载荷缺少 cards 数组";
            return false;
        }

        if (cardsElement.ValueKind != JsonValueKind.Array)
        {
            error = "cards 必须是 JSON 数组";
            return false;
        }

        if (cardsElement.GetArrayLength() == 0)
        {
            error = "布局至少需要一张卡片";
            return false;
        }

        var cards = new List<DashboardCard>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var cardElement in cardsElement.EnumerateArray())
        {
            index++;
            if (!TryParseCard(cardElement, index, out var card, out error))
            {
                return false;
            }

            if (!ids.Add(card.Id))
            {
                error = $"卡片 id 重复：{card.Id}";
                return false;
            }

            cards.Add(card);
        }

        layout = new DashboardLayout(cards);
        return true;
    }

    private static bool TryParseCard(JsonElement element, int index, out DashboardCard card, [NotNullWhen(false)] out string? error)
    {
        card = new DashboardCard(string.Empty, string.Empty, 0, Visible: false, default);
        error = null;

        if (element.ValueKind != JsonValueKind.Object)
        {
            error = $"第 {index} 张卡片必须是 JSON 对象";
            return false;
        }

        var subject = $"第 {index} 张卡片";
        if (!TryGetStringProperty(element, "id", subject, out var id, out error))
        {
            return false;
        }

        if (!TryGetStringProperty(element, "type", subject, out var type, out error))
        {
            return false;
        }

        if (!DashboardCardCatalog.IsKnownType(type))
        {
            error = $"{subject} type 未知：{type}";
            return false;
        }

        if (!element.TryGetProperty("sort", out var sortElement)
            || sortElement.ValueKind != JsonValueKind.Number
            || !sortElement.TryGetInt32(out var sort)
            || sort < 0)
        {
            error = $"{subject} sort 必须是非负整数";
            return false;
        }

        if (!element.TryGetProperty("visible", out var visibleElement)
            || visibleElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            error = $"{subject} visible 必须是布尔值";
            return false;
        }

        if (element.TryGetProperty("config", out var configElement)
            && configElement.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null))
        {
            error = $"{subject} config 必须是 JSON 对象";
            return false;
        }

        var config = configElement.ValueKind == JsonValueKind.Object
            ? configElement.Clone()
            : EmptyObject();

        if (DashboardCardCatalog.IsMetricType(type)
            && !TryValidateMetricConfig(config, subject, out error))
        {
            return false;
        }

        if (type == DashboardCardCatalog.TypeControl
            && !TryValidateControlConfig(config, subject, out error))
        {
            return false;
        }

        card = new DashboardCard(id, type, sort, visibleElement.GetBoolean(), config);
        return true;
    }

    /// <summary>
    /// 指标卡来源结构校验（与前端 parseMetricCardConfig 的结构契约对齐）：
    /// targetId 正整数、key 非空、windowHours 可选但出现时必须为正有限数；未知键仍透传（语义归前端）。
    /// </summary>
    private static bool TryValidateMetricConfig(JsonElement config, string subject, [NotNullWhen(false)] out string? error)
    {
        error = null;
        if (config.ValueKind != JsonValueKind.Object)
        {
            error = $"{subject} 为指标卡，必须携带 config（targetId、key）";
            return false;
        }

        if (!config.TryGetProperty("targetId", out var targetIdElement)
            || targetIdElement.ValueKind != JsonValueKind.Number
            || !targetIdElement.TryGetInt64(out var targetId)
            || targetId <= 0)
        {
            error = $"{subject} config.targetId 必须是正整数";
            return false;
        }

        if (!config.TryGetProperty("key", out var keyElement)
            || keyElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(keyElement.GetString()))
        {
            error = $"{subject} config.key 必须是非空字符串";
            return false;
        }

        if (keyElement.GetString() is { Length: > MaxCardFieldLength })
        {
            error = $"{subject} config.key 长度不能超过 {MaxCardFieldLength} 个字符";
            return false;
        }

        if (config.TryGetProperty("windowHours", out var windowElement)
            && (windowElement.ValueKind != JsonValueKind.Number
                || !double.IsFinite(windowElement.GetDouble())
                || windowElement.GetDouble() <= 0))
        {
            error = $"{subject} config.windowHours 必须是正数";
            return false;
        }

        return true;
    }

    /// <summary>
    /// 控制卡编排结构校验（与前端 parseControlCardConfig 的结构契约对齐）：
    /// controllers 非空数组，每项 { collectorId 正整数, key 非空字符串 }；未知键仍透传（语义归前端）。
    /// </summary>
    private static bool TryValidateControlConfig(JsonElement config, string subject, [NotNullWhen(false)] out string? error)
    {
        error = null;
        if (config.ValueKind != JsonValueKind.Object)
        {
            error = $"{subject} 为控制卡，必须携带 config（controllers）";
            return false;
        }

        if (!config.TryGetProperty("controllers", out var controllers)
            || controllers.ValueKind != JsonValueKind.Array
            || controllers.GetArrayLength() == 0)
        {
            error = $"{subject} config.controllers 必须是非空数组";
            return false;
        }

        var index = 0;
        foreach (var entry in controllers.EnumerateArray())
        {
            index++;
            if (entry.ValueKind != JsonValueKind.Object
                || !entry.TryGetProperty("collectorId", out var collectorIdElement)
                || collectorIdElement.ValueKind != JsonValueKind.Number
                || !collectorIdElement.TryGetInt64(out var collectorId)
                || collectorId <= 0)
            {
                error = $"{subject} config.controllers 第 {index} 项的 collectorId 必须是正整数";
                return false;
            }

            if (!entry.TryGetProperty("key", out var keyElement)
                || keyElement.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(keyElement.GetString()))
            {
                error = $"{subject} config.controllers 第 {index} 项的 key 必须是非空字符串";
                return false;
            }

            if (keyElement.GetString() is { Length: > MaxCardFieldLength })
            {
                error = $"{subject} config.controllers 第 {index} 项的 key 长度不能超过 {MaxCardFieldLength} 个字符";
                return false;
            }
        }

        return true;
    }

    private static bool TryGetStringProperty(
        JsonElement element,
        string name,
        string subject,
        out string value,
        [NotNullWhen(false)] out string? error)
    {
        value = string.Empty;
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
        {
            error = $"{subject} {name} 必须是非空字符串";
            return false;
        }

        value = property.GetString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            error = $"{subject} {name} 必须是非空字符串";
            return false;
        }

        if (value.Length > MaxCardFieldLength)
        {
            error = $"{subject} {name} 长度不能超过 {MaxCardFieldLength} 个字符";
            return false;
        }

        error = null;
        return true;
    }

    private static JsonElement EmptyObject() => JsonDocument.Parse("{}").RootElement.Clone();
}
