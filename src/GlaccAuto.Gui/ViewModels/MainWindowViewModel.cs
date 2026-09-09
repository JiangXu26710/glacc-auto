using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GlaccAuto.Core;
using GlaccAuto.Core.Glacc;

namespace GlaccAuto.Gui.ViewModels;

/// <summary>
/// 主窗口视图模型：接入真实业务。
/// 登录态（refresh 保活 / 短信登录）、任务进度与钱包（mobileGLTaskList / get_user_wallet）、
/// 主任务52 各阶段直推（mobileGLTaskPush，MD5 签名）均走 GlaccAuto.Core.Glacc 协议层。
/// </summary>
public partial class MainWindowViewModel : ViewModelBase
{
    private readonly AppSettings _settings;
    private readonly GlaccCredentials _cred;
    private readonly GlaccAuthClient _auth;
    private readonly GlaccGameClient _game;

    /// <summary>当前任务阶段（服务端每日可变，从 mobileGLTaskList 动态读取）</summary>
    private List<GlaccTaskStage> _stages = [];

    public MainWindowViewModel(AppSettings settings)
    {
        _settings = settings;
        Settings = new SettingsViewModel(settings);
        Settings.ScaleChangeRequested += p => ScaleChangeRequested?.Invoke(p);

        _cred = GlaccCredentials.Load();
        _auth = new GlaccAuthClient(_cred);
        _game = new GlaccGameClient(_cred);
        _ = InitializeAsync();
    }

    public SettingsViewModel Settings { get; }

    public bool ExitConfirmed { get; set; }
    public event Action? CloseRequested;

    /// <summary>设置页缩放变更 → MainWindow 应用缩放并弹出保护确认</summary>
    public event Action<int>? ScaleChangeRequested;

