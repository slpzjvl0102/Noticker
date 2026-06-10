using System.Diagnostics;
using System.Windows;
using Brushes = System.Windows.Media.Brushes;
using Noticker.Data;
using Noticker.Models;
using Noticker.Sync;

namespace Noticker.Windows;

// 첫 실행 / [연결 다시 설정] 2단계 위저드 — 토큰/DB/카테고리 설정의 단일 소유자.
// SettingsWindow는 더 이상 이 값들을 직접 편집하지 않는다.
public partial class OnboardingWindow : Window
{
    private const string NoCategoryLabel = "(카테고리 없음)";

    private readonly SettingsRepository _settings;
    private readonly NotionClient _client;
    private string _validatedToken = "";
    private List<(string Id, string Title)> _databases = [];
    private List<string> _selectProperties = [];

    public OnboardingWindow(SettingsRepository settings, NotionClient client)
    {
        _settings = settings;
        _client = client;
        InitializeComponent();

        // 재설정 진입이면 기존 토큰 미리 채움
        if (AppSettings.Instance.NotionToken is not null)
            TokenBox.Password = AppSettings.Instance.NotionToken;
    }

    private void TokenLink_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("https://www.notion.so/my-integrations")
        {
            UseShellExecute = true
        });

    private async void NextButton_Click(object sender, RoutedEventArgs e)
    {
        var token = TokenBox.Password.Trim();
        if (token.Length == 0)
        {
            SetStatus(Step1Status, "토큰을 입력하세요.", error: true);
            return;
        }

        NextButton.IsEnabled = false;
        SetStatus(Step1Status, "확인 중…");

        var error = await _client.ValidateTokenAsync(token, default);
        NextButton.IsEnabled = true;

        if (error is not null)
        {
            SetStatus(Step1Status, $"실패: {error}", error: true);
            return;
        }

        SetStatus(Step1Status, "");
        _validatedToken = token;
        Step1Panel.Visibility = Visibility.Collapsed;
        Step2Panel.Visibility = Visibility.Visible;
        NextButton.Visibility = Visibility.Collapsed;
        BackButton.Visibility = Visibility.Visible;
        FinishButton.Visibility = Visibility.Visible;
        await LoadDatabasesAsync();
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        Step2Panel.Visibility = Visibility.Collapsed;
        Step1Panel.Visibility = Visibility.Visible;
        BackButton.Visibility = Visibility.Collapsed;
        FinishButton.Visibility = Visibility.Collapsed;
        NextButton.Visibility = Visibility.Visible;
    }

    private async void RefreshDbButton_Click(object sender, RoutedEventArgs e) =>
        await LoadDatabasesAsync();

    private async Task LoadDatabasesAsync()
    {
        DbCombo.IsEnabled = false;
        CatCombo.IsEnabled = false;
        FinishButton.IsEnabled = false;
        RefreshDbButton.Visibility = Visibility.Collapsed;
        SetStatus(Step2Status, "DB 목록 불러오는 중…");

        try
        {
            _databases = await _client.SearchDatabasesAsync(_validatedToken, default);
        }
        catch (Exception ex)
        {
            SetStatus(Step2Status, $"실패: {ex.Message}", error: true);
            RefreshDbButton.Visibility = Visibility.Visible;
            return;
        }

        if (_databases.Count == 0)
        {
            SetStatus(Step2Status,
                "통합에 공유된 DB가 없습니다. 노션에서 DB 페이지의 ⋯ 메뉴 → 연결에 " +
                "이 통합을 추가한 뒤 새로고침하세요.", error: true);
            RefreshDbButton.Visibility = Visibility.Visible;
            return;
        }

        DbCombo.ItemsSource = _databases.Select(d => d.Title).ToList();
        DbCombo.IsEnabled = true;

        // 재설정 진입이면 현재 DB 미리 선택 (저장 형식은 32hex 무대시 — 비교 전 정규화)
        var currentId = AppSettings.Instance.TargetDbId;
        var idx = currentId is null ? -1
            : _databases.FindIndex(d => d.Id.Replace("-", "") == currentId);
        DbCombo.SelectedIndex = idx >= 0 ? idx : 0;   // SelectionChanged가 속성 로드를 이어받음
    }

    private async void DbCombo_SelectionChanged(
        object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (DbCombo.SelectedIndex < 0) return;

        CatCombo.IsEnabled = false;
        FinishButton.IsEnabled = false;
        SetStatus(Step2Status, "속성 확인 중…");

        var dbId = _databases[DbCombo.SelectedIndex].Id;
        try
        {
            _selectProperties = await _client.GetSelectPropertiesAsync(_validatedToken, dbId, default);
        }
        catch (Exception ex)
        {
            SetStatus(Step2Status, $"실패: {ex.Message}", error: true);
            return;
        }

        var items = new List<string>(_selectProperties) { NoCategoryLabel };
        CatCombo.ItemsSource = items;
        CatCombo.SelectedItem = _selectProperties.Contains("Category") ? "Category" : items[0];
        CatCombo.IsEnabled = true;
        FinishButton.IsEnabled = true;
        SetStatus(Step2Status, "");
    }

    private async void FinishButton_Click(object sender, RoutedEventArgs e)
    {
        if (DbCombo.SelectedIndex < 0) return;
        FinishButton.IsEnabled = false;

        var app = AppSettings.Instance;
        var (dbId, dbTitle) = _databases[DbCombo.SelectedIndex];
        var category = CatCombo.SelectedItem as string;
        var hasCategory = category is not null && category != NoCategoryLabel;

        app.NotionToken = _validatedToken;
        app.TargetDbId = dbId.Replace("-", "");   // 기존 저장 형식(32hex 무대시)과 통일
        app.NotionDbTitle = dbTitle;
        if (hasCategory)
            app.CategoryPropertyName = category!;
        // 카테고리 없음이면 CategoryPropertyName 기본값("Category") 유지 — 스펙 합의:
        // 속성이 없을 때 옵션이 비는 현행 동작과 동일

        _settings.SaveToken(_validatedToken);
        _settings.Set("target_db_id", app.TargetDbId);
        _settings.Set("category_property_name", app.CategoryPropertyName);
        _settings.Set("notion_db_title", dbTitle);

        // 토큰이 다른 integration일 수 있음 — 옛 bot id가 남으면 모든 push가 충돌 처리됨
        App.Current.InvalidateBotUserId();
        app.IsSyncPaused = false;

        if (hasCategory)
        {
            try { await App.Current.RefreshCategoryOptionsAsync(); }
            catch { /* 옵션 갱신 실패는 설정 창 [옵션 새로고침]으로 복구 가능 — 온보딩은 막지 않는다 */ }
        }

        Close();
    }

    private static void SetStatus(System.Windows.Controls.TextBlock target, string text, bool error = false)
    {
        target.Text = text;
        target.Foreground = error ? Brushes.Red : Brushes.Gray;
    }
}
