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
    private readonly GlaccSession _session;
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
        _session = new GlaccSession(_cred, _auth, () => _settings.NetworkRetryCount);
        _game = new GlaccGameClient(_cred, _session);
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
    [NotifyPropertyChangedFor(nameof(StatusOffset))]
    [NotifyPropertyChangedFor(nameof(StatusOpacity))]
    private string _statusText = "";

    public bool HasStatusText => !string.IsNullOrEmpty(StatusText);

    /// <summary>状态栏显示时下方按钮的避让位移（DIP）：状态栏不占布局空间，按钮让位并带过渡动画。</summary>
    public double StatusOffset => HasStatusText ? 16 : 0;

    /// <summary>状态栏显隐：常驻布局树（位于 0 高度行），只做淡入淡出，避免切换时布局重排。</summary>
    public double StatusOpacity => HasStatusText ? 1 : 0;

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
            // JWT 仍在有效期且未到 refresh_token 保活间隔：直接复用本地登录态，不打刷新请求
            if (_session.JwtNeedsRefresh)
            {
                StatusText = "正在恢复登录态…";
                var r = await _session.EnsureJwtAsync();
                if (!r.Ok)
                {
                    IsLoggedIn = false;
                    StatusText = r.Error;
                    // 登录已过期（invalid_grant）：打开登录引导，预填手机号
                    if (r.NeedRelogin) HandleRelogin();
                    return;
                }
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
        if (!TryHandle(stages, "网络异常，无法同步任务进度")) return;
        ApplyStages(stages.Value!);
        StatusText = "";

        var score = await _game.GetWalletScoreAsync();
        if (score.Reason == GlaccFailReason.NeedRelogin)
        {
            HandleRelogin();
            return;
        }
        if (score.Ok) BalanceMinutes = ScoreToMinutes(score.Value);
    }

    /// <summary>
    /// 统一处理请求结果：成功继续；登录态无法续期则中断并引导重新登录；
    /// 网络失败给出终态提示并回到空闲态（避免残留"正在…"类临时文案）。
    /// </summary>
    private bool TryHandle<T>(GlaccCallResult<T> result, string networkError)
    {
        if (result.Ok) return true;
        if (result.Reason == GlaccFailReason.NeedRelogin)
        {
            HandleRelogin();
            return false;
        }
        StatusText = networkError;
        State = RunState.Idle;
        return false;
    }

    /// <summary>登录态失效且无法自动续期：中断领取、切回主页并弹短信登录引导。</summary>
    private void HandleRelogin()
    {
        State = RunState.Idle;
        StatusText = "登录已过期，请重新短信登录";
        IsSettingsOpen = false;
        OpenLogin();
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
        // 对账①：开始前拉最新进度，确定阶段一的剩余领取次数（避免与他处已完成的重复推送）。
        // 对账总次数 = 剩余阶段数 + 开头 1 次：开头这次定阶段一领几次，之后每次阶段结束对账
        // 顺带定出下一阶段领几次；末阶段的结束对账即收尾，不再重复查询。
        var fresh = await _game.GetTaskStagesAsync();
        if (!TryHandle(fresh, "网络异常，已中断")) return;
        ApplyStages(fresh.Value!);
        if (ClaimIndex >= TotalClaims)
        {
            // 开头对账即今日已全部完成：无末阶段收尾，这里补查一次钱包刷新余额
            var wallet = await _game.GetWalletScoreAsync();
            if (wallet.Reason == GlaccFailReason.NeedRelogin)
            {
                HandleRelogin();
                return;
            }
            if (wallet.Ok) BalanceMinutes = ScoreToMinutes(wallet.Value);
            State = RunState.Done;
            return;
        }

        for (var si = 0; si < _stages.Count && State == RunState.Running; si++)
        {
            var firstPush = true;
            while (_stages[si].StageCurrent < _stages[si].StageSum &&
                   State == RunState.Running && ClaimIndex < TotalClaims)
            {
                if (!firstPush) await Task.Delay(NextInterval());
                firstPush = false;
                var stage = _stages[si];

                // 每个 push 请求的重试预算与 JWT 续期由 GlaccSession 统一处理，失败提示走回调
                var push = await _game.PushTaskAsync(stage.TaskId,
                    onRetry: (_, wait) =>
                    {
                        StatusText = $"网络波动：{stage.Name} 推送失败，{wait.TotalSeconds:0} 秒后重试";
                        return Task.CompletedTask;
                    });
                if (!TryHandle(push, "网络异常，已中断")) return;
                if (State != RunState.Running) break;

                var result = push.Value!;
                if (result.Code == 0)
                {
                    _stages[si] = stage with { StageCurrent = stage.StageCurrent + 1 };
                    ClaimIndex = _stages.Sum(s => s.StageCurrent);
                    var addMinutes = ScoreToMinutes(result.AddScore);
                    StatusText = addMinutes > 0
                        ? $"已领取 {stage.Name}（+{addMinutes:0.#} 分钟）"
                        : $"已领取 {stage.Name}";
                }
                else
                {
                    // -1702 = 阶段已满；其他 code = 服务端限制，跳下一阶段
                    StatusText = $"服务端返回 code={result.Code}（{stage.Name} 暂不可推），跳下一阶段";
                    break;
                }
            }

            // 阶段推送跑完后对账：等待 3s 让服务端落账，再拉服务端进度比对本地计数。
            // 等待短于落账耗时会把"已发放但服务端未记账"误判为发放链路失效（实测落账 ≤2s）。
            // 本地计数虚高（服务端进度少于已确认的领取次数）= 发放链路失效，中断业务；
            // 服务端更高（他处领取/阶段已满跳过等）以服务端为准继续。
            if (State != RunState.Running) break;
            await Task.Delay(TimeSpan.FromSeconds(3));
            // 运行期空列表视为查询失败：对账中服务端不应清空进度
            var server = await _game.GetTaskStagesAsync(accept: s => s is { Count: > 0 });
            if (!TryHandle(server, "网络异常，已中断")) return;
            var serverStages = server.Value!;
            var serverCount = serverStages.Sum(s => s.StageCurrent);
            if (ClaimIndex > serverCount)
            {
                ApplyStages(serverStages);
                StatusText = $"进度对账异常（本地 {ClaimIndex} 次 / 服务端 {serverCount} 次），已停止领取";
                State = RunState.Idle;
                return;
            }
            ApplyStages(serverStages);
        }

        if (State == RunState.Running)
        {
            // 收尾：末阶段的结束对账已同步服务端进度，这里只刷新钱包余额
            var wallet = await _game.GetWalletScoreAsync();
            if (wallet.Reason == GlaccFailReason.NeedRelogin)
            {
                HandleRelogin();
                return;
            }
            if (wallet.Ok) BalanceMinutes = ScoreToMinutes(wallet.Value);
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
