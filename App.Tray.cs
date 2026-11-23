using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Forms;
using Microsoft.Data.Sqlite;
using Noticker.Data;
using Noticker.Infrastructure;
using Noticker.Models;
using Noticker.Services;
using Noticker.Sync;
using Noticker.Windows;

namespace Noticker;

// App partial — 트레이 아이콘·메뉴·풍선 알림·포모도로 미니 웨지 아이콘. (App.xaml.cs에서 분리, 동작 변경 없음)
public partial class App
{
    private NotifyIcon? _trayIcon;
    private string? _lastTrayTooltip;
    // 트레이 미니 웨지 — 타이머 동작 중 아이콘 교체, Idle이면 기본 아이콘 복원
    private System.Drawing.Icon? _baseTrayIcon;
    private System.Drawing.Icon? _wedgeTrayIcon;
    private (int Bucket, string? Key, bool Active) _trayWedgeKey = (-1, null, false);

    private static System.Drawing.Icon LoadTrayIcon()
    {
        var uri = new Uri("pack://application:,,,/Assets/noticker.ico");
        var stream = System.Windows.Application.GetResourceStream(uri)?.Stream;
        return stream is not null
            ? new System.Drawing.Icon(stream)
            : SystemIcons.Application;
    }

    private void InitTray()
    {
        _baseTrayIcon = LoadTrayIcon();
        _trayIcon = new NotifyIcon
        {
            Icon = _baseTrayIcon,
            Visible = true,
            Text = "Noticker"
        };

        _trayIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                OpenNoteList();
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add("노트 목록", null, (_, _) => OpenNoteList());
        menu.Items.Add("새 스티커", null, (_, _) => CreateSticker());
        menu.Items.Add("포모도로 타이머", null, (_, _) => OpenPomodoro());
        menu.Items.Add("모든 스티커 표시", null, (_, _) => ShowAllStickers());
        menu.Items.Add("수동 Sync", null, async (_, _) => await RetryPendingAsync());
        menu.Items.Add("설정", null, (_, _) => OpenSettings());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("종료", null, (_, _) => ExitApp());

        _trayIcon.ContextMenuStrip = menu;
    }

    private void UpdateTrayWedgeIcon()
    {
        if (IsShuttingDown || _trayIcon is null || _pomodoro is null) return;

        bool active = _pomodoro.State != PomodoroState.Idle;
        // 분 단위 quantize — 16px 아이콘에서 초 단위 재렌더는 무의미 (GDI 핸들 churn 방지)
        int bucket = active ? (int)Math.Ceiling(_pomodoro.WedgeFraction * 60) : -1;
        var key = (bucket, _pomodoroWedgeColorKey, active);
        if (key == _trayWedgeKey) return;
        _trayWedgeKey = key;

        var old = _wedgeTrayIcon;
        try
        {
            if (!active)
            {
                _trayIcon.Icon = _baseTrayIcon;
                _wedgeTrayIcon = null;
            }
            else
            {
                // 모노톤(키 null)은 라이트/다크 태스크바 양쪽에서 읽히는 중간 회색
                var color = _pomodoroWedgeColorKey is null
                    ? System.Drawing.Color.FromArgb(0x9A, 0xA0, 0xA6)
                    : ToDrawingColor(NotionColorPalette.Wedge(_pomodoroWedgeColorKey));
                _wedgeTrayIcon = TrayWedgeIcon.Render(_pomodoro.WedgeFraction, color);
                _trayIcon.Icon = _wedgeTrayIcon;
            }
        }
        catch (ObjectDisposedException) { return; }
        old?.Dispose();
    }

    private static System.Drawing.Color ToDrawingColor(System.Windows.Media.Color c) =>
        System.Drawing.Color.FromArgb(c.R, c.G, c.B);

    private void UpdateTrayTooltip()
    {
        if (IsShuttingDown || _trayIcon is null) return;
        var text = _pomodoro!.TrayTooltip;
        if (text == _lastTrayTooltip) return;          // 같은 값이면 재대입 생략
        _lastTrayTooltip = text;
        try { _trayIcon.Text = text; }
        catch (ObjectDisposedException) { }
    }

    // 트레이 풍선 공용 — 종료 중 dispose된 NotifyIcon 접근을 한 곳에서 방어
    private void ShowBalloon(int timeoutMs, string title, string text, ToolTipIcon icon)
    {
        try { _trayIcon?.ShowBalloonTip(timeoutMs, title, text, icon); }
        catch (ObjectDisposedException) { }
    }
}
