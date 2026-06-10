using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using FlowDirection = System.Windows.FlowDirection;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using UserControl = System.Windows.Controls.UserControl;
using Noticker.Infrastructure;

namespace Noticker.Windows.Controls;

// Time Timer 다이얼 — 정적 면(링+눈금+숫자)은 캐시, per-tick에는 웨지 geometry 스왑만.
// 서비스를 모르는 순수 뷰: 입력은 이벤트로만 노출, 클램프/영속/플레이트 텍스트는 창 소관.
public partial class TimerDial : UserControl
{
    private const double Cx = 72.0;
    private const double Cy = 72.0;
    private const double WedgeR = 62.0;                // 눈금 링(r62~69) 안쪽
    private const double KnobR = 60.0;

    private static readonly Brush _ringBrush = FrozenGray(0x30);
    private static readonly Brush _outlineFill = FrozenGray(0x14);

    private double _lastAngle = double.NaN;            // 마지막으로 빌드한 웨지 각도
    private bool _isDragging;
    private int? _lastCommitted;                       // 현재 캡처 내 마지막 커밋 분 (null = 첫 샘플)

    // 창이 구독: 드래그/클릭 커밋(분), 드래그 종료(영속 시점), 휠 ±1 (클램프는 서비스 소관)
    public event EventHandler<int>? SetMinutesRequested;
    public event EventHandler? DragCompleted;
    public event EventHandler<int>? WheelDeltaRequested;

    public TimerDial()
    {
        InitializeComponent();
        RenderFace();
        UpdateWedgeAppearance();
        UpdateWedge();
    }

    // ── Dependency Properties ──────────────────────────────────────────────────

    public static readonly DependencyProperty FractionProperty =
        DependencyProperty.Register(nameof(Fraction), typeof(double), typeof(TimerDial),
            new PropertyMetadata(0.0, OnFractionChanged, CoerceFraction));

    public static readonly DependencyProperty WedgeBrushProperty =
        DependencyProperty.Register(nameof(WedgeBrush), typeof(Brush), typeof(TimerDial),
            new PropertyMetadata(null, OnWedgeAppearanceChanged));

    public static readonly DependencyProperty FaceBrushProperty =
        DependencyProperty.Register(nameof(FaceBrush), typeof(Brush), typeof(TimerDial),
            new PropertyMetadata(null, OnFaceChanged));

    public static readonly DependencyProperty TickBrushProperty =
        DependencyProperty.Register(nameof(TickBrush), typeof(Brush), typeof(TimerDial),
            new PropertyMetadata(null, OnFaceChanged));

    public static readonly DependencyProperty WedgeStrokeProperty =
        DependencyProperty.Register(nameof(WedgeStroke), typeof(Brush), typeof(TimerDial),
            new PropertyMetadata(null, OnWedgeAppearanceChanged));

    public static readonly DependencyProperty IsOutlineOnlyProperty =
        DependencyProperty.Register(nameof(IsOutlineOnly), typeof(bool), typeof(TimerDial),
            new PropertyMetadata(false, OnWedgeAppearanceChanged));

    public static readonly DependencyProperty IsSettableProperty =
        DependencyProperty.Register(nameof(IsSettable), typeof(bool), typeof(TimerDial),
            new PropertyMetadata(false, OnIsSettableChanged));

    public static readonly DependencyProperty WedgeOpacityProperty =
        DependencyProperty.Register(nameof(WedgeOpacity), typeof(double), typeof(TimerDial),
            new PropertyMetadata(1.0));

    public double Fraction
    {
        get => (double)GetValue(FractionProperty);
        set => SetValue(FractionProperty, value);
    }

    public Brush? WedgeBrush
    {
        get => (Brush?)GetValue(WedgeBrushProperty);
        set => SetValue(WedgeBrushProperty, value);
    }

    public Brush? FaceBrush
    {
        get => (Brush?)GetValue(FaceBrushProperty);
        set => SetValue(FaceBrushProperty, value);
    }

    public Brush? TickBrush
    {
        get => (Brush?)GetValue(TickBrushProperty);
        set => SetValue(TickBrushProperty, value);
    }

    public Brush? WedgeStroke
    {
        get => (Brush?)GetValue(WedgeStrokeProperty);
        set => SetValue(WedgeStrokeProperty, value);
    }

    public bool IsOutlineOnly
    {
        get => (bool)GetValue(IsOutlineOnlyProperty);
        set => SetValue(IsOutlineOnlyProperty, value);
    }

    public bool IsSettable
    {
        get => (bool)GetValue(IsSettableProperty);
        set => SetValue(IsSettableProperty, value);
    }