    // ── 运行状态 ──

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunning))]
    [NotifyPropertyChangedFor(nameof(ButtonText))]
    [NotifyPropertyChangedFor(nameof(ButtonEnabled))]
    [NotifyPropertyChangedFor(nameof(SpinnerVisible))]
    private RunState _state = RunState.Idle;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BalanceHoursText))]
    [NotifyPropertyChangedFor(nameof(BalanceMinutesText))]
    private double _balanceMinutes;

    // 版式化余额：数字与单位分开排版，增强设计感；分钟两位补零（9 → 09）。
    // 余额 = 钱包 score，按 1 score = 1 分钟换算（待实测校准）；未登录显示占位符 "--"。
    public int BalanceHours
    {
        get { var t = (int)Math.Round(BalanceMinutes); return t / 60; }
    }

    public string BalanceHoursText => IsLoggedIn ? BalanceHours.ToString() : "--";

    public string BalanceMinutesText => IsLoggedIn
        ? ((int)Math.Round(BalanceMinutes) % 60).ToString("D2")
        : "--";

    // ── 登录态 ──

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

    /// <summary>主标题：手机号（默认官方样式脱敏 12******901，点击显示原文，均不带 +86 前缀）</summary>
    public string AccountName => PhoneRevealed ? PhoneDigits(_cred.Phone) : PhoneMasked;

    /// <summary>副标题：用户 ID（随手机号一起切换显隐）</summary>
    public string UserIdText => PhoneRevealed ? $"ID:{_cred.Sub}" : $"ID:{MaskId(_cred.Sub)}";

    /// <summary>手机号脱敏显示（官方样式）</summary>
    private string PhoneMasked => MaskPhone(PhoneDigits(_cred.Phone));

    // 结构保留供扩展性
    public System.Collections.ObjectModel.ObservableCollection<string> Accounts { get; } = [];

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

    /// <summary>添加账号。</summary>
    [RelayCommand]
    private void AddAccount() => OpenLogin();

    // ── 任务进度 ──

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText))]
    [NotifyPropertyChangedFor(nameof(StageText))]
    [NotifyPropertyChangedFor(nameof(ProgressCurrentText))]
    private int _claimIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText))]
    [NotifyPropertyChangedFor(nameof(StageText))]
    [NotifyPropertyChangedFor(nameof(ProgressTotalText))]
    private int _totalClaims;

    [ObservableProperty]
    private IReadOnlyList<int> _segmentSizes = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TitleText))]
    [NotifyPropertyChangedFor(nameof(CurrentPage))]
    private bool _isSettingsOpen;

    /// <summary>供 ContentControl + DataTemplate 的页面切换（配入场动画）。</summary>
    public object CurrentPage => IsSettingsOpen ? (object)Settings : this;

    [ObservableProperty]
    private bool _showExitConfirm;

    public string TitleText => IsSettingsOpen ? "设置" : "glacc-auto";

    /// <summary>状态栏提示（网络错误 / 登录过期 / 每次领取结果等）</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusText))]
    private string _statusText = "";

    public bool HasStatusText => !string.IsNullOrEmpty(StatusText);

    public bool IsRunning => State == RunState.Running;
    public bool ButtonEnabled => State == RunState.Idle && IsLoggedIn;
    public bool SpinnerVisible => State == RunState.Running;

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

    public string StageText
    {
        get
        {
            if (!IsLoggedIn) return "登录后同步今日任务";
            if (TotalClaims == 0) return "今日暂无任务数据";
            if (ClaimIndex >= TotalClaims) return "今日任务已全部完成";
            var acc = 0;
            foreach (var s in _stages)
            {
                if (ClaimIndex < acc + s.StageSum)
                {
                    var remainingInStage = acc + s.StageSum - ClaimIndex;
                    return $"{s.Name} · 本阶段还剩 {remainingInStage} 次";
                }
                acc += s.StageSum;
            }
            return "";
        }
    }

    // ── 启动：恢复登录态 ──

    private async Task InitializeAsync()
    {
        try
        {
            if (!_cred.HasToken && !_cred.HasRefreshToken)
            {
                StatusText = "";
                return;
            }
            StatusText = "正在恢复登录态…";
            var r = await _auth.RefreshAsync();
            if (!r.Ok)
            {
                IsLoggedIn = false;
                StatusText = r.Error;
                return;
            }
            await EnterLoggedInAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"初始化失败：{ex.Message}";
        }
    }

    private async Task EnterLoggedInAsync()
    {
        IsLoggedIn = true;
        PhoneRevealed = false;
        Accounts.Clear();
        Accounts.Add(PhoneMasked);
        await RefreshSnapshotAsync();
    }

    /// <summary>拉取任务进度 + 钱包余额并刷新 UI。</summary>
    private async Task RefreshSnapshotAsync()
    {
        var stages = await _game.GetTaskStagesAsync();
        var score = await _game.GetWalletScoreAsync();
        if (score is not null) BalanceMinutes = ScoreToMinutes(score.Value);
        if (stages is not null)
        {
            ApplyStages(stages);
            StatusText = "";
        }
        else if (score is null)
        {
            StatusText = "无法连接服务，请检查网络后重试";
        }
    }

    private void ApplyStages(List<GlaccTaskStage> stages)
    {
        _stages = stages;
        SegmentSizes = stages.Select(s => s.StageSum).ToArray();
        TotalClaims = stages.Sum(s => s.StageSum);
        ClaimIndex = stages.Sum(s => s.StageCurrent);
        if (State == RunState.Idle && TotalClaims > 0 && ClaimIndex >= TotalClaims)
            State = RunState.Done;
        if (State == RunState.Done && ClaimIndex < TotalClaims)
            State = RunState.Idle;
        // ClaimIndex/TotalClaims 值可能未变（ObservableProperty 不触发通知），手动补齐派生属性
        OnPropertyChanged(nameof(StageText));
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(ProgressCurrentText));
        OnPropertyChanged(nameof(ProgressTotalText));
    }

    // ── 领取主流程 ──

    [RelayCommand]
    private async Task StartAsync()
    {
        if (!IsLoggedIn)
        {
            OpenLogin();
            return;
        }
        if (State != RunState.Idle) return;
        if (TotalClaims == 0)
        {
            StatusText = "正在同步任务…";
            await RefreshSnapshotAsync();
            if (TotalClaims == 0) return;
        }
        State = RunState.Running;
        StatusText = "";
        try
        {
            await RunClaimLoopAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"领取中断：{ex.Message}";
            State = ClaimIndex >= TotalClaims && TotalClaims > 0 ? RunState.Done : RunState.Idle;
        }
    }

    private async Task RunClaimLoopAsync()
    {
        // 开始前拉一次最新进度（避免与他处已完成的重复推送）
        var fresh = await _game.GetTaskStagesAsync();
        if (fresh is null)
        {
            StatusText = "网络错误，无法获取任务进度";
            State = RunState.Idle;
            return;
        }
        ApplyStages(fresh);
        if (ClaimIndex >= TotalClaims)
        {
            State = RunState.Done;
            return;
        }

        for (var si = 0; si < _stages.Count && State == RunState.Running; si++)
        {
            while (_stages[si].StageCurrent < _stages[si].StageSum &&
                   State == RunState.Running && ClaimIndex < TotalClaims)
            {
                var stage = _stages[si];
                var push = await _game.PushTaskAsync(stage.TaskId);
                if (push is null)
                {
                    StatusText = $"网络波动：{stage.Name} 推送失败，重试中";
                }
                else if (push.Code == 0)
                {
                    _stages[si] = stage with { StageCurrent = stage.StageCurrent + 1 };
                    ClaimIndex = _stages.Sum(s => s.StageCurrent);
                    var addMinutes = ScoreToMinutes(push.AddScore);
                    StatusText = addMinutes > 0
                        ? $"已领取 {stage.Name}（+{addMinutes:0.#} 分钟）"
                        : $"已领取 {stage.Name}";
                    var score = await _game.GetWalletScoreAsync();
                    if (score is not null) BalanceMinutes = ScoreToMinutes(score.Value);
                }
                else
                {
                    // -1702 = 阶段已满；其他 code = 服务端限制，跳下一阶段
                    StatusText = $"服务端返回 code={push.Code}（{stage.Name} 暂不可推），跳下一阶段";
                    break;
                }
                if (State != RunState.Running) break;
                await Task.Delay(NextInterval());
            }
        }

        if (State == RunState.Running)
        {
            // 收尾：以服务端进度为准
            var final = await _game.GetTaskStagesAsync();
            if (final is not null) ApplyStages(final);
            var wallet = await _game.GetWalletScoreAsync();
            if (wallet is not null) BalanceMinutes = ScoreToMinutes(wallet.Value);
            State = RunState.Done;
            // 完成态提示由任务卡副标题（StageText）唯一表达，状态栏直接清空避免重复
            StatusText = "";
        }
    }

    /// <summary>领取间隔：取设置中的随机区间（默认 30~40 秒）。</summary>
    private TimeSpan NextInterval()
    {
        var min = Math.Clamp(_settings.IntervalMinSec, 1, 3600);
        var max = Math.Clamp(_settings.IntervalMaxSec, min, 3600);
        return TimeSpan.FromSeconds(Random.Shared.Next(min, max + 1));
    }

    /// <summary>钱包 score → 分钟（80 score = 1 分钟，官方账号页实测）。</summary>
    private static double ScoreToMinutes(long score) => score / GlaccConstants.ScorePerMinute;

    /// <summary>手机号脱敏（官方样式）：11 位 → 12******901（前 2 + 6 星 + 后 3）。</summary>
    private static string MaskPhone(string phone) =>
        phone.Length <= 5 ? phone : phone[..2] + "******" + phone[^3..];

    /// <summary>ID 脱敏：ID 更短，保留后 2 位（6 位 → ****56）。</summary>
    private static string MaskId(string id) =>
        id.Length <= 2 ? id : new string('*', id.Length - 2) + id[^2..];

    // ── 登录引导（真实流程：手机号 + 短信验证码）──

    [ObservableProperty]
    private bool _showLoginDialog;

    /// <summary>false = 第一步手机号；true = 第二步验证码</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SentToText))]
    private bool _loginCodeStep;

    [ObservableProperty]
    private string _phoneInput = PhoneDigits("");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCodeInput))]
    private string _codeInput = "";

    /// <summary>重发倒计时（秒），0 表示可发送</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ResendText))]
    [NotifyPropertyChangedFor(nameof(CanSendCode))]
    private int _resendSeconds;

    [ObservableProperty]
    private string _loginError = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSendCode))]
    [NotifyPropertyChangedFor(nameof(CanConfirmLogin))]
    private bool _isLoginBusy;

    public bool HasLoginError => !string.IsNullOrEmpty(LoginError);
    public bool HasCodeInput => !string.IsNullOrWhiteSpace(CodeInput);
    public bool CanSendCode => ResendSeconds <= 0 && !IsLoginBusy;
    public bool CanConfirmLogin => HasCodeInput && !IsLoginBusy;
    public string ResendText => ResendSeconds > 0 ? $"{ResendSeconds} 秒后可重新发送" : "重新发送验证码";
    public string SentToText => $"验证码已发送至 {PhoneMasked}，5 分钟内有效";

    private DispatcherTimer? _resendTimer;

    [RelayCommand]
    private void OpenLogin()
    {
        StopResendTimer();
        LoginCodeStep = false;
        CodeInput = "";
        LoginError = "";
        IsLoginBusy = false;
        ResendSeconds = 0;
        PhoneInput = PhoneDigits(_cred.Phone);
        ShowLoginDialog = true;
    }

    [RelayCommand]
    private void CancelLogin() => ShowLoginDialog = false;

    [RelayCommand]
    private void BackToPhone()
    {
        StopResendTimer();
        ResendSeconds = 0;
        LoginError = "";
        LoginCodeStep = false;
    }

    [RelayCommand]
    private async Task SendCodeAsync()
    {
        if (!CanSendCode) return;
        LoginError = "";
        IsLoginBusy = true;
        GlaccResult r;
        try
        {
            r = await _auth.SendSmsAsync(PhoneInput);
        }
        catch (Exception ex)
        {
            r = GlaccResult.Fail($"发送验证码失败：{ex.Message}");
        }
        IsLoginBusy = false;
        if (!r.Ok)
        {
            LoginError = r.Error;
            return;
        }
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
    private async Task ConfirmLoginAsync()
    {
        if (!CanConfirmLogin) return;
        LoginError = "";
        IsLoginBusy = true;
        GlaccResult r;
        try
        {
            r = await _auth.LoginAsync(CodeInput.Trim());
        }
        catch (Exception ex)
        {
            r = GlaccResult.Fail($"登录失败：{ex.Message}");
        }
        IsLoginBusy = false;
        if (!r.Ok)
        {
            LoginError = r.Error;
            return;
        }
        StopResendTimer();
        ShowLoginDialog = false;
        await EnterLoggedInAsync();
    }

    /// <summary>点击账号行：切换 显示/隐藏 完整手机号</summary>
    [RelayCommand]
    private void TogglePhoneReveal() => PhoneRevealed = !PhoneRevealed;

    private void StopResendTimer()
    {
        _resendTimer?.Stop();
        _resendTimer = null;
    }

    /// <summary>凭证手机号 → 纯 11 位数字（供输入框预填）。</summary>
    private static string PhoneDigits(string phone)
    {
        var digits = new string(phone.Where(char.IsDigit).ToArray());
        if (digits.Length == 13 && digits.StartsWith("86")) digits = digits[2..];
        return digits;
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
