using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using System.Security.AccessControl;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Xml.Linq;

namespace FastApp.ViewModels
{
    public partial class AppItemModel : ObservableObject
    {
        [ObservableProperty]
        private int _id;

        [ObservableProperty]
        private string _name;

        [ObservableProperty]
        private string _executablePath;

        // NEW: Action Hooks Data
        [ObservableProperty] private int _actionType; // 0=Launch, 1=Mute, 2=Center, 3=Paste

        // FIX: Added = string.Empty; so it never passes 'null' to the database!
        [ObservableProperty] private string _actionPayload = string.Empty;

        // Smart UI toggles so the settings change based on the drop-down box
        // ActionType stays an int because that is the stored column and the
        // ComboBox binds to it arithmetically; this just names the values so the
        // decisions below read as something other than magic numbers.
        [NotMapped] public Services.HotkeyAction Action => (Services.HotkeyAction)ActionType;

        [NotMapped] public Visibility ShowAppPath => Action == Services.HotkeyAction.LaunchApp ? Visibility.Visible : Visibility.Collapsed;
        [NotMapped] public Visibility ShowTextPayload => Action == Services.HotkeyAction.PasteText ? Visibility.Visible : Visibility.Collapsed;

        [NotMapped] public Visibility ShowAutoStartToggle => Action == Services.HotkeyAction.LaunchApp ? Visibility.Visible : Visibility.Collapsed;

        [ObservableProperty] private string _category = "Other"; // Default to Other

        // Add this inside your AppItemModel class
        private int _orderIndex;
        public int OrderIndex
        {
            get => _orderIndex;
            set => SetProperty(ref _orderIndex, value);
        }

        // NEW: Custom Display Name
        [ObservableProperty] private string _customName = string.Empty;

        // UI Toggles: Defines whether this is an App (0) or an Action (1+)
        [NotMapped] public bool IsApp => Action == Services.HotkeyAction.LaunchApp;
        [NotMapped] public bool IsAction => Action != Services.HotkeyAction.LaunchApp;

        [NotMapped] public Visibility AppSettingsVisibility => IsApp ? Visibility.Visible : Visibility.Collapsed;
        [NotMapped] public Visibility ActionSettingsVisibility => IsAction ? Visibility.Visible : Visibility.Collapsed;

        // Custom Name Formatting
        [NotMapped] public Visibility SubtitleVisibility => string.IsNullOrWhiteSpace(CustomName) ? Visibility.Collapsed : Visibility.Visible;
        [NotMapped] public string DisplayNamePrimary => string.IsNullOrWhiteSpace(CustomName) ? Name : CustomName.ToUpper();

        // Translates ActionType (1,2,3) to ComboBox Index (0,1,2) to hide the "Launch App" option
        [NotMapped]
        public int ActionSelectionIndex
        {
            get => ActionType > 0 ? ActionType - 1 : 0;
            set
            {
                ActionType = value + 1;
                OnPropertyChanged(nameof(ActionSelectionIndex));
                OnPropertyChanged(nameof(ShowTextPayload));
            }
        }

        // Force the UI to refresh the visual headers when the Custom Name is typed
        partial void OnCustomNameChanged(string value)
        {
            OnPropertyChanged(nameof(SubtitleVisibility));
            OnPropertyChanged(nameof(DisplayNamePrimary));
        }

        // Force UI update when ActionType changes
        partial void OnActionTypeChanged(int value)
        {
            OnPropertyChanged(nameof(Action));
            OnPropertyChanged(nameof(ShowAppPath));
            OnPropertyChanged(nameof(ShowTextPayload));
            OnPropertyChanged(nameof(ShowAutoStartToggle));
        }

        [ObservableProperty]
        private bool _launchOnStartup;

        // Passed to the process on auto-launch and on a Launch-App hotkey, so a
        // managed entry can open a specific profile, workspace or file rather
        // than only the bare executable.
        [ObservableProperty] private string _launchArguments = string.Empty;

