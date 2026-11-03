using System.Globalization;

namespace AutoFly;

internal sealed partial class AutoStartDialog : UserControl
{
    private readonly (AutostartCheck Check, Label Name, Label Status)[] rows;
    private string passedText = GetText("Passed");

    internal AutoStartDialog()
    {
        InitializeComponent();
        rows =
        [
            (AutostartCheck.OnGround, groundNameLabel, groundStatusLabel),
            (AutostartCheck.CurrentTelemetry, telemetryNameLabel, telemetryStatusLabel),
            (AutostartCheck.EnginesRunning, enginesNameLabel, enginesStatusLabel),
            (AutostartCheck.TakeoffProfile, profileNameLabel, profileStatusLabel),
            (AutostartCheck.RunwayPosition, positionNameLabel, positionStatusLabel),
            (AutostartCheck.RunwayHeading, headingNameLabel, headingStatusLabel)
        ];
        ApplyLanguage();
        ActiveControl = acknowledgeButton;
    }

    internal void ApplyLanguage()
    {
        SetLabel(titleLabel, GetText("BlockedTitle"));
        AccessibleName = titleLabel.Text;
        AccessibleDescription = GetText("BlockedDescription");
        foreach (var row in rows)
        {
            bool passed = row.Status.Text == passedText;
            SetLabel(row.Name, GetText($"Check_{row.Check}"));
            SetLabel(row.Status, GetText(passed ? "Passed" : "Failed"));
        }
        passedText = GetText("Passed");
        informationTextBox.AccessibleName = GetText("BlockedInformationTitle");
        informationTextBox.AccessibleDescription = AccessibleDescription;
        acknowledgeButton.Text = GetText("Acknowledge");
        acknowledgeButton.AccessibleName = acknowledgeButton.Text;
        acknowledgeButton.AccessibleDescription = GetText("AcknowledgeDescription");
    }

    internal event EventHandler? Acknowledged;

    internal void UpdateStatus(AutostartEvaluation evaluation, string? blockingReason, string airportInformation)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        List<string> details = [];
        if (!string.IsNullOrWhiteSpace(blockingReason))
            details.Add(blockingReason);
        foreach (var row in rows)
        {
            AutostartCheckResult? result = evaluation.Checks.FirstOrDefault(check => check.Check == row.Check);
            SetLabel(row.Status, GetText(result?.Passed == true ? "Passed" : "Failed"));
            if (result?.Passed != true)
            {
                string reason = result == null ? GetText("Pending") : string.Join(Environment.NewLine,
                    result.Details.Select(detail => string.Format(CultureInfo.CurrentCulture,
                        GetText("DetailFormat"), detail.Field, GetText($"Detail_{detail.Code}"))));
                details.Add(Resources_DE.Format("Autostart_CheckReasonFormat", row.Name.Text, reason));
            }
        }
        if (!string.IsNullOrWhiteSpace(airportInformation))
        {
            details.Add(string.Empty);
            details.Add(GetText("AirportInformationTitle"));
            details.Add(airportInformation);
        }
        string text = string.Join(Environment.NewLine, details);
        if (!string.Equals(informationTextBox.Text, text, StringComparison.Ordinal))
            informationTextBox.Text = text;
    }

    private static string GetText(string key) => Resources_DE.GetString("Autostart." + key);

    private static void SetLabel(Label label, string text)
    {
        label.Text = text;
        label.UseMnemonic = false;
        label.AccessibleName = text;
        label.AccessibleDescription = text;
    }

    private void AcknowledgeButton_Click(object? sender, EventArgs e)
        => Acknowledged?.Invoke(this, EventArgs.Empty);
}
