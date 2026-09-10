using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using GlaccAuto.Core;
using GlaccAuto.Core.Diagnostics;
using GlaccAuto.Core.Scheduling;

namespace GlaccAuto.Gui.ViewModels;

public partial class SettingsViewModel : ViewModelBase
{
    private readonly AppSettings _settings;

    public SettingsViewModel(AppSettings settings)
    {
        _settings = settings;
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

        var retry = Math.Clamp(settings.NetworkRetryCount, 0, NetworkRetryUpperBound);
        if (settings.NetworkRetryCount != retry)
        {
            settings.NetworkRetryCount = retry;
            settings.Save();
        }
        _networkRetryCount = retry;

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
    private const int NetworkRetryUpperBound = 10;

    [ObservableProperty]
    private bool _scheduledEnabled;

    /// <summary>"定时领取"子行展开状态（点击行头切换，不持久化）</summary>
    [ObservableProperty]
    private bool _scheduleExpanded;

    /// <summary>计划任务注册/注销失败提示（空 = 无错误）</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasScheduleError))]
    private string _scheduleError = "";

    public bool HasScheduleError => !string.IsNullOrEmpty(ScheduleError);

    /// <summary>注册/注销失败回滚开关、启动投影时静默，避免再次触发变更处理</summary>
    private bool _suppressScheduleEvents;

    /// <summary>用户在本会话是否已手动改过计划任务设置；改过之后启动投影不再覆盖其改动</summary>
    private bool _scheduleTouched;

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

    /// <summary>网络重试次数（0~10）：失败后的额外重试次数，0 = 失败立即中断；总尝试 = 1 + 此值</summary>
    [ObservableProperty]
    private int _networkRetryCount = 3;

    partial void OnNetworkRetryCountChanged(int value)
    {
        var v = Math.Clamp(value, 0, NetworkRetryUpperBound);
        if (v != value)
        {
            _networkRetryCount = v;
            OnPropertyChanged(nameof(NetworkRetryCount));
        }
        _settings.NetworkRetryCount = v;
        _settings.Save();
    }

    [ObservableProperty]
    private int _themeIndex;

    /// <summary>
    /// 把计划任务的真实状态投影到界面：开关与时间均取自任务本身（任务不存在时保持本地配置的时间）。
    /// 用户已手动改过计划任务设置时不覆盖，避免启动投影盖掉用户刚做的改动。
    /// </summary>
    /// <param name="skipIfUserTouched">true = 用户本会话已改过则跳过（供启动投影使用）。</param>
    public void ApplyScheduleProjection(ScheduledTaskManager.TaskSnapshot? snapshot, bool skipIfUserTouched = false)
    {
        if (skipIfUserTouched && _scheduleTouched) return;
        _suppressScheduleEvents = true;
        try
        {
            ScheduledEnabled = snapshot is { Enabled: true, TriggerEnabled: true };
            // 只取到分钟：任务触发时间含秒时不进入界面，与注册写回的精度保持一致
            if (snapshot is not null)
                ScheduledTime = new TimeSpan(snapshot.StartTime.Hours, snapshot.StartTime.Minutes, 0);
        }
        finally
        {
            _suppressScheduleEvents = false;
        }
    }

    partial void OnScheduledEnabledChanged(bool value)
    {
        if (_suppressScheduleEvents) return;
        _scheduleTouched = true;
        ScheduleError = "";
        if (value)
        {
            try
            {
                ScheduledTaskManager.Register(GetScheduledTime());
            }
            catch (Exception ex)
            {
                // 注册失败：按任务真实状态回滚开关，界面始终反映系统实况
                DiagLog.Error("定时任务注册失败", ex);
                ScheduleError = $"定时任务注册失败：{ex.Message}";
                RevertScheduledEnabled();
            }
        }
        else
        {
            try
            {
                ScheduledTaskManager.Unregister();
            }
            catch (Exception ex)
            {
                // 注销失败：开关回到"任务仍在"的真实状态，避免界面显示已关而任务照常触发
                DiagLog.Error("定时任务注销失败", ex);
                ScheduleError = $"定时任务注销失败：{ex.Message}";
                RevertScheduledEnabled();
            }
        }
    }

    /// <summary>按计划任务的真实状态回滚开关（查询失败时视为未启用）。</summary>
    private void RevertScheduledEnabled()
    {
        var enabled = ScheduledTaskManager.TryQuery(out var snapshot)
            && snapshot is { Enabled: true, TriggerEnabled: true };
        _suppressScheduleEvents = true;
        try
        {
            ScheduledEnabled = enabled;
        }
        finally
        {
            _suppressScheduleEvents = false;
        }
    }

    partial void OnScheduledTimeChanged(TimeSpan? value)
    {
        // 投影与回滚期间不产生副作用：任务真实时间不回写本地配置
        if (_suppressScheduleEvents) return;
        _scheduleTouched = true;
        _settings.ScheduledTime = value?.ToString(@"hh\:mm") ?? "08:00";
        _settings.Save();
        if (!ScheduledEnabled) return;
        try
        {
            ScheduledTaskManager.Register(GetScheduledTime());
            ScheduleError = "";
        }
        catch (Exception ex)
        {
            DiagLog.Error("定时任务更新失败", ex);
            ScheduleError = $"定时任务更新失败：{ex.Message}";
        }
    }

    /// <summary>当前界面显示的领取时刻；界面未给出时回退到本地配置值。</summary>
    private TimeSpan GetScheduledTime() =>
        ScheduledTime
        ?? (TimeSpan.TryParse(_settings.ScheduledTime, CultureInfo.InvariantCulture, out var t)
            ? t : new TimeSpan(8, 0, 0));

    partial void OnServerKeyChanged(string value)
    {
        _settings.ServerKey = value;
        _settings.Save();
    }

    // 领取间隔校验：必须为数字、范围 1~3600 秒、且 min ≤ max；
    // 非法输入夹紧为有效值后落盘，并同步 VM 字段
    partial void OnIntervalMinChanged(string value) => CoerceAndApply(value, isMin: true);

    partial void OnIntervalMaxChanged(string value) => CoerceAndApply(value, isMin: false);

    /// <summary>失焦校验入口：以输入框当前文本为输入做夹紧并落盘，返回合法字符串，
    /// 供视图直接写回控件（双向绑定在失焦写源过程中会抑制 VM 的回写，不能依赖绑定同步文本）。</summary>
    public string CoerceIntervalInput(string? text, bool isMin) => CoerceAndApply(text, isMin);

    private string CoerceAndApply(string? input, bool isMin)
    {
        var coerced = isMin
            ? CoerceInterval(input, _settings.IntervalMinSec, _settings.IntervalMaxSec, isMin: true)
            : CoerceInterval(input, _settings.IntervalMaxSec, _settings.IntervalMinSec, isMin: false);
        var n = int.Parse(coerced);
        if (isMin && _settings.IntervalMinSec != n)
        {
            _settings.IntervalMinSec = n;
            _settings.Save();
        }
        else if (!isMin && _settings.IntervalMaxSec != n)
        {
            _settings.IntervalMaxSec = n;
            _settings.Save();
        }
        if (isMin && _intervalMin != coerced)
        {
            _intervalMin = coerced;
            OnPropertyChanged(nameof(IntervalMin));
        }
        else if (!isMin && _intervalMax != coerced)
        {
            _intervalMax = coerced;
            OnPropertyChanged(nameof(IntervalMax));
        }
        return coerced;
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
