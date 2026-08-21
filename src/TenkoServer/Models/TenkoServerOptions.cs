namespace TenkoServer.Models
{
    public class TenkoServerOptions
    {
        public const string SectionName = "TenkoServer";

        /// <summary>
        /// 端末（Tenko.Native）通信用の API キー (ヘッダー X-API-Key で検証)
        /// 未設定の場合は起動時にエラーとなる
        /// </summary>
        public string ApiKey { get; set; } = string.Empty;

        /// <summary>
        /// Web管理ビューのログインパスワード
        /// 未設定の場合は起動時にエラーとなる
        /// </summary>
        public string AdminPassword { get; set; } = string.Empty;

        /// <summary>
        /// Power Automate の HTTP 要求受信時 Webhook URL
        /// </summary>
        public string PowerAutomateWebhookUrl { get; set; } = string.Empty;

        /// <summary>
        /// メール通知を有効にするかどうか
        /// </summary>
        public bool EnableNotifications { get; set; } = true;

        /// <summary>
        /// 1回のWebhook送信にまとめる通知の最大件数（この件数に達した時点で送信する）
        /// </summary>
        public int MaxNotificationsPerBatch { get; set; } = 20;

        /// <summary>
        /// 最後のスキャン受信からバッチ送信まで待機する秒数（この時間が経過した時点で送信する）
        /// </summary>
        public int NotificationBatchWindowSeconds { get; set; } = 10;
    }
}
