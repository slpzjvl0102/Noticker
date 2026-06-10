using System.IO;

namespace Noticker.Infrastructure;

// 동기화 진단 로그 — %APPDATA%\Noticker\sync.log.
// 개인 앱이라 텔레메트리는 없지만, pull/push의 조용한 실패(silent-catch 어법)는
// 사용자 보고만으로 진단이 불가능해 최소한의 파일 로그를 남긴다. 실패해도 무해
public static class SyncLog
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Noticker", "sync.log");

    public static void Write(string message)
    {
        try
        {
            // 1MB 넘으면 리셋 — 무한 성장 방지
            if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 1_000_000)
                File.Delete(LogPath);
            File.AppendAllText(LogPath,
                $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}Z {message}\n");
        }
        catch { /* 로그 실패는 무시 */ }
    }
}
