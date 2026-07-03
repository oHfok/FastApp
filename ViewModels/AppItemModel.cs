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
        [NotMapped] public Visibility ShowAppPath => ActionType == 0 ? Visibility.Visible : Visibility.Collapsed;
        [NotMapped] public Visibility ShowTextPayload => ActionType == 3 ? Visibility.Visible : Visibility.Collapsed;

        [NotMapped] public Visibility ShowAutoStartToggle => ActionType == 0 ? Visibility.Visible : Visibility.Collapsed;

        [ObservableProperty] private string _category = "Other"; // Default to Other

        // NEW: Custom Display Name
        [ObservableProperty] private string _customName = string.Empty;

        // UI Toggles: Defines whether this is an App (0) or an Action (1+)
        [NotMapped] public bool IsApp => ActionType == 0;
        [NotMapped] public bool IsAction => ActionType > 0;

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
            OnPropertyChanged(nameof(ShowAppPath));
            OnPropertyChanged(nameof(ShowTextPayload));
            OnPropertyChanged(nameof(ShowAutoStartToggle));
        }

        [ObservableProperty]
        private bool _launchOnStartup;

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

        [NotMapped]
        public bool HasNotifiedToday { get; set; }

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
