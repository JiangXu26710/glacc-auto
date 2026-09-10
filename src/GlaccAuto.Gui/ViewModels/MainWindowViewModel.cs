using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GlaccAuto.Core;
using GlaccAuto.Core.Diagnostics;
using GlaccAuto.Core.Glacc;
using GlaccAuto.Core.Notify;
using GlaccAuto.Core.Scheduling;

namespace GlaccAuto.Gui.ViewModels;

/// <summary>
/// 主窗口视图模型：接入真实业务。
/// 登录态（refresh 保活 / 短信登录）、任务进度与钱包（mobileGLTaskList / get_user_wallet）、
/// 主任务52 各阶段直推（mobileGLTaskPush，MD5 签名）均走 GlaccAuto.Core.Glacc 协议层。
/// 定时领取（Windows 计划任务）在此调度：计划任务拉起时自动执行领取，成功后自动退出。
/// </summary>
public partial class MainWindowViewModel : ViewModelBase
{
    private readonly AppSettings _settings;
    private readonly GlaccCredentials _cred;
    private readonly GlaccAuthClient _auth;
    private readonly GlaccSession _session;
    private readonly GlaccGameClient _game;
    private readonly bool _scheduledLaunch;

    /// <summary>当前任务阶段（服务端每日可变，从 mobileGLTaskList 动态读取）</summary>
    private List<GlaccTaskStage> _stages = [];

    /// <summary>本次定时运行的失败原因（null = 成功或未运行）；供 Server酱结果通知使用</summary>
    private string? _scheduledOutcome;

    public MainWindowViewModel(AppSettings settings, bool scheduledLaunch = false)
    {
        _settings = settings;
        _scheduledLaunch = scheduledLaunch;
        Settings = new SettingsViewModel(settings);
        Settings.ScaleChangeRequested += p => ScaleChangeRequested?.Invoke(p);

        _cred = GlaccCredentials.Load();
        _auth = new GlaccAuthClient(_cred);
        _session = new GlaccSession(_cred, _auth, () => _settings.NetworkRetryCount);
        _game = new GlaccGameClient(_cred, _session);
        StartClaimSignalListener();
        SyncScheduledTask(skipIfUserTouched: true);
        _ = InitializeAsync();
    }

    public SettingsViewModel Settings { get; }

    public bool ExitConfirmed { get; set; }
    public event Action? CloseRequested;

    /// <summary>请求把诊断信息写入系统剪贴板（剪贴板由视图提供）</summary>
    public event Action<string>? CopyRequested;

    /// <summary>设置页缩放变更 → MainWindow 应用缩放并弹出保护确认</summary>
    public event Action<int>? ScaleChangeRequested;

