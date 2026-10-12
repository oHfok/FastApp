using System;
using System.IO;
using System.Windows;
using FastApp.Services;
using WC = System.Windows.Controls;
using WM = System.Windows.Media;

namespace FastApp
{
    /// <summary>
    /// The two moments the recovery key matters to a person:
    ///
    ///   - <see cref="ShowKey"/>: once, after the database is first encrypted, to hand them
    ///     the key. It is not dismissed as "seen" until they say they have saved it.
    ///   - <see cref="AskForKey"/>: at launch, when the data here was encrypted somewhere
    ///     else (a new PC, a reset Windows account) and only they can unlock it.
    ///
    /// Built in code with its own small palette rather than XAML and theme resources: it
    /// can be the very first window of a launch, before the rest of the app exists, and
    /// it must not be able to fail on a missing resource.
    /// </summary>
    public sealed class RecoveryKeyWindow : Window
    {
        public enum Answer { Closed, Unlocked, StartFresh, Quit }

        private readonly bool _light;
        private Answer _answer = Answer.Closed;

        private WM.Brush Bg => Hex(_light ? "#FFFFFF" : "#14171E");
        private WM.Brush Field => Hex(_light ? "#F1F3F6" : "#1A1E27");
        private WM.Brush Line => Hex(_light ? "#CDD3DC" : "#2F3542");
        private WM.Brush Ink => Hex(_light ? "#101828" : "#EEF0F4");
        private WM.Brush Dim => Hex(_light ? "#475467" : "#A8B0BE");
        private WM.Brush Danger => Hex(_light ? "#D92D3A" : "#FF7A82");
        private static WM.Brush Accent => Hex("#3D5AFE");

        private RecoveryKeyWindow(bool light)
        {
            _light = light;
            Title = "FastApp";
            Width = 540;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Topmost = true;
            Background = Bg;
            FontFamily = new WM.FontFamily("Segoe UI");
            FontSize = 14;
            UseLayoutRounding = true;
        }

        // ---------------------------------------------------------------- showing the key

        /// <summary>Shows the key. Returns true once the user has confirmed they saved it.</summary>
        public static bool ShowKey(string recoveryKey, bool light)
        {
            var window = new RecoveryKeyWindow(light);
            window.BuildShow(recoveryKey);
            window.ShowDialog();
            return window._answer == Answer.Unlocked;
        }

        internal void BuildShow(string recoveryKey)
        {
            var saved = new WC.CheckBox
            {
                Content = "I have saved this key somewhere other than this PC",
                Foreground = Ink,
                Margin = new Thickness(0, 18, 0, 0)
            };
            var done = MakeButton("Done", primary: true);
            done.IsEnabled = false;
            saved.Checked += (s, e) => done.IsEnabled = true;
            saved.Unchecked += (s, e) => done.IsEnabled = false;
            done.Click += (s, e) => { _answer = Answer.Unlocked; Close(); };

            var copy = MakeButton("Copy");
            copy.Click += (s, e) =>
            {
                try { System.Windows.Clipboard.SetText(recoveryKey); copy.Content = "Copied"; } catch { copy.Content = "Copy failed"; }
            };
            var save = MakeButton("Save to a file…");
            save.Click += (s, e) => SaveToFile(recoveryKey);

            Content = Page(
                Heading("Save your recovery key"),
                Body("FastApp now encrypts your tracking data and its backups. Windows keeps the key for your account, " +
                     "so nothing changes day to day and you will not be asked for it."),
                Body("If you move to a new PC, reinstall Windows, or restore a backup somewhere else, this key is the " +
                     "only way to open your data. FastApp cannot recover it for you."),
                KeyBox(recoveryKey, readOnly: true),
                Row(copy, save),
                saved,
                Row(System.Windows.HorizontalAlignment.Right, done));
        }

