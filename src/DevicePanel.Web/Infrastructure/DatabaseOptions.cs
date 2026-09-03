namespace DevicePanel.Web.Infrastructure;

public sealed class DatabaseOptions
{
    public const string SectionName = "DevicePanel:Database";

    /// <summary>数据库文件所在目录，默认为内容根目录下 data/。</summary>
    public string DataDir { get; set; } = "data";

    public string DatabaseFileName { get; set; } = "device-panel.db";

    public string DatabasePath => Path.Combine(DataDir, DatabaseFileName);
}
