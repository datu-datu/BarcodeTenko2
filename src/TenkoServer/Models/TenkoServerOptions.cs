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
        /// 学生マスタ（students.enc）の復号用パスフレーズ（環境変数 TenkoServer__StudentsPassphrase または data/students.passphrase で指定）
        /// </summary>
        public string StudentsPassphrase { get; set; } = string.Empty;

        /// <summary>
        /// 学生メールアドレスのドメイン (例: tokyo.kosen-ac.jp -> s21021@tokyo.kosen-ac.jp)
        /// </summary>
        public string EmailDomain { get; set; } = "tokyo.kosen-ac.jp";

        /// <summary>
        /// Power Automate の HTTP 要求受信時 Webhook URL
        /// </summary>
        public string PowerAutomateWebhookUrl { get; set; } = string.Empty;

        /// <summary>
        /// メール通知を有効にするかどうか
        /// </summary>
        public bool EnableNotifications { get; set; } = true;
    }
}