    public double WedgeOpacity
    {
        get => (double)GetValue(WedgeOpacityProperty);
        set => SetValue(WedgeOpacityProperty, value);
    }

    private static object CoerceFraction(DependencyObject d, object baseValue)
    {
        double v = (double)baseValue;
        if (double.IsNaN(v)) return 0.0;
        return Math.Clamp(v, 0.0, 1.0);
    }

    private static void OnFractionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var dial = (TimerDial)d;
        dial.UpdateWedge();
        dial.UpdateKnob();
    }

    private static void OnWedgeAppearanceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((TimerDial)d).UpdateWedgeAppearance();

    private static void OnFaceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((TimerDial)d).RenderFace();

    private static void OnIsSettableChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((TimerDial)d).ApplySettable();

    // ── 웨지 ───────────────────────────────────────────────────────────────────

    private void UpdateWedge()
    {
        double angle = Fraction * 360.0;               // Coerce 후라 0~360 보장
        if (angle == _lastAngle) return;

        // 1Hz tick 절약: 표시 각도 변화 0.5° 미만이면 재생성 생략 —
        // 단, 드래그 프리뷰(IsSettable)는 즉시, 0/360 경계는 특수 geometry라 항상 빌드
        bool boundary = angle <= 0.0 || angle >= 360.0;
        if (!IsSettable && !boundary && !double.IsNaN(_lastAngle) &&
            Math.Abs(angle - _lastAngle) < 0.5)
            return;
        _lastAngle = angle;

        if (angle <= 0.0)
        {
            WedgePath.Data = Geometry.Empty;
        }
        else if (angle >= 360.0)
        {
            // 시작점=끝점 ArcTo 퇴화 회피 — 꽉 찬 원
            var full = new EllipseGeometry(new Point(Cx, Cy), WedgeR, WedgeR);
            full.Freeze();
            WedgePath.Data = full;
        }
        else
        {
            // 12시 기준 반시계: 끝점 x = cx − r·sin(θ), y = cy − r·cos(θ)
            double rad = angle * Math.PI / 180.0;
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                ctx.BeginFigure(new Point(Cx, Cy), isFilled: true, isClosed: true);
                ctx.LineTo(new Point(Cx, Cy - WedgeR), true, false);
                ctx.ArcTo(new Point(Cx - WedgeR * Math.Sin(rad), Cy - WedgeR * Math.Cos(rad)),
                          new Size(WedgeR, WedgeR), 0, isLargeArc: angle > 180.0,
                          SweepDirection.Counterclockwise, true, false);
            }
            geo.Freeze();
            WedgePath.Data = geo;
        }
    }

    private void UpdateWedgeAppearance()
    {
        if (IsOutlineOnly)
        {
            // 휴식 웨지: 순수 아웃라인은 눈금 위에서 면으로 안 읽힘 — 약한 fill 동반
            WedgePath.Fill = _outlineFill;
            WedgePath.Stroke = WedgeStroke;
            WedgePath.StrokeThickness = 1.5;
        }
        else
        {
            // 채운 웨지는 플랫 컬러 — 외곽선 없음
            WedgePath.Fill = WedgeBrush;
            WedgePath.Stroke = null;
        }
    }

    // ── 노브 ───────────────────────────────────────────────────────────────────

    private void UpdateKnob()
    {
        if (!IsSettable) return;                       // Collapsed 상태 — 갱신 불필요
        double rad = Fraction * 360.0 * Math.PI / 180.0;
        double r = _isDragging ? 5.0 : 4.0;            // 드래그 중 10px로 확대
        var g = new EllipseGeometry(
            new Point(Cx - KnobR * Math.Sin(rad), Cy - KnobR * Math.Cos(rad)), r, r);
        g.Freeze();
        Knob.Data = g;
    }

    private void ApplySettable()
    {
        bool settable = IsSettable;
        HitArea.IsHitTestVisible = settable;           // Cursor/ToolTip은 히트 시에만 적용됨
        Knob.Visibility = settable ? Visibility.Visible : Visibility.Collapsed;
        if (settable)
        {
            UpdateKnob();
        }
        else if (_isDragging)
        {
            // 드래그 중 설정 불가 전환(Idle 이탈) — 캡처 해제로 드래그 취소
            HitArea.ReleaseMouseCapture();
        }
    }

    // ── 입력 (IsSettable일 때만 — HitArea.IsHitTestVisible로 게이트) ───────────

    private void HitArea_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!IsSettable) return;
        var pos = e.GetPosition(this);
        double? raw = DialMath.MinutesFromPoint(pos.X - Cx, pos.Y - Cy);
        if (raw == null) return;                       // 데드존(중심 r<27) — 드래그 미시작
        _isDragging = true;
        _lastCommitted = null;
        HitArea.CaptureMouse();
        Commit(DialMath.SnapTo5(raw.Value));
        UpdateKnob();
        e.Handled = true;
    }

    private void HitArea_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDragging) return;
        var pos = e.GetPosition(this);
        double? raw = DialMath.MinutesFromPoint(pos.X - Cx, pos.Y - Cy);
        if (raw == null) return;
        Commit(DialMath.SnapTo5(raw.Value));
    }

    private void HitArea_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDragging) return;
        HitArea.ReleaseMouseCapture();                 // LostMouseCapture에서 정리 + DragCompleted
        e.Handled = true;
    }

    private void HitArea_LostMouseCapture(object sender, MouseEventArgs e)
    {
        // 정상 종료(ButtonUp)와 강탈(Alt-Tab/풍선) 공통 경로 — 드래그 상태 누수 방지
        if (!_isDragging) return;
        _isDragging = false;
        _lastCommitted = null;
        UpdateKnob();
        DragCompleted?.Invoke(this, EventArgs.Empty);
    }

    private void HitArea_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!IsSettable) return;
        WheelDeltaRequested?.Invoke(this, e.Delta > 0 ? 1 : -1);
        e.Handled = true;
    }

    private void Commit(int snapped)
    {
        // 첫 샘플은 그대로, 이후엔 0/360 경계 핀 — 12시 부근 60↔5 플리커 방지
        int committed = _lastCommitted is int prev
            ? DialMath.ApplyDragSample(prev, snapped)
            : snapped;
        if (committed != _lastCommitted)
        {
            _lastCommitted = committed;
            SetMinutesRequested?.Invoke(this, committed);
        }
    }

    // ── 정적 면 (테마 변경 시에만 재생성 — per-tick 재생성 금지) ────────────────

    private void RenderFace()
    {
        var group = new DrawingGroup();
        using (var dc = group.Open())
        {
            // 투명 사각형으로 144×144 경계 고정 — DrawingImage가 내용 bounds로 줄어드는 것 방지
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, 144, 144));

            var ringPen = new Pen(_ringBrush, 1.0);
            ringPen.Freeze();
            dc.DrawEllipse(null, ringPen, new Point(Cx, Cy), 71, 71);

            if (TickBrush is Brush tick)
            {
                var majorPen = new Pen(WithOpacity(tick, 0.55), 1.5);
                majorPen.Freeze();
                var minorPen = new Pen(WithOpacity(tick, 0.25), 1.0);
                minorPen.Freeze();
                for (int i = 0; i < 60; i++)
                {
                    double rad = i * 6.0 * Math.PI / 180.0;
                    double sin = Math.Sin(rad), cos = Math.Cos(rad);
                    bool major = i % 5 == 0;
                    double rIn = major ? 62.0 : 66.0;
                    dc.DrawLine(major ? majorPen : minorPen,
                        new Point(Cx - 69.0 * sin, Cy - 69.0 * cos),
                        new Point(Cx - rIn * sin, Cy - rIn * cos));
                }

                // 숫자 12개 — 웨지와 같은 반시계 배치, 텍스트 자체는 수직 유지 (회전 금지)
                var textBrush = WithOpacity(tick, 0.55);
                var typeface = new Typeface(FontFamily, FontStyles.Normal,
                    FontWeights.Normal, FontStretches.Normal);
                double ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;
                for (int i = 0; i < 12; i++)
                {
                    double rad = i * 30.0 * Math.PI / 180.0;
                    double x = Cx - 54.0 * Math.Sin(rad);
                    double y = Cy - 54.0 * Math.Cos(rad);
                    var ft = new FormattedText((i * 5).ToString(),
                        CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                        typeface, 9.0, textBrush, ppd);
                    dc.DrawText(ft, new Point(x - ft.Width / 2.0, y - ft.Height / 2.0));
                }
            }
        }
        group.Freeze();
        FaceImage.Source = new DrawingImage(group);
    }

    private static Brush WithOpacity(Brush brush, double opacity)
    {
        var clone = brush.Clone();
        clone.Opacity = opacity;
        clone.Freeze();
        return clone;
    }

    private static Brush FrozenGray(byte alpha)
    {
        var b = new SolidColorBrush(Color.FromArgb(alpha, 0x80, 0x80, 0x80));
        b.Freeze();
        return b;
    }
}
