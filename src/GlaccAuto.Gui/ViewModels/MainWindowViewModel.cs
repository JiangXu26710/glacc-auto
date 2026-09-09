using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GlaccAuto.Core;

namespace GlaccAuto.Gui.ViewModels;

/// <summary>
/// 主窗口视图模型。当前为纯 UI 演示：所有数据为假数据，
/// “开始领取”仅模拟进度推进，不发起任何网络请求。
/// </summary>
public partial class MainWindowViewModel : ViewModelBase
{
    // 任务52 四阶段：5 + 5 + 10 + 3 = 23 次
    private const int TotalClaims = 23;
    private static readonly int[] StageBounds = { 5, 10, 20, 23 };
    private static readonly string[] StageNames = { "阶段一", "阶段二", "阶段三", "阶段四" };
    // 演示收益：按各阶段总时长折算到每次领取（分钟）
    private static readonly double[] RewardPerClaim = { 16, 20, 13.333, 53.333 };

    private const double InitialBalanceMinutes = 23232; // 演示数据：387 时 12 分
    private const int InitialClaimIndex = 7;            // 演示数据：今日已完成 7 次

    private const string DemoPhone = "12345678901";     // 演示占位手机号（不写真实号码）
    private const string DemoUserId = "123456";         // 演示占位用户 ID

    /// <summary>手机号脱敏：保留后 4 位（11 位 → *******8901）。</summary>
    private static string MaskPhone(string phone) =>
        phone.Length <= 4 ? phone : new string('*', phone.Length - 4) + phone[^4..];

    /// <summary>ID 脱敏：ID 更短，保留后 2 位（6 位 → ****56）。</summary>
    private static string MaskId(string id) =>
        id.Length <= 2 ? id : new string('*', id.Length - 2) + id[^2..];

    private DispatcherTimer? _timer;
    private DispatcherTimer? _resendTimer;

    public MainWindowViewModel(AppSettings settings)
    {
        Settings = new SettingsViewModel(settings);
        Settings.ScaleChangeRequested += p => ScaleChangeRequested?.Invoke(p);
    }

    public SettingsViewModel Settings { get; }

    public bool ExitConfirmed { get; set; }
    public event Action? CloseRequested;

