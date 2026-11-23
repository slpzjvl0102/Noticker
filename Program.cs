using Noticker.Infrastructure;
using Velopack;

namespace Noticker;

// 진입점 — Velopack은 설치·업데이트·제거 때 앱을 특수 인자로 잠깐 실행하므로,
// WPF가 창을 만들기 전에 VelopackApp.Run()이 가장 먼저 처리해야 한다.
// (App.xaml이 자동 생성하는 Main 대신 csproj의 StartupObject로 이 Main을 쓴다)
public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build()
            // 제거 시 자동 실행 레지스트리를 지워, 지워진 exe를 Windows가 찾지 않게 한다
            .OnBeforeUninstallFastCallback(_ => StartupManager.Disable())
            .Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