        // When set, the hotkey is swallowed and never reaches the focused app.
        // Off by default so existing bindings keep behaving as they always have.
        [ObservableProperty] private bool _suppressHotkeyPassthrough;

        // Seconds to wait before starting this one during an auto-launch pass.
        // Some apps refuse to start, or start wrong, if a dependency is not up
        // yet -- a game launcher before its client, a tool before its VPN.
        [ObservableProperty] private int _launchDelaySeconds;

        // Text-box friendly wrapper: an empty box means zero rather than
        // refusing the edit, matching how DailyLimitText already behaves.
        [NotMapped]
        public string LaunchDelayText
        {
            get => LaunchDelaySeconds == 0 ? "" : LaunchDelaySeconds.ToString();
            set
            {
                if (string.IsNullOrWhiteSpace(value)) LaunchDelaySeconds = 0;
                else if (int.TryParse(value, out int parsed)) LaunchDelaySeconds = Math.Max(0, parsed);
                OnPropertyChanged(nameof(LaunchDelayText));
            }
        }

        [ObservableProperty]
        private TimeSpan _timeRunning;       

        // What the user sees on screen (e.g., "Ctrl + Shift + V")
        [ObservableProperty]
        private string _hotkeyDisplayText = "None";   

        [ObservableProperty]
        private int _hotkeyTriggerCount;

        // Stores the exact sequence of keys required (e.g., "LeftCtrl,P,A,D")
        [ObservableProperty]
        private string _hotkeySequence = string.Empty;

        [ObservableProperty] private int _dailyLimitMinutes;
        [ObservableProperty] private bool _strictFocusMode;

        // NEW: A smart text wrapper that perfectly handles empty boxes, letters, and zeroes
        [NotMapped]
        public string DailyLimitText
        {
            get => DailyLimitMinutes == 0 ? "" : DailyLimitMinutes.ToString();
            set
            {
                // 1. If they delete the text completely, safely save it as 0
                if (string.IsNullOrWhiteSpace(value))
                {
                    DailyLimitMinutes = 0;
                }
                // 2. If they type a valid number, save it
                else if (int.TryParse(value, out int result))
                {
                    DailyLimitMinutes = result;
                }

                // 3. Force the UI to refresh (this instantly erases accidental letters!)
                OnPropertyChanged(nameof(DailyLimitText));
            }
        }

        // Persisted as the day each was last shown, following BonusMinutesDate:
        // a stamp that is not today reads as "not yet", so the day rolls over on
        // its own. These used to be in-memory booleans, which meant restarting
        // FastApp re-armed them and showed the same warning again the same day.
        [ObservableProperty] private DateTime? _limitNotifiedDate;
        [ObservableProperty] private DateTime? _limitWarnedDate;

        [NotMapped]
        public bool HasNotifiedToday
        {
            get => LimitNotifiedDate?.Date == DateTime.Today;
            set => LimitNotifiedDate = value ? DateTime.Today : null;
        }

        // "Nearing limit" warning fires once per day, separately from the
        // "limit reached" notification above.
        [NotMapped]
        public bool HasWarnedToday
        {
            get => LimitWarnedDate?.Date == DateTime.Today;
            set => LimitWarnedDate = value ? DateTime.Today : null;
        }

        // A same-day-only bonus granted via the dashboard's PIN-gated extension.
        // Persisted (not [NotMapped]) so the web dashboard — which reads the SQLite
        // file directly, not the live WPF process — can see and display it too.
        // BonusMinutesDate makes it self-expiring: if it isn't today, the bonus is
        // stale and reads as zero, no explicit daily-reset event required.
        [ObservableProperty] private int _todayBonusMinutes;
        [ObservableProperty] private DateTime? _bonusMinutesDate;

        public AppItemModel() { }

        // Constructor to easily create new items
        public AppItemModel(string name, string path, bool launchOnStartup = false)
        {
            Name = name;
            ExecutablePath = path;
            LaunchOnStartup = launchOnStartup;
            TimeRunning = TimeSpan.Zero;
        }
    }
}