    /// <summary>设置页缩放变更 → MainWindow 应用缩放并弹出保护确认</summary>
    public event Action<int>? ScaleChangeRequested;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunning))]
    [NotifyPropertyChangedFor(nameof(ButtonText))]
    [NotifyPropertyChangedFor(nameof(ButtonEnabled))]
    [NotifyPropertyChangedFor(nameof(SpinnerVisible))]
    [NotifyPropertyChangedFor(nameof(CanReset))]
    [NotifyPropertyChangedFor(nameof(StageText))]
    private RunState _state = RunState.Idle;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BalanceHoursText))]
    [NotifyPropertyChangedFor(nameof(BalanceMinutesText))]
    private double _balanceMinutes = InitialBalanceMinutes;

    // 版式化余额：数字与单位分开排版，增强设计感；分钟两位补零（9 → 09）。
    // 未登录时不展示演示数据，显示占位符 "--"（如 "--时--分"）。
    public int BalanceHours
    {
        get { var t = (int)Math.Round(BalanceMinutes); return t / 60; }
    }

    public string BalanceHoursText => IsLoggedIn ? BalanceHours.ToString() : "--";

    public string BalanceMinutesText => IsLoggedIn
        ? ((int)Math.Round(BalanceMinutes) % 60).ToString("D2")
        : "--";

    // 登录态（演示）：启动即未登录，走登录引导后进入演示数据
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccountName))]
    [NotifyPropertyChangedFor(nameof(BalanceHoursText))]
    [NotifyPropertyChangedFor(nameof(BalanceMinutesText))]
    [NotifyPropertyChangedFor(nameof(ProgressCurrentText))]
    [NotifyPropertyChangedFor(nameof(ProgressTotalText))]
    [NotifyPropertyChangedFor(nameof(StageText))]
    [NotifyPropertyChangedFor(nameof(ButtonEnabled))]
    [NotifyPropertyChangedFor(nameof(ButtonText))]
    private bool _isLoggedIn;

    // 手机号/用户 ID 显示/隐藏 原文（点击账号行切换）
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccountName))]
    [NotifyPropertyChangedFor(nameof(UserIdText))]
    private bool _phoneRevealed;

    /// <summary>主标题：手机号（默认脱敏，点击显示原文）</summary>
    public string AccountName => PhoneRevealed ? DemoPhone : MaskPhone(DemoPhone);

    /// <summary>副标题：用户 ID（随手机号一起切换显隐）</summary>
    public string UserIdText => PhoneRevealed ? DemoUserId : MaskId(DemoUserId);

    // 多账号：以当前账号为视觉焦点；仅一个账号时不出现任何“多账号”线索
    public System.Collections.ObjectModel.ObservableCollection<string> Accounts { get; } =
        new() { MaskPhone(DemoPhone) };

    public bool HasMultipleAccounts => Accounts.Count > 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccountName))]
    private int _selectedAccountIndex;

    [RelayCommand]
    private void SelectAccount(string name)
    {
        var i = Accounts.IndexOf(name);
        if (i >= 0) SelectedAccountIndex = i;
    }

    /// <summary>添加账号（演示占位：真实业务接入登录流程后生效）。</summary>
    [RelayCommand]
    private void AddAccount()
    {
        OpenLogin();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText))]
    [NotifyPropertyChangedFor(nameof(ProgressCurrent))]
    [NotifyPropertyChangedFor(nameof(ProgressTotal))]
    [NotifyPropertyChangedFor(nameof(ProgressCurrentText))]
    [NotifyPropertyChangedFor(nameof(ProgressTotalText))]
    [NotifyPropertyChangedFor(nameof(StageText))]
    private int _claimIndex = InitialClaimIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TitleText))]
    [NotifyPropertyChangedFor(nameof(CurrentPage))]
    private bool _isSettingsOpen;

    /// <summary>供 ContentControl + DataTemplate 的页面切换（配入场动画）。</summary>
    public object CurrentPage => IsSettingsOpen ? (object)Settings : this;

    [ObservableProperty]
    private bool _showExitConfirm;

    public string TitleText => IsSettingsOpen ? "设置" : "glacc-auto";

    public bool IsRunning => State == RunState.Running;
    public bool ButtonEnabled => State == RunState.Idle && IsLoggedIn;
    public bool SpinnerVisible => State == RunState.Running;
    public bool CanReset => State == RunState.Done;

    public string ButtonText => !IsLoggedIn
        ? "登录账号"
        : State switch
        {
            RunState.Running => "正在领取",
            RunState.Done => "今日已完成",
            _ => "开始领取",
        };

    public string ProgressText => $"{ClaimIndex} / {TotalClaims}";

    /// <summary>进度计数拆分（当前/总数），供"/"分隔符独立排版对齐；未登录显示占位符 "-"</summary>
    public int ProgressCurrent => ClaimIndex;
    public int ProgressTotal => TotalClaims;
    public string ProgressCurrentText => IsLoggedIn ? ClaimIndex.ToString() : "-";
    public string ProgressTotalText => IsLoggedIn ? TotalClaims.ToString() : "-";

    public IReadOnlyList<int> SegmentSizes => new[] { 5, 5, 10, 3 };

    public string StageText
    {
        get
        {
            if (!IsLoggedIn) return "登录后同步今日任务";
            if (ClaimIndex >= TotalClaims) return "今日任务已全部完成";
            for (var i = 0; i < StageBounds.Length; i++)
            {
                if (ClaimIndex < StageBounds[i])
                {
                    // 显示当前阶段剩余次数（而非总计剩余）
                    var remainingInStage = StageBounds[i] - ClaimIndex;
                    return $"{StageNames[i]} · 本阶段还剩 {remainingInStage} 次";
                }
            }
            return "";
        }
    }

    [RelayCommand]
    private void Start()
    {
        // 未登录：主按钮作为登录引导入口
        if (!IsLoggedIn)
        {
            OpenLogin();
            return;
        }
        if (State != RunState.Idle) return;
        State = RunState.Running;
        // 演示节奏：每 1.5 秒完成一次。真实业务将改用设置中的随机领取间隔（默认 30~40 秒）。
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1.5), DispatcherPriority.Normal, (_, _) => Tick());
        _timer.Start();
    }

    private void Tick()
    {
        if (State != RunState.Running) return;
        if (ClaimIndex >= TotalClaims)
        {
            StopTimer();
            State = RunState.Done;
            return;
        }
        BalanceMinutes += RewardPerClaim[StageOf(ClaimIndex + 1)];
        ClaimIndex++;
        if (ClaimIndex >= TotalClaims)
        {
            StopTimer();
            State = RunState.Done;
        }
    }

    private static int StageOf(int claimNo)
    {
        for (var i = 0; i < StageBounds.Length; i++)
        {
            if (claimNo <= StageBounds[i]) return i;
        }
        return StageBounds.Length - 1;
    }

    private void StopTimer()
    {
        _timer?.Stop();
        _timer = null;
    }

    /// <summary>演示辅助：完成后恢复初始演示数据。接入真实业务后移除。</summary>
    [RelayCommand]
    private void ResetDemo()
    {
        StopTimer();
        State = RunState.Idle;
        ClaimIndex = InitialClaimIndex;
        BalanceMinutes = InitialBalanceMinutes;
    }

    // ── 登录引导（演示模式：手机号 + 验证码两步，任意非空验证码即可登录，不发真实请求） ──

    [ObservableProperty]
    private bool _showLoginDialog;

    /// <summary>false = 第一步手机号；true = 第二步验证码</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SentToText))]
    private bool _loginCodeStep;

    [ObservableProperty]
    private string _phoneInput = DemoPhone;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCodeInput))]
    private string _codeInput = "";

    /// <summary>重发倒计时（秒），0 表示可发送</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ResendText))]
    private int _resendSeconds;

    public bool HasCodeInput => !string.IsNullOrWhiteSpace(CodeInput);
    public bool CanResend => ResendSeconds <= 0;
    public string ResendText => ResendSeconds > 0 ? $"{ResendSeconds} 秒后可重新发送" : "重新发送验证码";
    public string SentToText => $"验证码已发送至 {MaskPhone(PhoneInput)}，5 分钟内有效（演示模式：任意验证码均可登录）";

    [RelayCommand]
    private void OpenLogin()
    {
        StopResendTimer();
        LoginCodeStep = false;
        CodeInput = "";
        ResendSeconds = 0;
        ShowLoginDialog = true;
    }

    [RelayCommand]
    private void CancelLogin() => ShowLoginDialog = false;

    [RelayCommand]
    private void BackToPhone()
    {
        StopResendTimer();
        ResendSeconds = 0;
        LoginCodeStep = false;
    }

    [RelayCommand]
    private void SendCode()
    {
        if (!CanResend) return;
        LoginCodeStep = true;
        CodeInput = "";
        ResendSeconds = 60;
        _resendTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Normal,
            (_, _) =>
            {
                ResendSeconds--;
                if (ResendSeconds <= 0) StopResendTimer();
            });
        _resendTimer.Start();
    }

    [RelayCommand]
    private void ConfirmLogin()
    {
        if (!HasCodeInput) return;
        StopResendTimer();
        ShowLoginDialog = false;
        IsLoggedIn = true;
        PhoneRevealed = false;
        // 恢复演示初始数据，进入已登录演示态
        StopTimer();
        State = RunState.Idle;
        ClaimIndex = InitialClaimIndex;
        BalanceMinutes = InitialBalanceMinutes;
    }

    /// <summary>点击账号行：切换 显示/隐藏 完整手机号</summary>
    [RelayCommand]
    private void TogglePhoneReveal() => PhoneRevealed = !PhoneRevealed;

    private void StopResendTimer()
    {
        _resendTimer?.Stop();
        _resendTimer = null;
    }

    [RelayCommand]
    private void OpenHome() => IsSettingsOpen = false;

    [RelayCommand]
    private void OpenSettings() => IsSettingsOpen = true;

    [RelayCommand]
    private void ConfirmExit()
    {
        ExitConfirmed = true;
        CloseRequested?.Invoke();
    }

    [RelayCommand]
    private void CancelExit() => ShowExitConfirm = false;
}
