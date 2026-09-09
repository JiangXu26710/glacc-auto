using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Styling;

namespace GlaccAuto.Gui.Controls;

/// <summary>
/// 密码锁转子式数字文本：每个数字位是一个独立的滚轮，值变化时各 位 独立滚动到目标数字，
/// 位与位之间以 60ms 错开，产生 49 => 58 => 67 式的机械转子效果。
/// 非数字字符（空格、"/"等）静态显示；文本长度或数字位布局变化时直接重建（无动画）。
/// </summary>
public class AnimatedTextBlock : StackPanel
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<AnimatedTextBlock, string?>(nameof(Text));

    // 文本外观属性以 AddOwner 方式映射 TextElement 附加属性，使 XAML 可直接设置并沿视觉树生效
    public static readonly StyledProperty<FontFamily> FontFamilyProperty =
        TextElement.FontFamilyProperty.AddOwner<AnimatedTextBlock>();

    public static readonly StyledProperty<double> FontSizeProperty =
        TextElement.FontSizeProperty.AddOwner<AnimatedTextBlock>();

    public static readonly StyledProperty<FontWeight> FontWeightProperty =
        TextElement.FontWeightProperty.AddOwner<AnimatedTextBlock>();

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<AnimatedTextBlock>();

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public FontFamily FontFamily
    {
        get => GetValue(FontFamilyProperty);
        set => SetValue(FontFamilyProperty, value);
    }

    public double FontSize
    {
        get => GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    public FontWeight FontWeight
    {
        get => GetValue(FontWeightProperty);
        set => SetValue(FontWeightProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    private sealed class Reel
    {
        public StackPanel Strip = null!;       // 3 组 0-9 数字带
        public TranslateTransform Translate = null!;
        public CancellationTokenSource? Cts;
        public int Digit;                      // 当前定格数字
    }

    private readonly List<Reel> _reels = new();
    private readonly List<TextBlock> _statics = new();

    public AnimatedTextBlock()
    {
        Orientation = global::Avalonia.Layout.Orientation.Horizontal;
        AttachedToVisualTree += OnAttachedCalibrate;
    }

    // Win11 标准缓动
    private static readonly SplineEasing Ease = new(0.1, 0.9, 0.2, 1);

    static AnimatedTextBlock()
    {
        TextProperty.Changed.AddClassHandler<AnimatedTextBlock>((c, e) =>
        {
            var old = e.OldValue as string;
            var val = e.NewValue as string ?? "";

            // 初次绑定、长度变化或数字位布局变化：直接重建
            if (old is null || val.Length != old.Length || !SameDigitLayout(old, val))
            {
                c.Build(val);
                return;
            }
            c.AnimateTo(old, val);
        });
    }

    // 整数行高：避免各滚轮 Y 偏移的小数部分不同导致亚像素错位
    private double LineH => (int)Math.Round(TextElement.GetFontSize(this) * 1.45);

    private static bool SameDigitLayout(string a, string b)
    {
        for (var i = 0; i < a.Length; i++)
        {
            if (char.IsDigit(a[i]) != char.IsDigit(b[i])) return false;
        }
        return true;
    }

    private TextBlock MakeDigitText(char ch, double lh)
    {
        return new TextBlock
        {
            Text = ch.ToString(),
            LineHeight = lh,
            FontFamily = FontFamily,
            FontSize = FontSize,
            FontWeight = FontWeight,
            Foreground = Foreground,
        };
    }

    // ── 步长校准：布局舍入（29 DIP × 1.5 DPI → 29.333 DIP/格）会让固定步长 29 逐格漂移，
    //    数字越大偏得越多（"8" 下沉 5px）。布局完成后用实测格高替换近似步长，格距与步长严格一致。
    private double _stepH;

    private void OnAttachedCalibrate(object? sender, VisualTreeAttachmentEventArgs e)
    {
        LayoutUpdated -= OnFirstLayout;
        LayoutUpdated += OnFirstLayout;
    }

    private void OnFirstLayout(object? sender, EventArgs e)
    {
        if (_reels.Count == 0) return; // 数字带尚未 Build：等 Build 触发的下次布局
        if (Calibrate()) LayoutUpdated -= OnFirstLayout;
    }

    /// <summary>用第一格数字的实测布局高度校准滚轮步长并重新定位；未完成布局返回 false。</summary>
    private bool Calibrate()
    {
        if (_reels.Count == 0) return true;
        var h = _reels[0].Strip.Children.Count > 0 ? _reels[0].Strip.Children[0].Bounds.Height : 0;
        if (h <= 0) return false;
        if (Math.Abs(_stepH - h) < 0.001) return true;
        _stepH = h;
        foreach (var r in _reels)
            r.Translate.Y = -(10 + r.Digit) * h;
        return true;
    }

    /// <summary>按文本构建滚轮；每个数字位为裁剪视口内可滚动的 0-9 数字带（3 组防回绕越界）。</summary>
    private void Build(string text)
    {
        Children.Clear();
        _reels.Clear();
        _statics.Clear();

        var lh = LineH;
        if (_stepH <= 0) _stepH = lh; // 仅首次用近似值；已校准的实测步长跨 Build 保留

        foreach (var ch in text)
        {
            if (char.IsDigit(ch))
            {
                var strip = new StackPanel();
                for (var copy = 0; copy < 3; copy++)
                {
                    for (var d = 0; d <= 9; d++)
                    {
                        strip.Children.Add(MakeDigitText((char)('0' + d), lh));
                    }
                }

                var digit = ch - '0';
                var translate = new TranslateTransform { Y = -(10 + digit) * _stepH };
                strip.RenderTransform = translate;

                var viewport = new Border
                {
                    Child = strip,
                    ClipToBounds = true,
                    Height = lh,
                };
                Children.Add(viewport);

                _reels.Add(new Reel
                {
                    Strip = strip,
                    Translate = translate,
                    Digit = digit,
                });
            }
            else
            {
                var tb = MakeDigitText(ch, lh);
                Children.Add(tb);
                _statics.Add(tb);
            }
        }
    }

    /// <summary>逐位滚动：每位走 mod-10 最短路径，位间延迟 60ms，时长 450ms。</summary>
    private void AnimateTo(string oldText, string newText)
    {
        var lh = _stepH;
        var delayStep = TimeSpan.FromMilliseconds(60);
        var staticIndex = 0;

        for (var i = 0; i < newText.Length; i++)
        {
            if (!char.IsDigit(newText[i]))
            {
                // 非数字位直接更新静态字符
                if (staticIndex < _statics.Count)
                {
                    _statics[staticIndex].Text = newText[i].ToString();
                }
                staticIndex++;
                continue;
            }

            var reel = _reels[ReelIndexOf(i, oldText)];
            var target = newText[i] - '0';
            if (target == reel.Digit) continue;

            // mod-10 最短方向：如 9 → 7 反向滚 2 格
            var delta = (target - reel.Digit + 20) % 10;
            if (delta > 5) delta -= 10;

            PlayReel(reel, target, delta, lh, delayStep * i);
        }
    }

    /// <summary>第 i 个字符对应第几个滚轮（其前面共有多少个数字位）。</summary>
    private static int ReelIndexOf(int charIndex, string oldText)
    {
        var count = 0;
        for (var i = 0; i < charIndex; i++)
        {
            if (char.IsDigit(oldText[i])) count++;
        }
        return count;
    }

    private void PlayReel(Reel reel, int target, int delta, double lh, TimeSpan delay)
    {
        reel.Cts?.Cancel();
        reel.Cts = new CancellationTokenSource();

        var fromY = reel.Translate.Y;
        var toY = fromY - delta * lh;

        var anim = new Animation
        {
            Duration = TimeSpan.FromMilliseconds(450),
            Delay = delay,
            Easing = Ease,
            // Forward：结束后保持在最终帧，避免回弹到局部旧值造成闪动
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame
                {
                    Cue = new Cue(0.0),
                    Setters = { new Setter(TranslateTransform.YProperty, fromY) },
                },
                new KeyFrame
                {
                    Cue = new Cue(1.0),
                    Setters = { new Setter(TranslateTransform.YProperty, toY) },
                },
            },
        };

        var cts = reel.Cts;
        var ui = TaskScheduler.FromCurrentSynchronizationContext();
        _ = anim.RunAsync(reel.Strip, cts.Token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            // 定格并重定基到中组（视觉不变），避免多次变化后越出数字带；
            // local 值与动画终值一致（整数 lh），无跳变
            reel.Digit = target;
            reel.Translate.Y = -(10 + target) * lh;
        }, ui);
    }
}