        private void SaveToFile(string recoveryKey)
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                FileName = "FastApp recovery key.txt",
                Filter = "Text file (*.txt)|*.txt",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            };
            if (dialog.ShowDialog(this) != true) return;
            try
            {
                File.WriteAllText(dialog.FileName,
                    "FastApp recovery key" + Environment.NewLine + Environment.NewLine +
                    recoveryKey + Environment.NewLine + Environment.NewLine +
                    "Keep this somewhere other than the PC FastApp runs on (a password manager, a USB stick, a printout)." + Environment.NewLine +
                    "You need it to open your FastApp data or backups on a new PC or after reinstalling Windows." + Environment.NewLine +
                    "Anyone who has both this key and a copy of your FastApp data can read it." + Environment.NewLine);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(this, "The file could not be saved: " + ex.Message, "FastApp",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // ---------------------------------------------------------------- asking for the key

        /// <summary>Asks for the key until it works, or the user picks another way out.</summary>
        public static Answer AskForKey(bool light)
        {
            var window = new RecoveryKeyWindow(light);
            window.BuildAsk();
            window.ShowDialog();
            return window._answer == Answer.Closed ? Answer.Quit : window._answer;
        }

        internal void BuildAsk()
        {
            var input = KeyBox(string.Empty, readOnly: false);
            var problem = new WC.TextBlock
            {
                Foreground = Danger,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 0),
                Visibility = Visibility.Collapsed
            };

            var unlock = MakeButton("Unlock", primary: true);
            unlock.IsDefault = true;
            unlock.Click += (s, e) =>
            {
                if (DbEncryption.TryUnlockWithRecoveryKey(((WC.TextBox)input.Child).Text))
                {
                    _answer = Answer.Unlocked;
                    Close();
                    return;
                }
                problem.Text = "That key does not open this data. Check it against what you saved: it is eleven groups of five characters.";
                problem.Visibility = Visibility.Visible;
            };

            var fresh = MakeButton("Start fresh instead");
            fresh.Click += (s, e) =>
            {
                var sure = System.Windows.MessageBox.Show(this,
                    "FastApp will start with empty tracking data.\n\n" +
                    "The locked data is not deleted. It is set aside in the FastAppData folder, and you can still open it " +
                    "later if you find the recovery key.",
                    "Start fresh?", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
                if (sure != MessageBoxResult.OK) return;
                _answer = Answer.StartFresh;
                Close();
            };

            var quit = MakeButton("Quit FastApp");
            quit.Click += (s, e) => { _answer = Answer.Quit; Close(); };

            Content = Page(
                Heading("Enter your recovery key"),
                Body("The FastApp data on this PC was encrypted on another PC or under another Windows account, " +
                     "so Windows does not have the key for it here."),
                Body("Enter the recovery key you saved when FastApp first showed it to you. If you were never shown one, " +
                     "the file may be damaged instead: \"Start fresh\" sets it aside without deleting it."),
                input,
                problem,
                Row(System.Windows.HorizontalAlignment.Right, quit, fresh, unlock));

            Loaded += (s, e) => ((WC.TextBox)input.Child).Focus();
        }

        // ---------------------------------------------------------------- pieces

        private static WM.Brush Hex(string hex)
        {
            var brush = new WM.SolidColorBrush((WM.Color)WM.ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }

        private WC.StackPanel Page(params UIElement[] children)
        {
            var panel = new WC.StackPanel { Margin = new Thickness(28, 24, 28, 24) };
            foreach (var child in children) panel.Children.Add(child);
            return panel;
        }

        private WC.TextBlock Heading(string text) => new()
        {
            Text = text, FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = Ink, Margin = new Thickness(0, 0, 0, 10)
        };

        private WC.TextBlock Body(string text) => new()
        {
            Text = text, Foreground = Dim, TextWrapping = TextWrapping.Wrap, LineHeight = 21, Margin = new Thickness(0, 0, 0, 10)
        };

        private WC.Border KeyBox(string text, bool readOnly) => new()
        {
            Background = Field,
            BorderBrush = Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 6, 0, 0),
            Child = new WC.TextBox
            {
                Text = text,
                IsReadOnly = readOnly,
                TextWrapping = TextWrapping.Wrap,
                AcceptsReturn = false,
                BorderThickness = new Thickness(0),
                Background = WM.Brushes.Transparent,
                Foreground = Ink,
                CaretBrush = Ink,
                FontFamily = new WM.FontFamily("Cascadia Mono, Consolas"),
                FontSize = 15,
                MinHeight = readOnly ? 0 : 44
            }
        };

        private static WC.StackPanel Row(params UIElement[] children) => Row(System.Windows.HorizontalAlignment.Left, children);

        private static WC.StackPanel Row(System.Windows.HorizontalAlignment align, params UIElement[] children)
        {
            var row = new WC.StackPanel
            {
                Orientation = WC.Orientation.Horizontal,
                HorizontalAlignment = align,
                Margin = new Thickness(0, 14, 0, 0)
            };
            for (int i = 0; i < children.Length; i++)
            {
                if (i > 0 && children[i] is FrameworkElement element) element.Margin = new Thickness(8, 0, 0, 0);
                row.Children.Add(children[i]);
            }
            return row;
        }

        private WC.Button MakeButton(string text, bool primary = false)
        {
            // A plain rounded rectangle: the stock WPF button chrome looks like another decade.
            var template = (WC.ControlTemplate)System.Windows.Markup.XamlReader.Parse(
                "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='Button'>" +
                "<Border x:Name='b' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' Background='{TemplateBinding Background}' " +
                "BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='1' CornerRadius='8' Padding='16,8'>" +
                "<ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/></Border>" +
                "<ControlTemplate.Triggers>" +
                "<Trigger Property='IsMouseOver' Value='True'><Setter TargetName='b' Property='Opacity' Value='0.85'/></Trigger>" +
                "<Trigger Property='IsEnabled' Value='False'><Setter TargetName='b' Property='Opacity' Value='0.4'/></Trigger>" +
                "</ControlTemplate.Triggers></ControlTemplate>");

            return new WC.Button
            {
                Content = text,
                Template = template,
                Background = primary ? Accent : Field,
                BorderBrush = primary ? Accent : Line,
                Foreground = primary ? WM.Brushes.White : Ink,
                FontWeight = FontWeights.SemiBold,
                Cursor = System.Windows.Input.Cursors.Hand,
                MinWidth = 84
            };
        }
    }
}
