namespace EhGalleryDownloader;

public static class DownloadErrorClassifier
{
    public const string AuthenticationFailureMessage =
        "网站拒绝访问：当前登录状态或账号权限无法读取这个画廊。"
        + "请点击“检查登录”，重新导入 Cookie，并确认浏览器能打开同一画廊。";

    private static readonly string[] TransientTerms =
    [
        "timeout", "timed out", "connection", "proxy", "ssl", "eof",
        "429", "503", "502", "network", "reset by peer", "temporarily unavailable"
    ];

    public static bool IsTransient(string? text) =>
        !string.IsNullOrWhiteSpace(text)
        && !IsAuthenticationFailure(text)
        && TransientTerms.Any(term => text.Contains(term, StringComparison.OrdinalIgnoreCase));

    public static bool IsAuthenticationFailure(string? text) =>
        !string.IsNullOrWhiteSpace(text)
        && (text.Contains("AuthenticationError", StringComparison.OrdinalIgnoreCase)
            || text.Contains("AuthorizationError", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Insufficient privileges", StringComparison.OrdinalIgnoreCase)
            || text.Contains("requires you to log on", StringComparison.OrdinalIgnoreCase));

    public static string DescribeFailure(int exitCode, string? rawError, int failedFiles)
    {
        if (IsAuthenticationFailure(rawError)) return AuthenticationFailureMessage;
        if (IsTransient(rawError))
            return "下载因网络连接问题中断，已完成的图片已保留。请检查代理连接或换节点后继续下载。"
                   + $"\n具体原因：{rawError}";
        if (!string.IsNullOrWhiteSpace(rawError))
            return $"下载未完成：{rawError}";
        if (failedFiles > 0)
            return $"有 {failedFiles} 张图片未下载成功，已完成的图片已保留。点击“继续下载”可检查并补下。";
        return $"下载未完成（返回代码 {exitCode}）。请查看任务日志中的具体原因后重试。";
    }
}
