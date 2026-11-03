using System.Diagnostics;

namespace AutoFly;

public partial class AutoflyMain
{
    private static string LanguagePreferencePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoFly", "language.txt");

    private static void LoadLanguagePreference()
    {
        Resources_DE.SetLanguage("de");
        try
        {
            string language = File.ReadAllText(LanguagePreferencePath).Trim();
            if (language is "de" or "en")
                Resources_DE.SetLanguage(language);
        }
        catch (FileNotFoundException)
        {
            // Beim ersten Start ist noch keine Spracheinstellung vorhanden.
        }
        catch (DirectoryNotFoundException)
        {
            // Das benutzerspezifische Verzeichnis entsteht erst beim Speichern.
        }
        catch (IOException ex)
        {
            LogLanguagePreferenceError("Language_SettingsReadFailed", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            LogLanguagePreferenceError("Language_SettingsReadFailed", ex);
        }
    }

    private static void SaveLanguagePreference()
    {
        try
        {
            string directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoFly");
            Directory.CreateDirectory(directory);
            File.WriteAllText(LanguagePreferencePath, Resources_DE.Language);
        }
        catch (IOException ex)
        {
            LogLanguagePreferenceError("Language_SettingsWriteFailed", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            LogLanguagePreferenceError("Language_SettingsWriteFailed", ex);
        }
    }

    private static void LogLanguagePreferenceError(string key, Exception exception) =>
        Debug.WriteLine($"[Debug] {DateTime.Now:yyyy-MM-dd HH:mm:ss} - {Resources_DE.Format(key, exception.Message)}");

    private async void TsbGerman_Click(object? sender, EventArgs e)
    {
        try
        {
            await ChangeLanguageAsync("de");
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            LogWaypointError(ex);
        }
        catch (InvalidOperationException ex)
        {
            LogWaypointError(ex);
        }
    }

    private async void TsbEnglish_Click(object? sender, EventArgs e)
    {
        try
        {
            await ChangeLanguageAsync("en");
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            LogWaypointError(ex);
        }
        catch (InvalidOperationException ex)
        {
            LogWaypointError(ex);
        }
    }

    private async Task ChangeLanguageAsync(string language)
    {
        if (Resources_DE.Language == language)
            return;

        string[] statusKeys =
        [
            "MainForm_Ready", "MainForm_Starting", "MainForm_Connecting",
            "MainForm_WaitingForConnection", "MainForm_SimulatorNotRunning",
            "MainForm_Connected", "MainForm_Disconnecting"
        ];
        string? statusKey = statusKeys.FirstOrDefault(key => statusLabel.Text == Resources_DE.GetString(key));
        Resources_DE.SetLanguage(language);
        ApplyLanguage();
        if (statusKey != null)
            statusLabel.Text = Resources_DE.GetString(statusKey);

        await UpdateWaypointLanguageAsync();
    }

    private void ApplyLanguage()
    {
        toolStripTop.SuspendLayout();
        toolStripBottom.SuspendLayout();
        try
        {
            Text = Resources_DE.GetString("MainForm_Title");
            toolStripTop.Text = Resources_DE.GetString("MainForm_TopToolbar");
            toolStripBottom.Text = Resources_DE.GetString("MainForm_BottomToolbar");
            (ToolStripItem Item, string TextKey, string? ToolTipKey)[] bindings =
            [
                (tslPlaneCenter, "MainForm_AircraftPosition", null),
                (TsbPlaneCenter, "MainForm_CenterMap", null),
                (tslFlightPlan, "MainForm_FlightPlan", null),
                (TsbFollow, "MainForm_Follow", "MainForm_FollowToolTip"),
                (tsbClear, "MainForm_Clear", "MainForm_ClearToolTip"),
                (tsbReset, "MainForm_Reset", "MainForm_ResetToolTip"),
                (tsbSave, "MainForm_Save", "MainForm_SaveToolTip"),
                (TsbLoad, "MainForm_Load", "MainForm_LoadToolTip"),
                (tsbGerman, "MainForm_German", "MainForm_German"),
                (tsbEnglish, "MainForm_English", "MainForm_English"),
                (tsbInfo, "MainForm_Information", "MainForm_InformationToolTip"),
                (tsbAP, "MainForm_Autopilot", "MainForm_AutopilotToolTip"),
                (tslAltitude, "MainForm_TargetAltitude", null),
                (GetCurrALT, "MainForm_UseCurrentValue", "MainForm_CurrentAltitudeToolTip"),
                (tsbALT, "MainForm_AltitudeControl", "MainForm_AltitudeControlToolTip"),
                (TsbATT, "MainForm_AttitudeControl", "MainForm_AttitudeControlToolTip"),
                (tslHeading, "MainForm_TargetHeading", null),
                (GetCurrHDG, "MainForm_UseCurrentValue", "MainForm_CurrentHeadingToolTip"),
                (tsbHDG, "MainForm_HeadingControl", "MainForm_HeadingControlToolTip"),
                (TsbLVL, "MainForm_LevelControl", "MainForm_LevelControlToolTip"),
                (tsbAutoThrottle, "MainForm_AutoThrottle", "MainForm_AutoThrottleToolTip"),
                (TsbAutoElevation, "MainForm_AutoElevation", "MainForm_AutoElevationToolTip"),
                (tsbTakeOff, tsbTakeOff.Checked ? "Autostart.AbortStart" : "Autostart.TakeoffName", "MainForm_TakeoffToolTip"),
                (tsbAutoland, tsbAutoland.Checked ? "AutoLand.Abort" : "AutoLand.Land", "AutoLand.Description")
            ];
            foreach ((ToolStripItem item, string textKey, string? toolTipKey) in bindings)
            {
                item.Text = Resources_DE.GetString(textKey);
                item.AccessibleName = item.Text;
                if (toolTipKey != null)
                {
                    item.ToolTipText = Resources_DE.GetString(toolTipKey);
                    item.AccessibleDescription = item.ToolTipText;
                }
            }
            tstbAltitude.ToolTipText = Resources_DE.GetString("MainForm_AltitudeToolTip");
            tstbHeading.ToolTipText = Resources_DE.GetString("MainForm_HeadingToolTip");
            tsbAutoThrottle.AccessibleName = Resources_DE.GetString("MainForm_AutoThrottleName");
            tsbAutoThrottle.AccessibleDescription = Resources_DE.GetString("MainForm_AutoThrottleDescription");
            autoStartDialog.ApplyLanguage();
            tsbGerman.Checked = Resources_DE.Language == "de";
            tsbEnglish.Checked = Resources_DE.Language == "en";
        }
        finally
        {
            toolStripBottom.ResumeLayout(true);
            toolStripTop.ResumeLayout(true);
        }
    }
}