    // ── 运行状态 ──

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunning))]
    [NotifyPropertyChangedFor(nameof(ButtonText))]
    [NotifyPropertyChangedFor(nameof(ButtonEnabled))]
    [NotifyPropertyChangedFor(nameof(SpinnerVisible))]
    [NotifyPropertyChangedFor(nameof(AccountMenuEnabled))]
    private RunState _state = RunState.Idle;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BalanceHoursText))]
    [NotifyPropertyChangedFor(nameof(BalanceMinutesText))]
    private double _balanceMinutes;

    /// <summary>是否成功取到过余额：决定余额区显示真实数字还是占位符（避免把"没查到"显示成 0 时 00 分）</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BalanceHoursText))]
    [NotifyPropertyChangedFor(nameof(BalanceMinutesText))]
    [NotifyPropertyChangedFor(nameof(HasBalanceHint))]
    [NotifyPropertyChangedFor(nameof(BalanceHintTooltip))]
    private bool _balanceFetched;

    /// <summary>余额未同步原因（空 = 尚未查过或上次查询成功）；非空时余额卡标签行出现"未同步 · 重试"入口</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBalanceHint))]
    [NotifyPropertyChangedFor(nameof(BalanceHintTooltip))]
    private string _balanceHint = "";

    /// <summary>重试查询进行中：避免连点发起并发请求</summary>
    [ObservableProperty]
    private bool _balanceBusy;

    /// <summary>已登录但余额未知（查询失败，或因任务进度拉取失败而根本没查过）→ 给出就地重试入口</summary>
    public bool HasBalanceHint => IsLoggedIn && !BalanceFetched;

    public string? BalanceHintTooltip => HasBalanceHint
        ? $"{(BalanceHint.Length > 0 ? BalanceHint : "尚未取到余额")}{Environment.NewLine}点击重试"
        : null;

    // 版式化余额：数字与单位分开排版，增强设计感；分钟两位补零（9 → 09）。
    // 余额 = 钱包 score，按 80 score = 1 分钟换算；未登录或尚未取到余额时显示占位符 "--"。
    public int BalanceHours
    {
        get { var t = (int)Math.Round(BalanceMinutes); return t / 60; }
    }

    public string BalanceHoursText => BalanceKnown ? BalanceHours.ToString() : "--";

    public string BalanceMinutesText => BalanceKnown
        ? ((int)Math.Round(BalanceMinutes) % 60).ToString("D2")
        : "--";

    /// <summary>余额可信：已登录且成功取到过余额</summary>
    private bool BalanceKnown => IsLoggedIn && BalanceFetched;

    // ── 登录态 ──

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccountName))]
    [NotifyPropertyChangedFor(nameof(UserIdText))]
    [NotifyPropertyChangedFor(nameof(BalanceHoursText))]
    [NotifyPropertyChangedFor(nameof(BalanceMinutesText))]
    [NotifyPropertyChangedFor(nameof(HasBalanceHint))]
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
    [NotifyPropertyChangedFor(nameof(StatusTooltip))]
    [NotifyPropertyChangedFor(nameof(CanCopyStatus))]
    private string _statusText = "";

    /// <summary>状态栏提示对应的技术细节（异常类型与信息、服务端 code 等），供悬浮提示与"复制错误信息"</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusTooltip))]
    [NotifyPropertyChangedFor(nameof(HasStatusDetail))]
    private string _statusDetail = "";

    /// <summary>状态栏是否处于"需要用户处理"档（红字）：请求失败与需重新登录</summary>
    [ObservableProperty]
    private bool _statusIsError;

    public bool HasStatusText => !string.IsNullOrEmpty(StatusText);

    public bool HasStatusDetail => !string.IsNullOrEmpty(StatusDetail);

    /// <summary>有提示即可复制（含领取结果）；无提示时复制入口不出现</summary>
    public bool CanCopyStatus => HasStatusText;

    /// <summary>状态栏悬浮提示：完整文案 +（有则）技术细节与日志文件路径；无提示时为空，不弹空气泡。</summary>
    public string? StatusTooltip
    {
        get
        {
            if (!HasStatusText) return null;
            var lines = new List<string> { StatusText };
            if (HasStatusDetail)
            {
                lines.Add($"详情：{StatusDetail}");
                lines.Add($"日志：{DiagLog.CurrentFilePath}");
            }
            return string.Join(Environment.NewLine, lines);
        }
    }

    /// <summary>状态栏显示时下方按钮的避让位移（DIP）：状态栏不占布局空间，按钮让位并带过渡动画。</summary>
    public double StatusOffset => HasStatusText ? 16 : 0;

    /// <summary>状态栏显隐：常驻布局树（位于 0 高度行），只做淡入淡出，避免切换时布局重排。</summary>
    public double StatusOpacity => HasStatusText ? 1 : 0;

    public bool IsRunning => State == RunState.Running;
    public bool ButtonEnabled => State == RunState.Idle && IsLoggedIn;
    public bool SpinnerVisible => State == RunState.Running;

    /// <summary>账号区右键菜单可用：领取进行中进度与余额在实时推进，刷新无意义且退出会中断领取</summary>
    public bool AccountMenuEnabled => State != RunState.Running;

    public string ButtonText => !IsLoggedIn
        ? "请先登录"
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
            DiagLog.Info(_scheduledLaunch ? "定时拉起：开始恢复登录态" : "启动：开始恢复登录态");
            if (!_cred.HasToken && !_cred.HasRefreshToken)
            {
                ClearStatus();
                if (_scheduledLaunch)
                {
                    // 定时拉起但无可用登录态：留在登录引导，不自动退出，发失败通知
                    SetStatus("定时领取：尚未登录", isError: true);
                    _scheduledOutcome ??= "尚未登录，需要先完成短信登录";
                    DiagLog.Warn("定时领取：本地无可用登录态");
                    _ = NotifyScheduledOutcomeAsync();
                    OpenLogin();
                }
                return;
            }
            // JWT 仍在有效期且未到 refresh_token 保活间隔：直接复用本地登录态，不打刷新请求
            if (_session.JwtNeedsRefresh)
            {
                SetStatus("正在恢复登录态…");
                var r = await _session.EnsureJwtAsync();
                if (!r.Ok)
                {
                    IsLoggedIn = false;
                    SetStatus(r.Error, isError: true);
                    DiagLog.Warn($"恢复登录态失败：{r.Error}");
                    if (!r.NeedRelogin) _scheduledOutcome ??= r.Error;
                    // 登录已过期（invalid_grant）：打开登录引导，预填手机号
                    if (r.NeedRelogin) HandleRelogin();
                    if (_scheduledLaunch) _ = NotifyScheduledOutcomeAsync();
                    return;
                }
            }
            await EnterLoggedInAsync();
            if (_scheduledLaunch) await RunScheduledClaimAsync();
        }
        catch (Exception ex)
        {
            Fail($"初始化失败：{ex.Message}", GlaccSession.Describe(ex));
            if (_scheduledLaunch) _ = NotifyScheduledOutcomeAsync();
        }
    }

    private async Task EnterLoggedInAsync()
    {
        IsLoggedIn = true;
        PhoneRevealed = false;
        Accounts.Clear();
        Accounts.Add(PhoneMasked);
        // 换账号后旧余额不再可信：先置为未知，由下面的快照刷新重新取值或给出未同步入口
        BalanceFetched = false;
        BalanceHint = "";
        if (await RefreshSnapshotAsync())
            DiagLog.Info($"登录态就绪：今日任务 {TotalClaims} 次，已完成 {ClaimIndex} 次");
    }

    /// <summary>拉取任务进度 + 钱包余额并刷新 UI；返回是否取得任务进度。</summary>
    private async Task<bool> RefreshSnapshotAsync()
    {
        var stages = await _game.GetTaskStagesAsync(
            onRetry: (a, w) => OnRetryNoticeAsync("任务进度查询", a, w));
        if (!TryHandle(stages, "无法同步任务进度")) return false;
        ApplyStages(stages.Value!);
        ClearStatus();

        var score = await _game.GetWalletScoreAsync(
            onRetry: (a, w) => OnRetryNoticeAsync("余额查询", a, w));
        if (!ApplyWallet(score)) return false;
        return true;
    }

    /// <summary>
    /// 应用一次钱包查询结果：成功则刷新余额并清除"未同步"标记，失败则记录原因
    /// （供余额卡内提示与日志）。返回 false 表示登录态已失效，调用方应中断。
    /// </summary>
    private bool ApplyWallet(GlaccCallResult<long> score)
    {
        if (TryHandleRelogin(score.Reason)) return false;
        if (score.Ok)
        {
            BalanceMinutes = ScoreToMinutes(score.Value);
            BalanceFetched = true;
            BalanceHint = "";
            return true;
        }
        BalanceHint = $"{DescribeFailure(score.Reason)}（{score.Detail}）";
        DiagLog.Warn($"查询余额失败：{score.Reason}｜{score.Detail}");
        return true;
    }

    /// <summary>余额摘要：未取到时明确写"未知"，不输出可能是默认值的 0。</summary>
    private string BalanceSummary => BalanceFetched
        ? $"{BalanceHours} 小时 {((int)Math.Round(BalanceMinutes)) % 60:00} 分"
        : "未知";

    /// <summary>余额未同步时手动重试一次钱包查询（只读请求，无副作用）。</summary>
    [RelayCommand]
    private async Task RetryBalanceAsync()
    {
        if (!HasBalanceHint || BalanceBusy) return;
        BalanceBusy = true;
        try
        {
            var score = await _game.GetWalletScoreAsync(
                onRetry: (a, w) => OnRetryNoticeAsync("余额查询", a, w));
            if (!ApplyWallet(score)) return;
            if (score.Ok) DiagLog.Info($"余额已刷新：{BalanceSummary}");
        }
        finally
        {
            BalanceBusy = false;
        }
    }

    /// <summary>清空状态栏（成功与进行中路径）。</summary>
    private void ClearStatus() => SetStatus("");

    /// <summary>
    /// 请求失败、即将自动重试时的状态栏提示：让用户知道程序仍在重试而不是卡住了
    /// （退避 2/4/8/16/32/64 秒，断网时最长会静默等待两分钟）。
    /// </summary>
    /// <param name="what">业务描述，如"任务进度""余额""阶段一 推送"</param>
    private Task OnRetryNoticeAsync(string what, int attempt, TimeSpan wait)
    {
        SetStatus($"网络波动：{what}失败，{wait.TotalSeconds:0} 秒后自动重试（第 {attempt} 次）");
        return Task.CompletedTask;
    }

    /// <summary>写状态栏：文案 + 技术细节 + 严重度（isError = 需要用户处理，界面按错误色呈现）。</summary>
    private void SetStatus(string text, string detail = "", bool isError = false)
    {
        StatusText = text;
        StatusDetail = detail;
        StatusIsError = isError;
    }

    /// <summary>失败原因 → 用户可读文案；技术细节另行给出（悬浮提示与"复制错误信息"）。</summary>
    private static string DescribeFailure(GlaccFailReason reason) => reason switch
    {
        GlaccFailReason.NoResponse => "网络不可用：无法连接服务器",
        GlaccFailReason.ServerError => "服务端暂时不可用（HTTP 5xx）",
        GlaccFailReason.BadResponse => "服务端响应无法解析：接口可能已变更",
        GlaccFailReason.ClientError => "本机网络组件异常：TLS 指纹库不可用",
        GlaccFailReason.NeedRelogin => "登录已过期，请重新短信登录",
        GlaccFailReason.NotLoggedIn => "尚未登录，请先完成短信登录",
        _ => "网络异常",
    };

    /// <summary>置失败终态：状态栏文案 + 技术细节，中断领取并记录日志与定时通知原因。</summary>
    /// <param name="message">面向用户的终态提示</param>
    /// <param name="detail">技术细节（异常类型与信息），写入日志并供复制</param>
    private void Fail(string message, string detail = "")
    {
        SetStatus(message, detail, isError: true);
        State = RunState.Idle;
        _scheduledOutcome ??= message;
        DiagLog.Warn($"领取终态：{message}｜{detail}");
    }

    /// <summary>
    /// 统一处理请求结果：成功继续；登录态不可用则中断并引导重新登录；
    /// 请求失败按原因给出终态提示并回到空闲态（避免残留"正在…"类临时文案）。
    /// </summary>
    /// <param name="context">补充说明（如"无法同步任务进度""已中断"）</param>
    private bool TryHandle<T>(GlaccCallResult<T> result, string context)
    {
        if (result.Ok) return true;
        if (TryHandleRelogin(result.Reason)) return false;
        Fail($"{DescribeFailure(result.Reason)}，{context}", result.Detail);
        return false;
    }

    /// <summary>登录态不可用（本地无凭证 / 无法自动续期）时中断并引导重新登录；返回是否已处理。</summary>
    private bool TryHandleRelogin(GlaccFailReason reason)
    {
        if (reason is not (GlaccFailReason.NeedRelogin or GlaccFailReason.NotLoggedIn)) return false;
        HandleRelogin(expired: reason == GlaccFailReason.NeedRelogin);
        return true;
    }

    /// <summary>中断领取、切回主页并弹短信登录引导。</summary>
    /// <param name="expired">true = 原登录态已失效；false = 本地尚无凭证</param>
    private void HandleRelogin(bool expired = true)
    {
        State = RunState.Idle;
        SetStatus(expired ? "登录已过期，请重新短信登录" : "尚未登录，请先完成短信登录", isError: true);
        _scheduledOutcome ??= expired ? "登录已过期，需要重新短信登录" : "尚未登录，需要先完成短信登录";
        DiagLog.Warn(expired ? "登录已过期，需重新短信登录" : "本地无登录凭证，需先完成短信登录");
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
        // Done = 上次领取已完成（多为昨日遗留）：定时信号代跑需跨日重新领取，
        // 先重新同步当日进度再决定是否进入领取；Idle 且无任务数据时同样先同步
        if (State is not (RunState.Idle or RunState.Done)) return;
        if (TotalClaims == 0 || State == RunState.Done)
        {
            SetStatus("正在同步任务…");
            // 同步失败时 TryHandle / HandleRelogin 已写终态与 _scheduledOutcome，直接中断
            if (!await RefreshSnapshotAsync()) return;
            if (TotalClaims == 0)
            {
                _scheduledOutcome ??= "服务端今日暂无任务数据";
                DiagLog.Warn("服务端今日未返回主任务数据，放弃本次领取");
                return;
            }
            // 同步后仍为 Done：服务端显示今日已完成，无需再领
            if (State != RunState.Idle) return;
        }
        State = RunState.Running;
        ClearStatus();
        DiagLog.Info($"开始领取：共 {TotalClaims} 次，已完成 {ClaimIndex} 次");
        try
        {
            await RunClaimLoopAsync();
        }
        catch (Exception ex)
        {
            DiagLog.Error("领取中断", ex);
            Fail($"领取中断：{ex.Message}", GlaccSession.Describe(ex));
            State = ClaimIndex >= TotalClaims && TotalClaims > 0 ? RunState.Done : RunState.Idle;
        }
    }

    private async Task RunClaimLoopAsync()
    {
        // 对账①：开始前拉最新进度，确定阶段一的剩余领取次数（避免与他处已完成的重复推送）。
        // 对账总次数 = 剩余阶段数 + 开头 1 次：开头这次定阶段一领几次，之后每次阶段结束对账
        // 顺带定出下一阶段领几次；末阶段的结束对账即收尾，不再重复查询。
        // 运行期空列表视为查询失败（与下方对账②口径一致）：StartAsync 已确认过非空，
        // 此处若照单全收会把"服务端清空进度"误判成 0>=0 已完成，空跑一次还发成功通知。
        var fresh = await _game.GetTaskStagesAsync(accept: s => s is { Count: > 0 },
            onRetry: (a, w) => OnRetryNoticeAsync("任务进度查询", a, w));
        if (!TryHandle(fresh, "已中断")) return;
        ApplyStages(fresh.Value!);
        if (ClaimIndex >= TotalClaims)
        {
            // 开头对账即今日已全部完成：无末阶段收尾，这里补查一次钱包刷新余额
            DiagLog.Info("对账显示今日任务已完成，无需领取");
            if (!ApplyWallet(await _game.GetWalletScoreAsync(
                    onRetry: (a, w) => OnRetryNoticeAsync("余额查询", a, w)))) return;
            CompleteScheduledRun();
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
                    onRetry: (a, w) => OnRetryNoticeAsync($"{stage.Name} 推送", a, w));
                if (!TryHandle(push, "已中断")) return;
                if (State != RunState.Running) break;

                var result = push.Value!;
                if (result.Code == 0)
                {
                    _stages[si] = stage with { StageCurrent = stage.StageCurrent + 1 };
                    ClaimIndex = _stages.Sum(s => s.StageCurrent);
                    var addMinutes = ScoreToMinutes(result.AddScore);
                    SetStatus(addMinutes > 0
                        ? $"已领取 {stage.Name}（+{addMinutes:0.#} 分钟）"
                        : $"已领取 {stage.Name}");
                    DiagLog.Info($"已领取 {stage.Name}，+{addMinutes:0.#} 分钟（taskId={stage.TaskId}）");
                }
                else
                {
                    // -1702 = 阶段已满；其他 code = 服务端限制，跳下一阶段
                    SetStatus($"服务端返回 code={result.Code}（{stage.Name} 暂不可推），跳下一阶段",
                        $"mobileGLTaskPush code={result.Code}，taskId={stage.TaskId}");
                    DiagLog.Warn($"推送被服务端拒绝：code={result.Code}，taskId={stage.TaskId}");
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
            var server = await _game.GetTaskStagesAsync(accept: s => s is { Count: > 0 },
                onRetry: (a, w) => OnRetryNoticeAsync("任务进度对账", a, w));
            if (!TryHandle(server, "已中断")) return;
            var serverStages = server.Value!;
            var serverCount = serverStages.Sum(s => s.StageCurrent);
            // ApplyStages 会把 ClaimIndex 覆盖为服务端计数，先存下本地值，消息里才能如实报告差异
            var localCount = ClaimIndex;
            if (localCount > serverCount)
            {
                ApplyStages(serverStages);
                SetStatus($"进度对账异常（本地 {localCount} 次 / 服务端 {serverCount} 次），已停止领取",
                    "服务端进度少于本地已确认的领取次数，发放链路可能已失效", isError: true);
                _scheduledOutcome ??= $"进度对账异常（本地 {localCount} 次 / 服务端 {serverCount} 次）";
                DiagLog.Error($"进度对账异常：本地 {localCount} 次 / 服务端 {serverCount} 次，已停止领取");
                State = RunState.Idle;
                return;
            }
            ApplyStages(serverStages);
        }

        if (State == RunState.Running)
        {
            // 收尾：末阶段的结束对账已同步服务端进度，这里只刷新钱包余额
            if (!ApplyWallet(await _game.GetWalletScoreAsync(
                    onRetry: (a, w) => OnRetryNoticeAsync("余额查询", a, w)))) return;
            if (ClaimIndex >= TotalClaims)
            {
                CompleteScheduledRun();
                // 完成态提示由任务卡副标题（StageText）唯一表达，状态栏直接清空避免重复
                ClearStatus();
            }
            else
            {
                // 阶段被服务端拒绝导致提前结束时进度不满：不进入完成态，
                // 否则定时通知会按"已完成"发出成功推送
                State = RunState.Idle;
                SetStatus($"本次未完成：{ClaimIndex} / {TotalClaims} 次（部分阶段被服务端拒绝）",
                    "阶段推送被服务端拒绝，剩余次数未领取", isError: true);
                _scheduledOutcome ??= $"领取未跑满：{ClaimIndex} / {TotalClaims} 次（部分阶段被服务端拒绝）";
                DiagLog.Warn($"领取结束但未跑满：{ClaimIndex} / {TotalClaims} 次");
            }
        }
    }

    /// <summary>领取间隔：取设置中的随机区间（默认 30~40 秒）。</summary>
    private TimeSpan NextInterval()
    {
        var min = Math.Clamp(_settings.IntervalMinSec, 1, 3600);
        var max = Math.Clamp(_settings.IntervalMaxSec, min, 3600);
        return TimeSpan.FromSeconds(Random.Shared.Next(min, max + 1));
    }

    // ── 定时领取（Windows 计划任务）──

    /// <summary>
    /// 领取到达完成态：进入 Done。定时拉起的自动退出由 RunScheduledClaimAsync 在
    /// 发送结果通知后执行；异常终态（网络中断/需重登/对账异常）到不了这里，窗口保持打开。
    /// </summary>
    private void CompleteScheduledRun()
    {
        State = RunState.Done;
        _scheduledOutcome = null; // 成功无需原因
        DiagLog.Info($"领取完成：{ClaimIndex} / {TotalClaims} 次，余额 {BalanceSummary}");
    }

    /// <summary>
    /// 定时发起的领取编排（计划任务拉起 / 运行中实例收到信号代跑）：
    /// 跑完一次领取 → 若配置了 Server酱则发送一条结果通知 → 定时拉起且领取正常跑通时自动退出。
    /// </summary>
    private async Task RunScheduledClaimAsync()
    {
        _scheduledOutcome = null;
        try
        {
            await StartAsync();
        }
        catch
        {
            // StartAsync 内部已兜底记录终态，这里防编排本身被打断导致通知缺失
        }
        await NotifyScheduledOutcomeAsync();
        if (_scheduledLaunch && State == RunState.Done)
        {
            ExitConfirmed = true;
            CloseRequested?.Invoke();
        }
    }

    /// <summary>发送本次定时运行结果通知；未配置 Server酱或发送失败均不影响主流程（失败落日志）。</summary>
    private async Task NotifyScheduledOutcomeAsync()
    {
        var key = _settings.ServerKey;
        if (!ServerChanClient.IsConfigured(key))
        {
            DiagLog.Info("未配置 Server酱通知，跳过结果推送");
            return;
        }
        var success = State == RunState.Done;
        var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        var desp = success
            ? $"**定时领取完成**\n\n- 时间: {now}\n- 本次进度: {ClaimIndex} / {TotalClaims} 次\n- 当前余额: {BalanceSummary}"
            : $"**定时领取未完成**\n\n- 时间: {now}\n- 原因: {_scheduledOutcome ?? "未知原因"}";
        var sent = await ServerChanClient.SendAsync(key.Trim(),
            success ? "glacc-auto 定时领取成功" : "glacc-auto 定时领取失败", desp);
        DiagLog.Info(sent ? "定时领取结果通知已发送" : "定时领取结果通知未送达");
    }

    /// <summary>监听"到点领取"信号：计划任务拉起了第二个实例，而本实例已在运行时，由本实例代为执行。</summary>
    private void StartClaimSignalListener()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                using var signal = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ClaimSignalName);
                while (signal.WaitOne())
                {
                    await Dispatcher.UIThread.InvokeAsync(OnClaimSignal);
                }
            }
            catch
            {
                // 信号监听不可用仅损失"运行中代跑"能力，不影响手动领取主流程
                DiagLog.Warn("到点领取信号监听不可用，运行中实例无法代跑定时领取");
            }
        });
    }

    private void OnClaimSignal()
    {
        // Idle = 常规代跑；Done = 上次已完成（多为昨日遗留），
        // StartAsync 会先重新同步当日进度：已复位则继续领取，仍完成则按既有语义发完成通知
        if (IsLoggedIn && State is RunState.Idle or RunState.Done)
        {
            DiagLog.Info(State == RunState.Done
                ? "收到到点领取信号：上次领取已完成，将重新同步当日进度后继续"
                : "收到到点领取信号，由本实例代跑");
            _ = RunScheduledClaimAsync();
        }
        else
        {
            DiagLog.Warn($"收到到点领取信号但未执行（已登录={IsLoggedIn}，当前状态={State}）");
        }
    }

    /// <summary>
    /// 计划任务状态同步：把任务的真实状态（XML 导出）投影到设置页，开关与时间均以系统为准；
    /// 任务处于启用态但定义与预期不符（可执行文件路径漂移、参数或电源设置被改）时按任务的真实时间重注册一次。
    /// </summary>
    /// <param name="skipIfUserTouched">true = 用户本会话已改过计划任务设置则不覆盖其改动。</param>
    private void SyncScheduledTask(bool skipIfUserTouched = false)
    {
        _ = Task.Run(() =>
        {
            try
            {
                if (!ScheduledTaskManager.TryQuery(out var snapshot))
                {
                    DiagLog.Warn("计划任务状态查询失败，本次状态同步跳过");
                    return;
                }
                Dispatcher.UIThread.Post(() =>
                    Settings.ApplyScheduleProjection(snapshot, skipIfUserTouched));
                var time = snapshot?.StartTime ?? ParseScheduledTime(_settings.ScheduledTime);
                if (snapshot is { Enabled: true, TriggerEnabled: true }
                    && !ScheduledTaskManager.MatchesExpectation(snapshot, time))
                {
                    DiagLog.Warn("计划任务定义与预期不符，重新注册");
                    ScheduledTaskManager.Register(time);
                }
            }
            catch (Exception ex)
            {
                // 同步失败不影响应用启动与手动领取
                DiagLog.Error("计划任务状态同步失败", ex);
            }
        });
    }

    private static TimeSpan ParseScheduledTime(string s) =>
        TimeSpan.TryParse(s, CultureInfo.InvariantCulture, out var t) ? t : new TimeSpan(8, 0, 0);

    /// <summary>钱包 score → 分钟（80 score = 1 分钟，官方账号页实测）。</summary>
    private static double ScoreToMinutes(long score) => score / GlaccConstants.ScorePerMinute;

    /// <summary>手机号脱敏（官方样式）：11 位 → 12******901（前 2 + 6 星 + 后 3）。</summary>
    private static string MaskPhone(string phone) =>
        phone.Length <= 5 ? phone : phone[..2] + "******" + phone[^3..];

    /// <summary>ID 脱敏：ID 更短，保留后 2 位（6 位 → ****56）。</summary>
    private static string MaskId(string id) =>
        id.Length <= 2 ? id : new string('*', id.Length - 2) + id[^2..];

    // ── 状态栏诊断 ──

    /// <summary>复制状态栏信息（版本、时间、提示、技术细节、日志路径），便于粘贴到 issue。</summary>
    [RelayCommand]
    private async Task CopyStatusAsync()
    {
        if (!HasStatusText) return;
        CopyRequested?.Invoke(BuildDiagnosticReport());
        DiagLog.Info("已复制状态栏信息");
        var restoreText = StatusText;
        var restoreDetail = StatusDetail;
        var restoreError = StatusIsError;
        // 沿用当前档位色：复制确认不改变严重度，避免颜色来回闪动
        SetStatus("已复制到剪贴板", isError: restoreError);
        await Task.Delay(TimeSpan.FromSeconds(2));
        // 期间若状态被新的领取结果刷新，则不回滚
        if (StatusText == "已复制到剪贴板")
        {
            SetStatus(restoreText, restoreDetail, restoreError);
        }
    }

    private string BuildDiagnosticReport()
    {
        var lines = new List<string>
        {
            AppInfo.VersionText,
            $"时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}",
            $"提示：{StatusText}",
        };
        if (HasStatusDetail) lines.Add($"详情：{StatusDetail}");
        lines.Add($"日志：{DiagLog.CurrentFilePath}");
        return string.Join(Environment.NewLine, lines);
    }

    // ── 账号区右键菜单 ──

    /// <summary>刷新数据进行中：避免连点发起并发请求（与余额重试的 BalanceBusy 相互独立）</summary>
    [ObservableProperty]
    private bool _dataRefreshBusy;

    /// <summary>退出登录二次确认层是否显示</summary>
    [ObservableProperty]
    private bool _showLogoutConfirm;

    /// <summary>
    /// 重新拉取今日任务进度与钱包余额（只读请求，无副作用）。
    /// 成功不写状态栏：余额与进度数字自身会更新，状态栏只留给需要用户处理的失败。
    /// </summary>
    [RelayCommand]
    private async Task RefreshDataAsync()
    {
        if (!IsLoggedIn || DataRefreshBusy) return;
        DataRefreshBusy = true;
        try
        {
            if (await RefreshSnapshotAsync()) DiagLog.Info($"数据已刷新：{BalanceSummary}");
        }
        catch (Exception ex)
        {
            // 只读刷新不动运行状态机，异常就地进状态栏
            DiagLog.Error("刷新数据异常", ex);
            SetStatus($"刷新数据失败：{ex.Message}", GlaccSession.Describe(ex), isError: true);
        }
        finally
        {
            DataRefreshBusy = false;
        }
    }

    /// <summary>请求退出登录：先弹二次确认，确认后才清除本地会话。</summary>
    [RelayCommand]
    private void Logout()
    {
        if (!IsLoggedIn) return;
        ShowLogoutConfirm = true;
    }

    [RelayCommand]
    private void CancelLogout() => ShowLogoutConfirm = false;

    /// <summary>
    /// 确认退出登录：清除本机保存的手机号、账号 ID 与令牌，回到未登录状态。
    /// 设备标识与设备档案由手机号派生、不落盘，同一手机号重登时自动复现，无需另行保存。
    /// </summary>
    [RelayCommand]
    private void ConfirmLogout()
    {
        ShowLogoutConfirm = false;
        _cred.ClearSession();
        _cred.Save();

        IsLoggedIn = false;
        PhoneRevealed = false;
        Accounts.Clear();
        State = RunState.Idle;
        _stages = [];
        SegmentSizes = [];
        TotalClaims = 0;
        ClaimIndex = 0;
        BalanceFetched = false;
        BalanceHint = "";
        BalanceMinutes = 0;
        ClearStatus();
        DiagLog.Info("已退出登录：本机账号信息已清除");
    }

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
    [NotifyPropertyChangedFor(nameof(CanConfirmLogin))]
    private string _codeInput = "";

    /// <summary>重发倒计时（秒），0 表示可发送</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ResendText))]
    [NotifyPropertyChangedFor(nameof(CanSendCode))]
    private int _resendSeconds;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLoginError))]
    private string _loginError = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSendCode))]
    [NotifyPropertyChangedFor(nameof(CanConfirmLogin))]
    private bool _isLoginBusy;

    public bool HasLoginError => !string.IsNullOrEmpty(LoginError);
    public bool CanSendCode => ResendSeconds <= 0 && !IsLoginBusy;
    public bool CanConfirmLogin => !string.IsNullOrWhiteSpace(CodeInput) && !IsLoginBusy;
    public string ResendText => ResendSeconds > 0 ? $"{ResendSeconds} 秒后可重新发送" : "重新发送验证码";
    public string SentToText => $"验证码已发送至 {MaskPhone(PhoneDigits(_cred.LoginPhone))}，5 分钟内有效";

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
        PhoneInput = PhoneDigits(_cred.LoginPhone);
        ShowLoginDialog = true;
    }

    /// <summary>关闭登录引导：停掉重发倒计时并清掉错误提示，重开时回到第一步手机号。</summary>
    [RelayCommand]
    private void CancelLogin()
    {
        ShowLoginDialog = false;
        StopResendTimer();
        ResendSeconds = 0;
        LoginError = "";
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
            DiagLog.Error("发送验证码异常", ex);
            r = GlaccResult.Fail($"发送验证码失败：{ex.Message}");
        }
        IsLoginBusy = false;
        if (!r.Ok)
        {
            DiagLog.Warn($"发送验证码失败：{r.Error}");
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
            DiagLog.Error("登录异常", ex);
            r = GlaccResult.Fail($"登录失败：{ex.Message}");
        }
        IsLoginBusy = false;
        if (!r.Ok)
        {
            DiagLog.Warn($"登录失败：{r.Error}");
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
    private void OpenSettings()
    {
        IsSettingsOpen = true;
        // 进入设置页时重新读取计划任务真实状态，避免显示启动后系统被外部改动的旧状态
        SyncScheduledTask();
    }

    [RelayCommand]
    private void ConfirmExit()
    {
        ExitConfirmed = true;
        CloseRequested?.Invoke();
    }

    [RelayCommand]
    private void CancelExit() => ShowExitConfirm = false;
}
