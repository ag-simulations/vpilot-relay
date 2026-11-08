using System;
using System.ComponentModel;
using System.Windows.Media.Animation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace VpilotRelaySetup
{
    /// <summary>Rotating busy indicator; its look is in App.xaml.</summary>
    public class Spinner : Control
    {
        public override void OnApplyTemplate()
        {
            base.OnApplyTemplate();
            if (GetTemplateChild("Arc") is UIElement arc)
            {
                var rotate = new RotateTransform();
                arc.RenderTransform = rotate;
                rotate.BeginAnimation(RotateTransform.AngleProperty,
                    new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9)) { RepeatBehavior = RepeatBehavior.Forever });
            }
        }
    }

    /// <summary>One entry of the sidebar step list.</summary>
    public sealed class StepItem : INotifyPropertyChanged
    {
        private string _state = "pending";

        public int Number { get; set; }
        public string Title { get; set; }
        public bool IsLast { get; set; }

        /// <summary>"pending", "current" or "done".</summary>
        public string State
        {
            get => _state;
            set
            {
                if (_state == value) return;
                _state = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(State)));
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }

    public enum StatusKind { Ok, Warn, Error, Info, Busy, Pending }

    /// <summary>Small building blocks for status rows and message boxes.</summary>
    internal static class Ui
    {
        public static readonly Brush Ink = Hex("#0F172A");
        public static readonly Brush Body = Hex("#475569");

        private static Brush Hex(string hex)
        {
            var b = (Brush)new BrushConverter().ConvertFromString(hex);
            b.Freeze();
            return b;
        }

        public static (Brush Soft, Brush Strong, string Glyph) Palette(StatusKind kind)
        {
            switch (kind)
            {
                case StatusKind.Ok: return (Hex("#DCFCE7"), Hex("#16A34A"), "");
                case StatusKind.Warn: return (Hex("#FEF3C7"), Hex("#D97706"), "");
                case StatusKind.Error: return (Hex("#FEE2E2"), Hex("#DC2626"), "");
                case StatusKind.Info: return (Hex("#E3EFF7"), Hex("#2B7AB0"), "");
                case StatusKind.Busy: return (Hex("#E3EFF7"), Hex("#2B7AB0"), null);
                default: return (Hex("#F1F5F9"), Hex("#94A3B8"), null);
            }
        }

        /// <summary>Round status icon (check, warning, error, spinner, or an empty dot for pending).</summary>
        public static FrameworkElement StatusIcon(StatusKind kind, double size = 30)
        {
            if (kind == StatusKind.Busy)
                return new Spinner { Width = size * 0.72, Height = size * 0.72, Margin = new Thickness(size * 0.14) };

            var p = Palette(kind);
            var circle = new Border
            {
                Width = size,
                Height = size,
                CornerRadius = new CornerRadius(size / 2),
                Background = p.Soft,
            };
            if (p.Glyph != null)
            {
                circle.Child = new TextBlock
                {
                    Text = p.Glyph,
                    FontFamily = (FontFamily)Application.Current.FindResource("IconFont"),
                    FontSize = size * 0.42,
                    Foreground = p.Strong,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                };
            }
            else
            {
                circle.Child = new Border
                {
                    Width = size * 0.27,
                    Height = size * 0.27,
                    CornerRadius = new CornerRadius(size),
                    Background = p.Strong,
                    Opacity = 0.6,
                };
            }
            return circle;
        }

        /// <summary>Fills a "status box": a soft-colored Border with an icon slot, a title and a detail line.</summary>
        public static void SetStatusBox(Border box, ContentControl icon, TextBlock title, TextBlock detail,
            StatusKind kind, string titleText, string detailText = null)
        {
            box.Visibility = Visibility.Visible;
            box.Background = kind == StatusKind.Busy || kind == StatusKind.Pending ? Hex("#F8FAFC") : Palette(kind).Soft;
            icon.Content = StatusIcon(kind, 28);
            title.Text = titleText;
            detail.Text = detailText ?? "";
            detail.Visibility = string.IsNullOrEmpty(detailText) ? Visibility.Collapsed : Visibility.Visible;
        }
    }
}
