namespace TenkoServer.Services
{
    public interface IScanAcceptanceService
    {
        /// <summary>クライアントからの点呼データ受付を受け入れるかどうか (テスト時などに拒否できる)</summary>
        bool IsAcceptingScans { get; set; }
    }

    /// <summary>
    /// 点呼データの受付可否を保持するインメモリの状態サービス。
    /// 既定値は true (受け入れる)。管理パネルからのトグルでのみ変化し、
    /// 再起動後は常に受け入れる状態に戻る (誤って拒否し続ける事故を防ぐ)。
    /// </summary>
    public class ScanAcceptanceService : IScanAcceptanceService
    {
        public bool IsAcceptingScans { get; set; } = true;
    }
}
