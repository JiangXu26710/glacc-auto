using CommunityToolkit.Mvvm.ComponentModel;
using GlaccAuto.Core;

namespace GlaccAuto.Gui.ViewModels;

public partial class SettingsViewModel : ViewModelBase
{
    private readonly AppSettings _settings;

    public SettingsViewModel(AppSettings settings)
    {
        _settings = settings;
        _scheduledEnabled = settings.ScheduledEnabled;
        _serverKey = settings.ServerKey;

        // 启动时校验持久化值（ctor 直接给字段赋值不经过 setter，需在此显式纠正）
        var min = Math.Clamp(settings.IntervalMinSec, 1, IntervalUpperBound);
        var max = Math.Clamp(settings.IntervalMaxSec, 1, IntervalUpperBound);
        if (min > max) min = max;
        if (max < min) max = min;
        if (settings.IntervalMinSec != min || settings.IntervalMaxSec != max)
        {
            settings.IntervalMinSec = min;
            settings.IntervalMaxSec = max;
            settings.Save();
        }
        _intervalMin = min.ToString();
        _intervalMax = max.ToString();

        _themeIndex = settings.Theme switch
        {
            "light" => 1,
            "dark" => 2,
            _ => 0,
        };
        _scaleIndex = Math.Clamp((settings.ScalePercent - 75) / 25, 0, 7);
        if (TimeSpan.TryParse(settings.ScheduledTime, out var t))
        {
            _scheduledTime = t;
        }
    }

    private const int IntervalUpperBound = 3600;

    [ObservableProperty]
    private bool _scheduledEnabled;

    /// <summary>"定时领取"子行展开状态（点击行头切换，不持久化）</summary>
    [ObservableProperty]
    private bool _scheduleExpanded;

    // 缩放事件保护：还原时静默设置，避免再次触发变更事件
    private bool _suppressScaleEvents;

    [ObservableProperty]
    private int _scaleIndex;

    /// <summary>缩放百分比：75 + 25 × 索引（75%~250%），基准 125% = 当前 1.3x 设计</summary>
    public int ScalePercent => 75 + 25 * ScaleIndex;

    /// <summary>用户确认保留后无需再通知；MainWindow 订阅此事件应用缩放并弹出保护确认</summary>
    public event Action<int>? ScaleChangeRequested;

    partial void OnScaleIndexChanged(int value)
    {
        if (_suppressScaleEvents) return;
        _settings.ScalePercent = ScalePercent;
        _settings.Save();
        ScaleChangeRequested?.Invoke(ScalePercent);
    }

    /// <summary>保护倒计时到期/用户点"恢复"后，静默回退 ComboBox 选择与持久化值。</summary>
    public void RevertScaleSilently(int percent)
    {
        _suppressScaleEvents = true;
        ScaleIndex = Math.Clamp((percent - 75) / 25, 0, 7);
        _suppressScaleEvents = false;
        _settings.ScalePercent = percent;
        _settings.Save();
    }

    [ObservableProperty]
    private TimeSpan? _scheduledTime = new TimeSpan(8, 0, 0);

    [ObservableProperty]
    private string _serverKey = "";

    [ObservableProperty]
    private string _intervalMin = "30";

    [ObservableProperty]
    private string _intervalMax = "40";

    [ObservableProperty]
    private int _themeIndex;

    partial void OnScheduledEnabledChanged(bool value)
    {
        _settings.ScheduledEnabled = value;
        _settings.Save();
    }

    partial void OnScheduledTimeChanged(TimeSpan? value)
    {
        _settings.ScheduledTime = value?.ToString(@"hh\:mm") ?? "08:00";
        _settings.Save();
    }

    partial void OnServerKeyChanged(string value)
    {
        _settings.ServerKey = value;
        _settings.Save();
    }

    // 领取间隔校验：必须为数字、范围 1~3600 秒、且 min ≤ max；
    // 非法输入回退为上一个有效值（TextBox 文本随之回写）
    partial void OnIntervalMinChanged(string value)
    {
        var coerced = CoerceInterval(value, _settings.IntervalMinSec, _settings.IntervalMaxSec, isMin: true);
        _settings.IntervalMinSec = int.Parse(coerced);
        _settings.Save();
        if (coerced != value)
        {
            _intervalMin = coerced;
            OnPropertyChanged(nameof(IntervalMin));
        }
    }

    partial void OnIntervalMaxChanged(string value)
    {
        var coerced = CoerceInterval(value, _settings.IntervalMaxSec, _settings.IntervalMinSec, isMin: false);
        _settings.IntervalMaxSec = int.Parse(coerced);
        _settings.Save();
        if (coerced != value)
        {
            _intervalMax = coerced;
            OnPropertyChanged(nameof(IntervalMax));
        }
    }

    private string CoerceInterval(string? input, int current, int other, bool isMin)
    {
        if (!int.TryParse(input, out var n)) return current.ToString();
        n = Math.Clamp(n, 1, IntervalUpperBound);
        // 保持 min ≤ max：越界一侧被夹到另一侧
        if (isMin && n > other) n = other;
        if (!isMin && n < other) n = other;
        return n.ToString();
    }

    /// <summary>失焦时强制复核（供视图层 LostFocus 调用），非法文本回写为有效值。</summary>
    public void ValidateIntervals()
    {
        IntervalMin = CoerceInterval(IntervalMin, _settings.IntervalMinSec, _settings.IntervalMaxSec, isMin: true);
        IntervalMax = CoerceInterval(IntervalMax, _settings.IntervalMaxSec, _settings.IntervalMinSec, isMin: false);
    }

    partial void OnThemeIndexChanged(int value)
    {
        _settings.Theme = value switch
        {
            1 => "light",
            2 => "dark",
            _ => "system",
        };
        _settings.Save();
        App.ApplyTheme(_settings.Theme);
    }
}
