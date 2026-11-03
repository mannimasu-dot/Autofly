using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.FlightSimulator.SimConnect;

namespace AutoFly;

/// <summary>Reine Eingabewerte für die einmalige Fahrwerksanforderung eines Autostarts.</summary>
public sealed record AutostartGearRetractionInput(
    bool AirborneObserved, bool AlreadyRequested, double AboveGroundFeet, double IsGearRetractable);

/// <summary>Eine Anforderung, keine Bestätigung eines tatsächlich eingefahrenen Fahrwerks.</summary>
public sealed record AutostartGearRetractionOutput(bool RequestGearUp);

// Pomax' Rollregler: gemessene Ruderposition plus Kursfehler und Fehleränderung.
// Lesen in Prozent, Schreiben von RUDDER POSITION in percent over 100; keine Lenk-Events.
public static class AutostartControlOutput
{
    public static AutostartGearRetractionOutput CalculateGearRetraction(AutostartGearRetractionInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return new(input.AirborneObserved && !input.AlreadyRequested &&
            double.IsFinite(input.AboveGroundFeet) && input.AboveGroundFeet >= 100.0 &&
            input.IsGearRetractable == 1.0);
    }

    /// <summary>Erlaubt einen Start ausschließlich mit einem gültigen, höchstens zwei Sekunden alten Bodennachweis.</summary>
    public static bool IsFreshGroundSample(double onGround, DateTime sampleUtc, DateTime nowUtc) =>
        onGround == 1.0 && nowUtc.Kind == DateTimeKind.Utc && sampleUtc.Kind == DateTimeKind.Utc &&
        sampleUtc != default && sampleUtc <= nowUtc && (nowUtc - sampleUtc).TotalSeconds <= 2.0;

    public static double CalculateRudderPosition(double measuredRudderPercent, double headingErrorDegrees,
        double? previousHeadingErrorDegrees, double elapsedSeconds, bool enabled)
    {
        if (!enabled || !double.IsFinite(measuredRudderPercent) || Math.Abs(measuredRudderPercent) > 100.0 ||
            !double.IsFinite(headingErrorDegrees) ||
            (previousHeadingErrorDegrees.HasValue && !double.IsFinite(previousHeadingErrorDegrees.Value)) ||
            !double.IsFinite(elapsedSeconds) || elapsedSeconds <= 0.0)
            return 0.0;

        // Bei 100 ms genau Pomax: clamp(hDiff/30)*0.05 + clamp(dHeading)*0.1.
        // Erstes Sample ohne künstlichen Differenzsprung; Zeitbezug für Timer-Jitter.
        double elapsed = Math.Min(elapsedSeconds, 0.25);
        double delta = previousHeadingErrorDegrees.HasValue
            ? Math.IEEERemainder(headingErrorDegrees - previousHeadingErrorDegrees.Value, 360.0) : 0.0;
        double update = Math.Clamp(headingErrorDegrees / 30.0, -1.0, 1.0) * 0.05 +
            Math.Clamp(delta * 0.1 / elapsed, -1.0, 1.0) * 0.1;
        return Math.Clamp(measuredRudderPercent / 100.0 + update * elapsed / 0.1, -1.0, 1.0);
    }

    public static double CalculateThrottle(double measuredThrottlePercent, double elapsedSeconds, bool enabled)
    {
        if (!enabled || !double.IsFinite(measuredThrottlePercent) || measuredThrottlePercent < 0.0 ||
            measuredThrottlePercent > 100.0 || !double.IsFinite(elapsedSeconds) || elapsedSeconds <= 0.0)
            return 0.0;

        // Pomax: ein Prozentpunkt je 100 ms, bezogen auf den jeweiligen Motor-Istwert.
        return Math.Min(100.0, measuredThrottlePercent + 10.0 * Math.Min(elapsedSeconds, 0.25));
    }

    public static double RudderPositionForSend(double position, bool controlFailed)
    {
        return controlFailed || !double.IsFinite(position) ? 0.0 : Math.Clamp(position, -1.0, 1.0);
    }

    public static uint EncodeBrake(bool fullBrakes, bool release, bool controlFailed)
    {
        // Ein Übertragungsfehler hat auch vor einer angeforderten Freigabe Vorrang.
        return unchecked((uint)(controlFailed || (!release && fullBrakes) ? 16383 : -16383));
    }

    public static (int AltitudeFeet, int MagneticHeadingDegrees) CalculateHandoverTargets(
        double altitudeFeet, double runwayTrueDegrees, double trueHeadingDegrees, double magneticHeadingDegrees,
        double? targetAltitudeFeet = null)
    {
        if (!double.IsFinite(altitudeFeet))
            throw new ArgumentOutOfRangeException(nameof(altitudeFeet));
        double targetAltitude = targetAltitudeFeet ?? altitudeFeet + 500.0;
        if (!double.IsFinite(targetAltitude) || targetAltitude < int.MinValue || targetAltitude > int.MaxValue)
            throw new ArgumentOutOfRangeException(targetAltitudeFeet.HasValue ? nameof(targetAltitudeFeet) : nameof(altitudeFeet));
        if (!double.IsFinite(runwayTrueDegrees) || runwayTrueDegrees < 0 || runwayTrueDegrees >= 360)
            throw new ArgumentOutOfRangeException(nameof(runwayTrueDegrees));
        if (!double.IsFinite(trueHeadingDegrees) || trueHeadingDegrees < 0 || trueHeadingDegrees >= 360)
            throw new ArgumentOutOfRangeException(nameof(trueHeadingDegrees));
        if (!double.IsFinite(magneticHeadingDegrees) || magneticHeadingDegrees < 0 || magneticHeadingDegrees >= 360)
            throw new ArgumentOutOfRangeException(nameof(magneticHeadingDegrees));
        double declination = Math.IEEERemainder(trueHeadingDegrees - magneticHeadingDegrees, 360);
        double heading = (runwayTrueDegrees - declination + 360) % 360;
        return (checked((int)Math.Round(targetAltitude, MidpointRounding.AwayFromZero)),
            checked((int)Math.Round(heading, MidpointRounding.AwayFromZero)) % 360);
    }
}

/// <summary>Flugzustände der isoliert prüfbaren Startsteuerung, ohne Simulatorzugriff.</summary>
public enum AutostartFlightStage { WaitingForVr, Rotation, InitialClimb, ReadyForHandover, ManualTakeover }

/// <summary>Ein vollständiges, zusammengehöriges Paket; Pitch ist wie in SimConnect Nase-hoch negativ.</summary>
public sealed record AutostartFlightInput(
    uint SessionId, string AircraftTitle, DateTime SampleUtc, bool OnGround,
    double IndicatedAirspeedKnots, double RotationSpeedKnots, double ClimbSpeedKnots,
    double VerticalSpeedFeetPerMinute, double AboveGroundFeet, double PitchDegrees,
    double BankDegrees, double ElevatorPercent, bool IsAcrobatic)
{
    // Die Integration liefert beide Richtungen aus demselben Telemetriepaket bzw.
    // der verriegelten Startbahn. Ohne Richtung keine Übergabefreigabe.
    public double? TrueHeadingDegrees { get; init; }
    public double? RunwayTrueHeadingDegrees { get; init; }
    public double AileronPercent { get; init; }
}

/// <summary>Normierte Achssollwerte, keine Bestätigung ihrer Wirkung; Duplikate erlauben kein Senden.</summary>
public sealed record AutostartFlightOutput(AutostartFlightStage Stage, bool IsNewSample,
    double ElevatorPosition, double AileronPosition, bool AirborneObserved, string? FailureKey)
{
    public double SampleIntervalSeconds { get; init; }
    public double VerticalSpeedErrorFeetPerMinute { get; init; }
    public double VerticalSpeedChangeFeetPerMinutePerSecond { get; init; }
    public double ElevatorCorrection { get; init; }
    public double? HeadingErrorDegrees { get; init; }
    public double TargetBankDegrees { get; init; }
    public double RollRateDegreesPerSecond { get; init; }
    /// <summary>Querrudertrimmung normiert (percent over 100), rechts positiv.</summary>
    public double AileronTrimPosition { get; init; }
    public bool SpeedLimited { get; init; }
    public bool PitchLimited { get; init; }
}

/// <summary>Kapselt Rechenwerte und zeitgestützte Flugfreigaben für Tests ohne UI oder SimConnect.</summary>
public sealed class AutostartFlightController
{
    public const double TargetVerticalSpeedFeetPerMinute = 500.0;
    public const double CompletionAboveGroundFeet = 500.0;
    // Lokale, vorläufige Toleranzen und Dämpfungszeiten; keine ALT-/HDG-Abstimmung.
    public const double VerticalSpeedToleranceFeetPerMinute = 50.0;
    public const double HeadingToleranceDegrees = 2.0;
    private const double RateDampingSeconds = 1.0;
    private const double MaximumNoseUpDegrees = 10.0;
    private double? previousVerticalSpeed;
    private double? previousBank;
    private DateTime lastSampleUtc;
    private DateTime rotationStartedUtc;
    private uint sessionId;
    private string? aircraftTitle;
    private double rotationSpeed;
    private double climbSpeed;
    private AutostartFlightOutput output = new(AutostartFlightStage.WaitingForVr, false, 0, 0, false, null);

    /// <summary>Verarbeitet jedes neue Paket höchstens einmal; ungültige Pakete entziehen die Freigabe.</summary>
    public AutostartFlightOutput Update(AutostartFlightInput input, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (output.Stage is AutostartFlightStage.ManualTakeover or AutostartFlightStage.InitialClimb)
            return output with { IsNewSample = false };
        bool fresh = nowUtc.Kind == DateTimeKind.Utc && input.SampleUtc.Kind == DateTimeKind.Utc &&
            input.SampleUtc != default && input.SampleUtc <= nowUtc &&
            (nowUtc - input.SampleUtc).TotalSeconds <= 2.0;
        bool identity = input.SessionId != 0 && !string.IsNullOrWhiteSpace(input.AircraftTitle) &&
            (aircraftTitle == null || (sessionId == input.SessionId && aircraftTitle == input.AircraftTitle));
        // Das erste frische Abhebesignal beendet die Steuerung. Keine AGL-/VS-
        // Bedingung und keine Haltezeit: InitialClimb ist nur die Abschlussmarke.
        if (fresh && identity && input.SampleUtc > lastSampleUtc && !input.OnGround)
        {
            lastSampleUtc = input.SampleUtc;
            return output = output with
            {
                Stage = AutostartFlightStage.InitialClimb, IsNewSample = true,
                AirborneObserved = true, ElevatorPosition = 0, AileronPosition = 0,
                ElevatorCorrection = 0, FailureKey = null
            };
        }
        if (!fresh || !identity || !double.IsFinite(input.IndicatedAirspeedKnots) || input.IndicatedAirspeedKnots < 0 ||
            !double.IsFinite(input.RotationSpeedKnots) || input.RotationSpeedKnots <= 0 ||
            !double.IsFinite(input.ClimbSpeedKnots) || input.ClimbSpeedKnots < input.RotationSpeedKnots ||
            !double.IsFinite(input.VerticalSpeedFeetPerMinute) || !double.IsFinite(input.AboveGroundFeet) ||
            input.AboveGroundFeet < 0 || !double.IsFinite(input.PitchDegrees) || Math.Abs(input.PitchDegrees) > 90 ||
            !double.IsFinite(input.BankDegrees) || Math.Abs(input.BankDegrees) > 180 ||
            !double.IsFinite(input.ElevatorPercent) || Math.Abs(input.ElevatorPercent) > 100 ||
            !double.IsFinite(input.AileronPercent) || Math.Abs(input.AileronPercent) > 100 ||
            input.TrueHeadingDegrees.HasValue != input.RunwayTrueHeadingDegrees.HasValue ||
            (input.TrueHeadingDegrees.HasValue &&
                (!double.IsFinite(input.TrueHeadingDegrees.Value) || input.TrueHeadingDegrees.Value < 0 ||
                 input.TrueHeadingDegrees.Value >= 360 || !double.IsFinite(input.RunwayTrueHeadingDegrees!.Value) ||
                 input.RunwayTrueHeadingDegrees.Value < 0 || input.RunwayTrueHeadingDegrees.Value >= 360)) ||
            (aircraftTitle != null && (rotationSpeed != input.RotationSpeedKnots || climbSpeed != input.ClimbSpeedKnots)))
            return Fail("FlightTelemetryInvalid");
        if (input.SampleUtc <= lastSampleUtc)
            return output with { IsNewSample = false };

        sessionId = input.SessionId;
        aircraftTitle = input.AircraftTitle;
        rotationSpeed = input.RotationSpeedKnots;
        climbSpeed = input.ClimbSpeedKnots;
        double gap = lastSampleUtc == default ? 0.1 : (input.SampleUtc - lastSampleUtc).TotalSeconds;
        lastSampleUtc = input.SampleUtc;
        // Unveränderter Zeitbezug der Rotation, dt maximal 250 ms.
        double scale = Math.Min(gap, 0.25) / 0.1;
        bool initial = output.Stage == AutostartFlightStage.WaitingForVr;
        if (initial && input.IndicatedAirspeedKnots < rotationSpeed)
        {
            if (!input.OnGround)
                return Fail("FlightTelemetryInvalid");
            return output = output with { IsNewSample = true };
        }
        if (initial)
        {
            rotationStartedUtc = input.SampleUtc;
            output = output with { Stage = AutostartFlightStage.Rotation };
        }
        // Die bisherige Rotationszeitgrenze bleibt erhalten; kein Steigflug-Timer.
        if ((input.SampleUtc - rotationStartedUtc).TotalSeconds > 30)
            return Fail("FlightTimeout");

        double step = input.IsAcrobatic ? 0.001 : 0.005;
        // Nur zusammenhängende Samples differenzieren; nach Lücken kein Dämpfungssprung.
        bool ratesAvailable = !initial && gap >= 0.05 && gap <= 0.25;
        double verticalSpeedRate = ratesAvailable && previousVerticalSpeed.HasValue
            ? (input.VerticalSpeedFeetPerMinute - previousVerticalSpeed.Value) / gap : 0;
        double rollRate = ratesAvailable && previousBank.HasValue
            ? (input.BankDegrees - previousBank.Value) / gap : 0;
        previousVerticalSpeed = input.VerticalSpeedFeetPerMinute;
        previousBank = input.BankDegrees;
        double error = TargetVerticalSpeedFeetPerMinute - input.VerticalSpeedFeetPerMinute;
        double damping = Math.Clamp(verticalSpeedRate * RateDampingSeconds,
            -TargetVerticalSpeedFeetPerMinute, TargetVerticalSpeedFeetPerMinute);
        // Vorzeichen des Regelfehlers erhalten: stärkeres Sinken darf die
        // Hochziehkorrektur nicht auf null reduzieren. VS-Trend wirkt vorausschauend.
        double elevatorCorrection = step * Math.Clamp(
            (error - damping) / TargetVerticalSpeedFeetPerMinute, -1, 1) * scale;
        bool pitchLimited = input.PitchDegrees <= -MaximumNoseUpDegrees;
        if (pitchLimited)
        {
            // Kein Sprung auf Neutral: nur den begrenzten Korrekturschritt ändern.
            double excess = (-input.PitchDegrees - MaximumNoseUpDegrees) / MaximumNoseUpDegrees;
            elevatorCorrection = Math.Min(elevatorCorrection, -step * Math.Clamp(excess, 0, 1) * scale);
        }
        double elevator = initial ? 5 * step : input.ElevatorPercent / 100.0 + elevatorCorrection;
        if (initial && pitchLimited)
            elevator = input.ElevatorPercent / 100.0 + elevatorCorrection;
        double? headingError = input.TrueHeadingDegrees.HasValue
            ? (input.RunwayTrueHeadingDegrees!.Value - input.TrueHeadingDegrees.Value + 540.0) % 360.0 - 180.0
            : null;
        // Nur die bisherige Querneigungskorrektur am Boden. Keine Kurs- oder
        // Querruderregelung nach OnGround=false.
        double aileron = Math.Clamp(input.BankDegrees / 100.0, -0.15, 0.15);
        output = output with { IsNewSample = true, ElevatorPosition = Math.Clamp(elevator, -1, 1),
            SampleIntervalSeconds = gap, VerticalSpeedErrorFeetPerMinute = error,
            VerticalSpeedChangeFeetPerMinutePerSecond = verticalSpeedRate,
            ElevatorCorrection = elevator - input.ElevatorPercent / 100.0,
            HeadingErrorDegrees = headingError, TargetBankDegrees = 0,
            RollRateDegreesPerSecond = rollRate, SpeedLimited = false, PitchLimited = pitchLimited,
            AileronPosition = aileron, Stage = AutostartFlightStage.Rotation };
        return output;
    }

    public static bool IsClimbRateEstablished(double verticalSpeedFeetPerMinute) =>
        double.IsFinite(verticalSpeedFeetPerMinute) &&
        Math.Abs(verticalSpeedFeetPerMinute - TargetVerticalSpeedFeetPerMinute) <= VerticalSpeedToleranceFeetPerMinute;

    private AutostartFlightOutput Fail(string key) => output = output with
    {
        Stage = AutostartFlightStage.ManualTakeover, IsNewSample = false,
        ElevatorPosition = 0, AileronPosition = 0, FailureKey = key
    };
}

/// <summary>Isolierter Anfangssteigflug; lokale vorläufige Grenzen, keine ALT-/HDG-Abstimmung.</summary>
public sealed class AutostartInitialClimbController
{
    public const double HandoverAboveGroundFeet = 200.0;
    // Vorläufige lokale Grenzen, nicht aus Pomax oder den Joystick-Prozenten abgeleitet:
    // maximal +/-5 % Quertrimmung und maximal 0,5 Prozentpunkte Änderung je Sekunde.
    public const double MaximumAileronTrimPosition = 0.05;
    public const double MaximumAileronTrimChangePerSecond = 0.005;
    private bool airborneObserved;
    public string? TelemetryFailureDetails { get; private set; }
    private DateTime lastSampleUtc;
    private DateTime startedUtc;
    private uint sessionId;
    private string? aircraftTitle;
    private double? previousBank;
    private double highestAboveGroundFeet;
    private AutostartFlightOutput output = new(AutostartFlightStage.InitialClimb, false, 0, 0, true, null);

    public AutostartFlightOutput Update(AutostartFlightInput input, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (output.Stage is AutostartFlightStage.ManualTakeover or AutostartFlightStage.ReadyForHandover)
            return output with { IsNewSample = false };
        // Nach dem ersten Abhebesignal niemals wieder auf Bodenstatus zurückfallen.
        airborneObserved |= !input.OnGround;
        List<string> failures = [];
        void check(bool valid, string detail)
        {
            if (!valid) failures.Add(detail);
        }
        check(nowUtc.Kind == DateTimeKind.Utc, $"Now.Kind={nowUtc.Kind}");
        check(input.SampleUtc.Kind == DateTimeKind.Utc, $"Sample.Kind={input.SampleUtc.Kind}");
        check(input.SampleUtc != default, "SampleUtc fehlt");
        check(input.SampleUtc <= nowUtc, $"Zeitstempel in Zukunft: Sample={input.SampleUtc:O}, Now={nowUtc:O}");
        check((nowUtc - input.SampleUtc).TotalSeconds <= 2,
            $"Paket veraltet: Alter={(nowUtc - input.SampleUtc).TotalSeconds:F6} s");
        check(input.SessionId != 0, "SessionId=0");
        check(!string.IsNullOrWhiteSpace(input.AircraftTitle), "AircraftTitle fehlt");
        check(aircraftTitle == null || (input.SessionId == sessionId && input.AircraftTitle == aircraftTitle),
            $"Identität geändert: Session={input.SessionId}/{sessionId}, Title={input.AircraftTitle}/{aircraftTitle}");
        check(airborneObserved, "Noch kein OnGround=false empfangen");
        check(double.IsFinite(input.AboveGroundFeet) && input.AboveGroundFeet >= 0, $"AGL={input.AboveGroundFeet:R}");
        check(double.IsFinite(input.PitchDegrees) && Math.Abs(input.PitchDegrees) <= 90, $"Pitch={input.PitchDegrees:R}");
        check(double.IsFinite(input.BankDegrees) && Math.Abs(input.BankDegrees) <= 180, $"Bank={input.BankDegrees:R}");
        check(double.IsFinite(input.ElevatorPercent) && Math.Abs(input.ElevatorPercent) <= 100, $"Elevator={input.ElevatorPercent:R}");
        check(double.IsFinite(input.AileronPercent) && Math.Abs(input.AileronPercent) <= 100, $"Aileron={input.AileronPercent:R}");
        double heading = input.TrueHeadingDegrees ?? double.NaN;
        double runway = input.RunwayTrueHeadingDegrees ?? double.NaN;
        check(double.IsFinite(heading) && heading >= 0 && heading < 360, $"TrueHeading={heading:R}");
        check(double.IsFinite(runway) && runway >= 0 && runway < 360, $"RunwayHeading={runway:R}");
        if (failures.Count != 0)
        {
            TelemetryFailureDetails = string.Join("; ", failures);
            return Fail("FlightTelemetryInvalid");
        }
        if (input.SampleUtc <= lastSampleUtc)
            return output with { IsNewSample = false };
        bool first = lastSampleUtc == default;
        double gap = first ? 0.1 : (input.SampleUtc - lastSampleUtc).TotalSeconds;
        if (first)
        {
            startedUtc = input.SampleUtc;
            sessionId = input.SessionId;
            aircraftTitle = input.AircraftTitle;
        }
        lastSampleUtc = input.SampleUtc;
        // Lokale Überwachungsgrenze: kein unbegrenzt fortgesetzter Steigversuch.
        if ((input.SampleUtc - startedUtc).TotalSeconds > 180)
            return Fail("FlightTimeout");
        highestAboveGroundFeet = Math.Max(highestAboveGroundFeet, input.AboveGroundFeet);
        // Höhenabhängige Rampe: bis 100 ft 15 %, bei 200 ft 10 %.
        // Ein späterer Höhenverlust darf den Ausschlag nicht wieder erhöhen.
        double elevator = 0.15 - 0.05 * Math.Clamp(
            (highestAboveGroundFeet - 100.0) / (HandoverAboveGroundFeet - 100.0), 0.0, 1.0);
        if (input.AboveGroundFeet >= HandoverAboveGroundFeet)
            return output = output with { Stage = AutostartFlightStage.ReadyForHandover, IsNewSample = true,
                ElevatorPosition = 0.10,
                ElevatorCorrection = 0.10 - input.ElevatorPercent / 100.0, SampleIntervalSeconds = gap };

        bool ratesAvailable = !first && gap >= 0.05 && gap <= 0.25;
        double rollRate = ratesAvailable && previousBank.HasValue
            ? (input.BankDegrees - previousBank.Value) / gap : 0;
        previousBank = input.BankDegrees;
        double headingError = Math.IEEERemainder(runway - heading, 360);
        // SimConnect: Rechtsbank negativ; positive Querruderausgabe baut Rechtsbank auf.
        double targetBank = -Math.Clamp(headingError * 0.5, -5, 5);
        double aileron = Math.Clamp((input.BankDegrees - targetBank) * 0.02 + rollRate * 0.01, -0.15, 0.15);
        double trim = output.AileronTrimPosition;
        double bankError = input.BankDegrees - targetBank;
        // Langsame Entlastung der direkten Quersteuerung; keine Integration bei Datenlücken.
        // Auch SIM_FRAME-Abstände unter 50 ms integrieren; Rollvorhalt nur, wenn verfügbar.
        // Die 0,5-Grad-Totzone hält die Trimmung bei bereits passender Bank konstant.
        if (!first && gap > 0.0 && gap <= 0.25 && Math.Abs(bankError) > 0.5)
            trim = Math.Clamp(trim + Math.Clamp((bankError + 2.0 * rollRate) / 5.0, -1.0, 1.0) *
                MaximumAileronTrimChangePerSecond * gap, -MaximumAileronTrimPosition, MaximumAileronTrimPosition);
        return output = output with
        {
            IsNewSample = true, ElevatorPosition = elevator,
            AileronPosition = aileron, AileronTrimPosition = trim, SampleIntervalSeconds = gap,
            ElevatorCorrection = elevator - input.ElevatorPercent / 100, HeadingErrorDegrees = headingError,
            TargetBankDegrees = targetBank, RollRateDegreesPerSecond = rollRate
        };
    }

    private AutostartFlightOutput Fail(string key) => output = output with
    {
        Stage = AutostartFlightStage.ManualTakeover, IsNewSample = false, FailureKey = key,
        ElevatorPosition = 0, AileronPosition = 0, AileronTrimPosition = 0
    };
}

internal enum AutostartPhase
{
    Idle,
    Readiness,
    StartRoll,
    Braking,
    Stopped,
    Rotation,
    InitialClimb,
    Handover,
    Completed,
    Aborted
}

internal enum AutostartCheck
{
    OnGround,
    CurrentTelemetry,
    EnginesRunning,
    TakeoffProfile,
    RunwayPosition,
    RunwayHeading
}

internal enum AutostartCheckCode
{
    MissingValue,
    InvalidTimestamp,
    FutureTimestamp,
    StaleTelemetry,
    InvalidValue,
    AircraftNotOnGround,
    EngineNotRunning,
    InconsistentProfile,
    RunwayDataPending,
    RunwayDataUnavailable,
    InvalidRunwayData,
    RunwayModelMismatch,
    AircraftOutsideRunway,
    RunwayHeadingMismatch,
    PhaseNotReady,
    RunwayHeadingRequiresPosition
}

// HasValue bezeichnet einen tatsächlich empfangenen Wert, nicht nur einen initialisierten Zeitstempel.
internal readonly record struct AutostartValue<T>(T Value, DateTime LastUpdatedUtc, bool HasValue);

internal sealed record AutostartSnapshot
{
    internal uint SessionId { get; init; }
    internal AutostartValue<string?> Title { get; init; }
    internal AutostartValue<double> NumberOfEngines { get; init; }
    internal AutostartValue<double> RotationSpeedKnots { get; init; }
    internal AutostartValue<double> ClimbSpeedKnots { get; init; }
    internal AutostartValue<double> StallSpeedLandingKnots { get; init; }
    internal AutostartValue<double> StallSpeedCleanKnots { get; init; }
    internal AutostartValue<double> CruiseSpeedKnots { get; init; }
    internal AutostartValue<double> OnGround { get; init; }
    internal AutostartValue<double> IndicatedAirspeedKnots { get; init; }
    internal AutostartValue<double> GroundSpeedKnots { get; init; }
    internal AutostartValue<double> IndicatedAltitudeFeet { get; init; }
    internal AutostartValue<double> VerticalSpeedFeetPerMinute { get; init; }
    internal AutostartValue<double> PitchDegrees { get; init; }
    internal AutostartValue<double> BankDegrees { get; init; }
    internal AutostartValue<double> TrueHeadingDegrees { get; init; }
    internal AutostartValue<double> LatitudeDegrees { get; init; }
    internal AutostartValue<double> LongitudeDegrees { get; init; }
    internal AutostartValue<double> EngineCombustion1 { get; init; }
    internal AutostartValue<double> EngineCombustion2 { get; init; }
    internal AutostartValue<double> EngineCombustion3 { get; init; }
    internal AutostartValue<double> EngineCombustion4 { get; init; }
    internal AutostartValue<double> RudderPercent { get; init; }
    internal AutostartValue<double> ElevatorPercent { get; init; }
    internal AutostartValue<double> AboveGroundFeet { get; init; }
    internal AutostartValue<double> MagneticHeadingDegrees { get; init; }
    internal AutostartValue<double> AileronPercent { get; init; }
    internal AutostartValue<double> LeftBrakePosition { get; init; }
    internal AutostartValue<double> RightBrakePosition { get; init; }
    internal AutostartValue<double> IsGearRetractable { get; init; }
    internal AutostartValue<double> GearTotalPosition { get; init; }
    internal AutostartValue<double> GearCenterPosition { get; init; }
    internal AutostartValue<double> GearLeftPosition { get; init; }
    internal AutostartValue<double> GearRightPosition { get; init; }
    internal AutostartValue<double> TrailingFlapsLeft { get; init; }
    internal AutostartValue<double> TrailingFlapsRight { get; init; }
    internal AutostartValue<double> LeadingFlapsLeft { get; init; }
    internal AutostartValue<double> LeadingFlapsRight { get; init; }
    internal AutostartValue<double>[] EngineThrottles { get; init; } = [];
}

// Vorläufiges Modellprofil, keine zertifizierten oder bahnbezogenen Leistungsdaten.
internal sealed record AutostartProfile(
    string AircraftTitle,
    int EngineCount,
    double RotationSpeedKnots,
    double ClimbSpeedKnots,
    double StallSpeedLandingKnots,
    double StallSpeedCleanKnots,
    double CruiseSpeedKnots);

internal enum AutostartRunwayDataStatus { Pending, Complete, Failed, Invalid }

internal sealed record AutostartRunwayData(
    string AircraftTitle,
    uint SessionId,
    AutostartRunwayDataStatus Status,
    IReadOnlyList<AutostartRunwayGeometry> Runways);

internal sealed record AutostartRunwayGeometry(
    double LatitudeDegrees,
    double LongitudeDegrees,
    double TrueHeadingDegrees,
    double LengthMeters,
    double WidthMeters)
{
    internal bool IsValid => double.IsFinite(LatitudeDegrees) && Math.Abs(LatitudeDegrees) <= 90.0 &&
        double.IsFinite(LongitudeDegrees) && Math.Abs(LongitudeDegrees) <= 180.0 &&
        double.IsFinite(TrueHeadingDegrees) && TrueHeadingDegrees >= 0.0 && TrueHeadingDegrees <= 360.0 &&
        double.IsFinite(LengthMeters) && LengthMeters > 0.0 &&
        double.IsFinite(WidthMeters) && WidthMeters > 0.0;

    internal bool Contains(double latitudeDegrees, double longitudeDegrees)
    {
        return TryProject(latitudeDegrees, longitudeDegrees, out double along, out double across) &&
            Math.Abs(along) <= LengthMeters / 2.0 + 0.000001 &&
            Math.Abs(across) <= WidthMeters / 2.0 + 0.000001;
    }

    internal bool TryProject(double latitudeDegrees, double longitudeDegrees, out double along, out double across)
    {
        along = across = double.NaN;
        if (!IsValid || !double.IsFinite(latitudeDegrees) || Math.Abs(latitudeDegrees) > 90.0 ||
            !double.IsFinite(longitudeDegrees) || Math.Abs(longitudeDegrees) > 180.0)
            return false;

        const double radians = Math.PI / 180.0;
        const double earthRadiusMeters = 6_371_000.0;
        double latitude = latitudeDegrees * radians;
        double centerLatitude = LatitudeDegrees * radians;
        double deltaLongitude = ((longitudeDegrees - LongitudeDegrees + 540.0) % 360.0 - 180.0) * radians;
        double east = Math.Cos(latitude) * Math.Sin(deltaLongitude);
        double north = Math.Cos(centerLatitude) * Math.Sin(latitude) -
            Math.Sin(centerLatitude) * Math.Cos(latitude) * Math.Cos(deltaLongitude);
        double sineDistance = Math.Sqrt(east * east + north * north);
        double cosineDistance = Math.Sin(centerLatitude) * Math.Sin(latitude) +
            Math.Cos(centerLatitude) * Math.Cos(latitude) * Math.Cos(deltaLongitude);
        if (sineDistance < 1e-15 && cosineDistance < 0.0)
            return false;

        // Azimutale äquidistante Projektion um die Bahnmitte; kein Längengrad-Sprung am Datum.
        double scale = sineDistance < 1e-15 ? earthRadiusMeters :
            earthRadiusMeters * Math.Atan2(sineDistance, cosineDistance) / sineDistance;
        east *= scale;
        north *= scale;
        double heading = TrueHeadingDegrees * radians;
        along = north * Math.Cos(heading) + east * Math.Sin(heading);
        across = east * Math.Cos(heading) - north * Math.Sin(heading);

        return double.IsFinite(along) && double.IsFinite(across);
    }

    // Rückprojektion eines Mittellinienpunkts erhält auch die lokale wahre Richtung nahe Polen/Datumsgrenze.
    internal double BearingToCenterline(double latitudeDegrees, double longitudeDegrees, double targetAlong)
    {
        const double radians = Math.PI / 180.0;
        double distance = Math.Abs(targetAlong) / 6_371_000.0;
        double heading = (TrueHeadingDegrees + (targetAlong < 0.0 ? 180.0 : 0.0)) * radians;
        double centerLatitude = LatitudeDegrees * radians;
        double targetLatitude = Math.Asin(Math.Clamp(Math.Sin(centerLatitude) * Math.Cos(distance) +
            Math.Cos(centerLatitude) * Math.Sin(distance) * Math.Cos(heading), -1.0, 1.0));
        double targetLongitude = LongitudeDegrees * radians + Math.Atan2(
            Math.Sin(heading) * Math.Sin(distance) * Math.Cos(centerLatitude),
            Math.Cos(distance) - Math.Sin(centerLatitude) * Math.Sin(targetLatitude));
        double latitude = latitudeDegrees * radians;
        double deltaLongitude = targetLongitude - longitudeDegrees * radians;

        return (Math.Atan2(Math.Sin(deltaLongitude) * Math.Cos(targetLatitude),
            Math.Cos(latitude) * Math.Sin(targetLatitude) -
            Math.Sin(latitude) * Math.Cos(targetLatitude) * Math.Cos(deltaLongitude)) / radians + 360.0) % 360.0;
    }

    internal bool MatchesHeading(double trueHeadingDegrees, double maximumDifferenceDegrees,
        double latitudeDegrees, double longitudeDegrees)
    {
        if (!IsValid || !double.IsFinite(trueHeadingDegrees) || trueHeadingDegrees < 0.0 ||
            trueHeadingDegrees >= 360.0 || !double.IsFinite(maximumDifferenceDegrees) || maximumDifferenceDegrees < 0.0 ||
            !double.IsFinite(latitudeDegrees) || Math.Abs(latitudeDegrees) > 90.0 ||
            !double.IsFinite(longitudeDegrees) || Math.Abs(longitudeDegrees) > 180.0)
            return false;

        const double radians = Math.PI / 180.0;
        double latitude = latitudeDegrees * radians;
        double centerLatitude = LatitudeDegrees * radians;
        double deltaLongitude = ((longitudeDegrees - LongitudeDegrees + 540.0) % 360.0 - 180.0) * radians;
        double heading = trueHeadingDegrees * radians;
        double east = Math.Cos(latitude) * Math.Sin(deltaLongitude);
        double north = Math.Cos(centerLatitude) * Math.Sin(latitude) -
            Math.Sin(centerLatitude) * Math.Cos(latitude) * Math.Cos(deltaLongitude);
        double cosineDistance = Math.Sin(centerLatitude) * Math.Sin(latitude) +
            Math.Cos(centerLatitude) * Math.Cos(latitude) * Math.Cos(deltaLongitude);
        double sineDistance = Math.Sqrt(east * east + north * north);

        // Lokalen Richtungstangenten mit dem Differential derselben Projektion wie Contains abbilden.
        double eastDirection = -Math.Sin(latitude) * Math.Cos(heading) * Math.Sin(deltaLongitude) +
            Math.Sin(heading) * Math.Cos(deltaLongitude);
        double northDirection = Math.Cos(centerLatitude) * Math.Cos(latitude) * Math.Cos(heading) +
            Math.Sin(centerLatitude) * Math.Sin(latitude) * Math.Cos(heading) * Math.Cos(deltaLongitude) +
            Math.Sin(centerLatitude) * Math.Sin(heading) * Math.Sin(deltaLongitude);
        if (sineDistance < 1e-8)
        {
            if (cosineDistance < 0.0)
                return false;
        }
        else
        {
            double cosineDerivative = Math.Sin(centerLatitude) * Math.Cos(latitude) * Math.Cos(heading) -
                Math.Cos(centerLatitude) * Math.Sin(latitude) * Math.Cos(heading) * Math.Cos(deltaLongitude) -
                Math.Cos(centerLatitude) * Math.Sin(heading) * Math.Sin(deltaLongitude);
            double sineDerivative = (east * eastDirection + north * northDirection) / sineDistance;
            double distance = Math.Atan2(sineDistance, cosineDistance);
            double distanceDerivative = cosineDistance * sineDerivative - sineDistance * cosineDerivative;
            double scale = distance / sineDistance;
            double scaleDerivative = (distanceDerivative - scale * sineDerivative) / sineDistance;
            eastDirection = scale * eastDirection + east * scaleDerivative;
            northDirection = scale * northDirection + north * scaleDerivative;
        }
        double projectedHeading = Math.Atan2(eastDirection, northDirection) / radians;
        double difference = Math.Abs((projectedHeading - TrueHeadingDegrees + 540.0) % 360.0 - 180.0);

        return Math.Min(difference, 180.0 - difference) <= maximumDifferenceDegrees;
    }
}

internal sealed record AutostartDetail(AutostartCheckCode Code, string Field);

internal sealed record AutostartCheckResult(
    AutostartCheck Check,
    bool Passed,
    IReadOnlyList<AutostartDetail> Details);

internal sealed record AutostartEvaluation(
    AutostartPhase Phase,
    AutostartProfile? Profile,
    IReadOnlyList<AutostartCheckResult> Checks)
{
    internal bool IsReady => Phase == AutostartPhase.Readiness && Checks.Count == 6 &&
        Checks.Select(check => check.Check).Distinct().Count() == 6 &&
        Checks.All(check => Enum.IsDefined(check.Check) && check.Passed);
}

// Deterministische, instanzgebundene Zustandslogik ohne UI-, SimConnect- oder Aktorzugriffe.
// Aufrufe durch die Integration serialisieren; einzige Diagnoseausgabe ist Debug.WriteLine.
internal sealed class Autostart
{
    internal const double MaximumTelemetryAgeSeconds = 2.0;
    internal const double MaximumRunwayHeadingDifferenceDegrees = 3.0;
    internal const double StandstillKnots = 0.5;
    // Vorläufige Abbruchreserve, ausdrücklich keine berechnete/garantierte Anhaltestrecke.
    internal double RemainingRunwayAbortMeters { get; init; } = 200.0;

    private AutostartRunwayGeometry? activeRunway;
    private AutostartProfile? activeProfile;
    private uint activeSession;
    private int runwayDirection;
    private DateTime rollStartedUtc;
    private DateTime lastControlUtc;
    private DateTime lastRollSampleUtc;
    private DateTime? standstillSinceUtc;
    private double[] throttlePercents = [];
    private double rudderPosition;
    private double? previousHeadingError;
    private DateTime lastRollDiagnosticUtc;
    private AutostartPhase? lastRollDiagnosticPhase;
    private string? lastRollDiagnosticStopReason;
    private AutostartFlightController flightController = new();
    internal AutostartFlightOutput? FlightOutput { get; private set; }
    internal double RunwayTrueHeading { get; private set; }
    internal bool AirborneObserved { get; private set; }
    internal bool UsesFlightControl => Phase == AutostartPhase.Rotation;
    internal bool RequiresAirborneSafety(AutostartSnapshot snapshot, DateTime nowUtc) => AirborneObserved ||
        (RotationSpeedReached && (!snapshot.OnGround.HasValue || snapshot.OnGround.Value != 1.0 ||
            snapshot.OnGround.LastUpdatedUtc.Kind != DateTimeKind.Utc || snapshot.OnGround.LastUpdatedUtc > nowUtc ||
            (nowUtc - snapshot.OnGround.LastUpdatedUtc).TotalSeconds > MaximumTelemetryAgeSeconds));

    internal void ObserveAirborne(AutostartSnapshot snapshot, DateTime nowUtc)
    {
        if (!AirborneObserved && HasRoll && snapshot.SessionId == activeSession && snapshot.Title.Value == activeProfile?.AircraftTitle &&
            snapshot.OnGround.HasValue && snapshot.OnGround.Value == 0 &&
            snapshot.OnGround.LastUpdatedUtc.Kind == DateTimeKind.Utc && snapshot.OnGround.LastUpdatedUtc <= nowUtc &&
            (nowUtc - snapshot.OnGround.LastUpdatedUtc).TotalSeconds <= MaximumTelemetryAgeSeconds)
        {
            AirborneObserved = true;
            UpdateFlight(snapshot, nowUtc);
            Phase = AutostartPhase.InitialClimb;
        }
    }

    internal void Complete() => Phase = AutostartPhase.Completed;
    internal bool RotationSpeedReached { get; private set; }
    internal string? RollStopReason { get; private set; }
    internal int ActiveEngineCount => activeProfile?.EngineCount ?? 0;
    internal bool HasRoll => activeRunway != null;

    private readonly Dictionary<AutostartCheck, AutostartCheckResult> lastLoggedChecks = [];

    internal AutostartPhase Phase { get; private set; } = AutostartPhase.Idle;

    internal bool Begin()
    {
        if (Phase is not (AutostartPhase.Idle or AutostartPhase.Aborted or AutostartPhase.Completed))
            return false;

        lastLoggedChecks.Clear();
        activeRunway = null;
        activeProfile = null;
        throttlePercents = [];
        rudderPosition = 0.0;
        previousHeadingError = null;
        lastRollSampleUtc = default;
        standstillSinceUtc = null;
        lastRollDiagnosticUtc = default;
        lastRollDiagnosticPhase = null;
        lastRollDiagnosticStopReason = null;
        RotationSpeedReached = false;
        flightController = new();
        FlightOutput = null;
        AirborneObserved = false;
        RunwayTrueHeading = 0;
        RollStopReason = null;
        Phase = AutostartPhase.Readiness;
        return true;
    }

    internal void Cancel()
    {
        Phase = AutostartPhase.Aborted;
    }

    internal bool TryAdvanceToStartRoll(AutostartSnapshot snapshot, AutostartRunwayData runwayData, DateTime nowUtc,
        out AutostartEvaluation evaluation)
    {
        // Niemals eine frühere Bereitschaftsauswertung als Startfreigabe wiederverwenden.
        evaluation = Evaluate(snapshot, runwayData, nowUtc);
        if (!evaluation.IsReady)
            return false;

        activeRunway = runwayData.Runways.First(runway =>
            runway.Contains(snapshot.LatitudeDegrees.Value, snapshot.LongitudeDegrees.Value) &&
            runway.MatchesHeading(snapshot.TrueHeadingDegrees.Value, MaximumRunwayHeadingDifferenceDegrees,
                snapshot.LatitudeDegrees.Value, snapshot.LongitudeDegrees.Value));
        activeRunway.TryProject(snapshot.LatitudeDegrees.Value, snapshot.LongitudeDegrees.Value,
            out double along, out _);
        double forwardBearing = activeRunway.BearingToCenterline(snapshot.LatitudeDegrees.Value,
            snapshot.LongitudeDegrees.Value, along + 100_000.0);
        runwayDirection = Math.Abs(SignedDifference(forwardBearing, snapshot.TrueHeadingDegrees.Value)) <= 90.0 ? 1 : -1;
        RunwayTrueHeading = (forwardBearing + (runwayDirection < 0 ? 180 : 0)) % 360;
        activeProfile = evaluation.Profile;
        activeSession = snapshot.SessionId;
        throttlePercents = new double[ActiveEngineCount];
        rollStartedUtc = nowUtc;
        lastControlUtc = snapshot.OnGround.LastUpdatedUtc;
        Phase = AutostartPhase.StartRoll;
        evaluation = evaluation with { Phase = Phase };
        //Debug.WriteLine(FormattableString.Invariant($"[Debug] {nowUtc:yyyy-MM-dd HH:mm:ss} - [Autostart] Rollgeometrie: Bahnmitte={activeRunway.LatitudeDegrees:F8}/{activeRunway.LongitudeDegrees:F8}, BahnTrue={activeRunway.TrueHeadingDegrees:F3} deg, Länge={activeRunway.LengthMeters:F1} m, Breite={activeRunway.WidthMeters:F1} m, Richtung={runwayDirection}, VR={activeProfile?.RotationSpeedKnots:F2} kt"));
        return true;
    }

    internal void Brake(string reason)
    {
        RollStopReason ??= reason;
        if (AirborneObserved)
            return;
        if (HasRoll && Phase != AutostartPhase.Stopped)
            Phase = AutostartPhase.Braking;
        Array.Clear(throttlePercents);
        rudderPosition = 0.0;
    }

    internal (double[] Throttles, bool FullBrakes, double RudderPosition) UpdateRoll(
        AutostartSnapshot snapshot, AutostartRunwayData runwayData, DateTime nowUtc)
    {
        if (activeRunway == null || activeProfile == null || !double.IsFinite(RemainingRunwayAbortMeters) ||
            RemainingRunwayAbortMeters <= 0.0)
            throw new InvalidOperationException();
        ObserveAirborne(snapshot, nowUtc);
        if (AirborneObserved)
            return ([], false, 0);
        // Die Rollauswertung ist nicht auf die Startfreigabe beschränkt, sondern kann auch während des Rollvorgangs erfolgen.
        AutostartEvaluation current = Evaluate(snapshot, runwayData, nowUtc);
        bool flight = UsesFlightControl || AirborneObserved;
        bool valid = snapshot.SessionId == activeSession && current.Profile == activeProfile &&
            current.Checks.Where(check => check.Check is not
                (AutostartCheck.RunwayHeading or AutostartCheck.EnginesRunning) &&
                (!flight || check.Check is not (AutostartCheck.OnGround or AutostartCheck.RunwayPosition)))
                .All(check => check.Passed);
        List<AutostartDetail> groundDetails = [];
        valid &= ValidateNumber(snapshot.OnGround, "SIM ON GROUND", nowUtc, true,
            value => value is 0.0 or 1.0, groundDetails);
        if (flight && (!valid || current.Checks.Any(check => check.Check == AutostartCheck.EnginesRunning && !check.Passed)))
        {
            if (RequiresAirborneSafety(snapshot, nowUtc))
            {
                RollStopReason ??= "FlightTelemetryInvalid";
                return ([], false, 0);
            }
            Brake("FlightTelemetryInvalid");
        }
        List<AutostartDetail> details = [];
        bool groundSpeedValid = ValidateNumber(snapshot.GroundSpeedKnots, "GROUND VELOCITY", nowUtc, true,
            value => value >= 0.0, details);
        valid &= groundSpeedValid;
        bool standstillValid = groundSpeedValid && snapshot.SessionId == activeSession &&
            ValidateNumber(snapshot.OnGround, "SIM ON GROUND", nowUtc, true, value => value == 1.0, details);
        bool projected = activeRunway.TryProject(snapshot.LatitudeDegrees.Value, snapshot.LongitudeDegrees.Value,
            out double along, out double across);
        double remaining = activeRunway.LengthMeters / 2.0 - runwayDirection * along;
        // Pomax' ferner Mittellinien-Zielpunkt, aber auf der echten erkannten Bahnachse,
        // nicht auf einer aus Flugzeugposition und Anfangskurs erfundenen Startlinie.
        const double lookAhead = 1000.0;
        double desiredHeading = activeRunway.BearingToCenterline(snapshot.LatitudeDegrees.Value,
            snapshot.LongitudeDegrees.Value, along + runwayDirection * lookAhead);
        double localRunwayHeading = activeRunway.BearingToCenterline(snapshot.LatitudeDegrees.Value,
            snapshot.LongitudeDegrees.Value, along + runwayDirection * 100_000.0);
        double headingError = SignedDifference(desiredHeading, snapshot.TrueHeadingDegrees.Value);
        bool tracking = valid && projected && activeRunway.Contains(snapshot.LatitudeDegrees.Value,
            snapshot.LongitudeDegrees.Value) && Math.Abs(across) <= activeRunway.WidthMeters * 0.4 &&
            Math.Abs(SignedDifference(localRunwayHeading, snapshot.TrueHeadingDegrees.Value)) <= 20.0 &&
            double.IsFinite(headingError);
        if (current.Checks.Any(check => check.Check == AutostartCheck.EnginesRunning && !check.Passed))
            Brake("EngineFailure");
        if (!tracking)
            Brake("RollTelemetryOrTracking");
        else if (Phase is AutostartPhase.StartRoll or AutostartPhase.Rotation)
        {
            // Reserve hat Vorrang vor VR: am Bahnende niemals einen erfolgreichen Versuch melden.
            if (remaining <= RemainingRunwayAbortMeters)
                Brake("RunwayReserve");
            else if ((nowUtc - rollStartedUtc).TotalSeconds > 120.0)
                Brake("RollTimeout");
            else if (snapshot.IndicatedAirspeedKnots.Value >= activeProfile.RotationSpeedKnots)
            {
                RotationSpeedReached = true;
                Phase = AutostartPhase.Rotation;
            }
        }
        if (Phase is AutostartPhase.Braking or AutostartPhase.Stopped)
        {
            if (standstillValid && snapshot.GroundSpeedKnots.Value <= StandstillKnots)
            {
                standstillSinceUtc ??= snapshot.GroundSpeedKnots.LastUpdatedUtc;
                if ((snapshot.GroundSpeedKnots.LastUpdatedUtc - standstillSinceUtc.Value).TotalSeconds >= 1.0)
                    Phase = AutostartPhase.Stopped;
            }
            else
            {
                standstillSinceUtc = null;
                Phase = AutostartPhase.Braking;
            }
        }
        bool rudderEnabled = tracking && Phase != AutostartPhase.Stopped && RollStopReason == null;
        // Ein erneut verwendetes Telemetriepaket darf keinen weiteren Regelschritt erzeugen.
        if (tracking && snapshot.TrueHeadingDegrees.LastUpdatedUtc > lastRollSampleUtc)
        {
            double elapsed = Math.Clamp((snapshot.OnGround.LastUpdatedUtc - lastControlUtc).TotalSeconds, 0.0, 0.25);
            rudderPosition = AutostartControlOutput.CalculateRudderPosition(snapshot.RudderPercent.Value,
                headingError, previousHeadingError, elapsed, rudderEnabled);
            for (int engine = 0; engine < throttlePercents.Length; engine++)
                throttlePercents[engine] = AutostartControlOutput.CalculateThrottle(
                    snapshot.EngineThrottles[engine].Value, elapsed,
                    Phase is AutostartPhase.StartRoll or AutostartPhase.Rotation);
            previousHeadingError = headingError;
            lastRollSampleUtc = snapshot.TrueHeadingDegrees.LastUpdatedUtc;
            lastControlUtc = snapshot.OnGround.LastUpdatedUtc;
        }
        if (Phase is AutostartPhase.Braking or AutostartPhase.Stopped)
            Array.Clear(throttlePercents);
        if (!rudderEnabled)
            rudderPosition = 0.0;

        // Sollwerte, keine Bestätigung der Simulatorwirkung.
        // Phasen-/Abbruchwechsel sofort, sonst höchstens vier Diagnosezeilen pro Sekunde.
        if (lastRollDiagnosticUtc == default || lastRollDiagnosticPhase != Phase ||
            lastRollDiagnosticStopReason != RollStopReason ||
            (nowUtc - lastRollDiagnosticUtc).TotalSeconds >= 0.25)
        {
            bool inside = activeRunway.Contains(snapshot.LatitudeDegrees.Value, snapshot.LongitudeDegrees.Value);
            bool lateralWithinLimit = Math.Abs(across) <= activeRunway.WidthMeters * 0.4;
            double runwayHeadingError = SignedDifference(localRunwayHeading, snapshot.TrueHeadingDegrees.Value);
            string throttles = string.Join("/", throttlePercents.Select(value => value.ToString("F2", CultureInfo.InvariantCulture)));
            //Debug.WriteLine(FormattableString.Invariant($"[Debug] {nowUtc:yyyy-MM-dd HH:mm:ss} - [Autostart] RollSoll: t={(nowUtc - rollStartedUtc).TotalSeconds:F3} s, SampleUTC={snapshot.TrueHeadingDegrees.LastUpdatedUtc:O}, Phase={Phase}, Stop={RollStopReason ?? "-"}, längs={along:F3} m, quer={across:F3} m, Rest={remaining:F1} m, LookAhead={lookAhead:F1} m, TrueIst={snapshot.TrueHeadingDegrees.Value:F3} deg, TrueSoll={desiredHeading:F3} deg, BahnLokal={localRunwayHeading:F3} deg, Kursfehler={headingError:F3} deg, Bahnfehler={runwayHeadingError:F3} deg, IAS={snapshot.IndicatedAirspeedKnots.Value:F2} kt, GS={snapshot.GroundSpeedKnots.Value:F2} kt, SchubSoll={throttles} %, VollbremsSoll={Phase != AutostartPhase.StartRoll}, RuderIst={snapshot.RudderPercent.Value:F4} %, RUDDER_POSITION_Soll={rudderPosition:F6}, DatenGültig={valid}, Projektion={projected}, InnerhalbBahn={inside}, QuerLimit={lateralWithinLimit}, RichtungLimit={Math.Abs(runwayHeadingError) <= 20.0}, KursfehlerGültig={double.IsFinite(headingError)}, Tracking={tracking}"));
            lastRollDiagnosticUtc = nowUtc;
            //Debug.WriteLine(FormattableString.Invariant($"[Debug] {nowUtc:yyyy-MM-dd HH:mm:ss} - [Autostart] Phase={Phase}, IAS={snapshot.IndicatedAirspeedKnots.Value:F2} kt, VR={activeProfile.RotationSpeedKnots:F2} kt, Rest={remaining:F1} m, Quer={across:F2} m, RudderSoll={rudderPosition:F6}, ThrottleSoll={throttles}, Tracking={tracking}, Stop={RollStopReason ?? "-"}"));
            lastRollDiagnosticPhase = Phase;
            lastRollDiagnosticStopReason = RollStopReason;
        }

        if (Phase == AutostartPhase.Rotation)
            UpdateFlight(snapshot, nowUtc);
        return ((double[])throttlePercents.Clone(), Phase is AutostartPhase.Braking or AutostartPhase.Stopped, rudderPosition);
    }

    private void UpdateFlight(AutostartSnapshot snapshot, DateTime nowUtc)
    {
        // Gleiche vorläufige Titelzuordnung wie im vorhandenen HDG-Regler; kein neues Modellflag erfinden.
        FlightOutput = flightController.Update(new(snapshot.SessionId, snapshot.Title.Value ?? string.Empty,
            snapshot.OnGround.LastUpdatedUtc, snapshot.OnGround.Value == 1,
            snapshot.IndicatedAirspeedKnots.Value, snapshot.RotationSpeedKnots.Value, snapshot.ClimbSpeedKnots.Value,
            snapshot.VerticalSpeedFeetPerMinute.Value, snapshot.AboveGroundFeet.Value, snapshot.PitchDegrees.Value,
            snapshot.BankDegrees.Value, snapshot.ElevatorPercent.Value,
            activeProfile?.AircraftTitle.Contains("Pitts", StringComparison.OrdinalIgnoreCase) == true ||
            activeProfile?.AircraftTitle.Contains("Gee Bee", StringComparison.OrdinalIgnoreCase) == true)
            {
                TrueHeadingDegrees = snapshot.TrueHeadingDegrees.Value,
                RunwayTrueHeadingDegrees = RunwayTrueHeading,
                AileronPercent = snapshot.AileronPercent.Value
            }, nowUtc);
        AirborneObserved |= FlightOutput.AirborneObserved;
        if (FlightOutput.FailureKey != null)
        {
            if (AirborneObserved)
                RollStopReason ??= FlightOutput.FailureKey;
            else
                Brake(FlightOutput.FailureKey);
        }
        else
            Phase = FlightOutput.Stage switch
            {
                AutostartFlightStage.InitialClimb => AutostartPhase.InitialClimb,
                _ => AutostartPhase.Rotation
            };
    }

    private static double SignedDifference(double target, double current) =>
        (target - current + 540.0) % 360.0 - 180.0;

    internal AutostartEvaluation Evaluate(AutostartSnapshot snapshot, AutostartRunwayData runwayData, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(runwayData);
        if (nowUtc == default || nowUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException(null, nameof(nowUtc));

        List<AutostartDetail> ground = [];
        if (ValidateNumber(snapshot.OnGround, "SIM ON GROUND", nowUtc, true,
            value => value is 0.0 or 1.0, ground) && snapshot.OnGround.Value != 1.0)
            ground.Add(new(AutostartCheckCode.AircraftNotOnGround, "SIM ON GROUND"));

        List<AutostartDetail> telemetry = [];
        if (!double.IsFinite(RemainingRunwayAbortMeters) || RemainingRunwayAbortMeters <= 0.0)
            telemetry.Add(new(AutostartCheckCode.InvalidValue, "AUTOFLY_AUTOSTART_RUNWAY_RESERVE_METERS"));
        ValidateNumber(snapshot.IndicatedAirspeedKnots, "AIRSPEED INDICATED", nowUtc, true,
            value => value >= 0.0, telemetry);
        ValidateNumber(snapshot.GroundSpeedKnots, "GROUND VELOCITY", nowUtc, true,
            value => value >= 0.0 && (Phase != AutostartPhase.Readiness || value <= 1.0), telemetry);
        ValidateNumber(snapshot.IndicatedAltitudeFeet, "INDICATED ALTITUDE", nowUtc, true, _ => true, telemetry);
        ValidateNumber(snapshot.VerticalSpeedFeetPerMinute, "VERTICAL SPEED", nowUtc, true, _ => true, telemetry);
        ValidateNumber(snapshot.PitchDegrees, "PLANE PITCH DEGREES", nowUtc, true,
            value => value >= -90.0 && value <= 90.0, telemetry);
        ValidateNumber(snapshot.BankDegrees, "PLANE BANK DEGREES", nowUtc, true,
            value => value >= -180.0 && value <= 180.0, telemetry);
        ValidateNumber(snapshot.RudderPercent, "RUDDER POSITION", nowUtc, true,
            value => value >= -100.0 && value <= 100.0, telemetry);
        ValidateNumber(snapshot.ElevatorPercent, "ELEVATOR POSITION", nowUtc, true,
            value => Math.Abs(value) <= 100, telemetry);
        ValidateNumber(snapshot.AboveGroundFeet, "PLANE ALT ABOVE GROUND", nowUtc, true, value => value >= 0, telemetry);
        ValidateNumber(snapshot.MagneticHeadingDegrees, "PLANE HEADING DEGREES MAGNETIC", nowUtc, true,
            value => value >= 0 && value < 360, telemetry);
        ValidateNumber(snapshot.AileronPercent, "AILERON POSITION", nowUtc, true, value => Math.Abs(value) <= 100, telemetry);
        ValidateNumber(snapshot.LeftBrakePosition, "BRAKE LEFT POSITION", nowUtc, true, value => value >= 0 && value <= 1, telemetry);
        ValidateNumber(snapshot.RightBrakePosition, "BRAKE RIGHT POSITION", nowUtc, true, value => value >= 0 && value <= 1, telemetry);
        bool headingValid = ValidateNumber(snapshot.TrueHeadingDegrees, "PLANE HEADING DEGREES TRUE", nowUtc, true,
            value => value >= 0.0 && value < 360.0, telemetry);
        bool latitudeValid = ValidateNumber(snapshot.LatitudeDegrees, "PLANE LATITUDE", nowUtc, true,
            value => value >= -90.0 && value <= 90.0, telemetry);
        bool longitudeValid = ValidateNumber(snapshot.LongitudeDegrees, "PLANE LONGITUDE", nowUtc, true,
            value => value >= -180.0 && value <= 180.0, telemetry);

        List<AutostartDetail> model = [];
        bool titleValid = ValidateSample(snapshot.Title, "TITLE", nowUtc, false, model);
        if (titleValid && string.IsNullOrWhiteSpace(snapshot.Title.Value))
        {
            titleValid = false;
            model.Add(new(AutostartCheckCode.InvalidValue, "TITLE"));
        }
        bool engineCountValid = ValidateNumber(snapshot.NumberOfEngines, "NUMBER OF ENGINES", nowUtc, false,
            value => value >= 1.0 && value <= 4.0 && value == Math.Truncate(value), model);
        if (engineCountValid)
        {
            for (int engine = 0; engine < (int)snapshot.NumberOfEngines.Value; engine++)
            {
                AutostartValue<double> throttle = engine < snapshot.EngineThrottles.Length
                    ? snapshot.EngineThrottles[engine] : default;
                ValidateNumber(throttle, $"GENERAL ENG THROTTLE LEVER POSITION:{engine + 1}", nowUtc, true,
                    value => value >= 0.0 && value <= 100.0, telemetry);
            }
        }
        ValidateNumber(snapshot.RotationSpeedKnots, "DESIGN SPEED MIN ROTATION", nowUtc, false, value => value > 0.0, model);
        ValidateNumber(snapshot.ClimbSpeedKnots, "DESIGN SPEED CLIMB", nowUtc, false, value => value > 0.0, model);
        ValidateNumber(snapshot.StallSpeedLandingKnots, "DESIGN SPEED VS0", nowUtc, false, value => value > 0.0, model);
        ValidateNumber(snapshot.StallSpeedCleanKnots, "DESIGN SPEED VS1", nowUtc, false, value => value > 0.0, model);
        ValidateNumber(snapshot.CruiseSpeedKnots, "DESIGN SPEED VC", nowUtc, false, value => value > 0.0, model);

        AutostartProfile? profile = null;
        if (model.Count == 0)
        {
            double vs0 = snapshot.StallSpeedLandingKnots.Value;
            double vs1 = snapshot.StallSpeedCleanKnots.Value;
            double vr = snapshot.RotationSpeedKnots.Value;
            double vy = snapshot.ClimbSpeedKnots.Value;
            double vc = snapshot.CruiseSpeedKnots.Value;
            // Vr kann mit Startklappen unter VS1 liegen; kein erfundener Sicherheitsfaktor.
            if (vs0 > vs1 || vr <= vs0 || vr > vy || vy <= vs1 || vy > vc)
                model.Add(new(AutostartCheckCode.InconsistentProfile, "VS0/VS1/VR/VY/VC"));
            else
                profile = new(snapshot.Title.Value!, (int)snapshot.NumberOfEngines.Value, vr, vy, vs0, vs1, vc);
            if (Phase == AutostartPhase.Readiness && snapshot.IndicatedAirspeedKnots.Value >= vr)
                telemetry.Add(new(AutostartCheckCode.InvalidValue, "AIRSPEED INDICATED"));
        }

        List<AutostartDetail> engines = [];
        if (!engineCountValid)
            engines.AddRange(model.Where(detail => detail.Field == "NUMBER OF ENGINES"));
        else
        {
            for (int engine = 1; engine <= (int)snapshot.NumberOfEngines.Value; engine++)
            {
                AutostartValue<double> combustion = engine switch
                {
                    1 => snapshot.EngineCombustion1,
                    2 => snapshot.EngineCombustion2,
                    3 => snapshot.EngineCombustion3,
                    _ => snapshot.EngineCombustion4
                };
                string field = $"ENG COMBUSTION:{engine}";
                if (ValidateNumber(combustion, field, nowUtc, true, value => value is 0.0 or 1.0, engines) &&
                    combustion.Value != 1.0)
                    engines.Add(new(AutostartCheckCode.EngineNotRunning, field));
            }
        }

        List<AutostartDetail> position = [];
        List<AutostartDetail> heading = [];
        AutostartCheckCode? runwayError = runwayData.Status switch
        {
            AutostartRunwayDataStatus.Pending => AutostartCheckCode.RunwayDataPending,
            AutostartRunwayDataStatus.Failed => AutostartCheckCode.RunwayDataUnavailable,
            AutostartRunwayDataStatus.Invalid => AutostartCheckCode.InvalidRunwayData,
            AutostartRunwayDataStatus.Complete when runwayData.Runways is null => AutostartCheckCode.InvalidRunwayData,
            AutostartRunwayDataStatus.Complete when runwayData.Runways.Count == 0 => AutostartCheckCode.RunwayDataUnavailable,
            AutostartRunwayDataStatus.Complete when runwayData.Runways.Any(runway => runway is null || !runway.IsValid)
                => AutostartCheckCode.InvalidRunwayData,
            AutostartRunwayDataStatus.Complete => null,
            _ => AutostartCheckCode.InvalidRunwayData
        };
        if (runwayError == null && (!titleValid || snapshot.SessionId == 0 ||
            snapshot.SessionId != runwayData.SessionId ||
            !string.Equals(snapshot.Title.Value, runwayData.AircraftTitle, StringComparison.Ordinal)))
            runwayError = AutostartCheckCode.RunwayModelMismatch;
        if (runwayError.HasValue)
        {
            position.Add(new(runwayError.Value, "RUNWAY_DATA"));
            heading.Add(new(runwayError.Value, "RUNWAY_DATA"));
        }
        else if (!latitudeValid || !longitudeValid)
        {
            position.AddRange(telemetry.Where(detail => detail.Field is "PLANE LATITUDE" or "PLANE LONGITUDE"));
            heading.AddRange(position);
        }
        else
        {
            AutostartRunwayGeometry[] containing = [.. runwayData.Runways!.Where(runway =>
                runway.Contains(snapshot.LatitudeDegrees.Value, snapshot.LongitudeDegrees.Value))];
            if (containing.Length == 0)
            {
                position.Add(new(AutostartCheckCode.AircraftOutsideRunway, "RUNWAY_POSITION"));
                heading.Add(new(AutostartCheckCode.RunwayHeadingRequiresPosition, "RUNWAY_TRUE_HEADING"));
            }
            else if (headingValid && !containing.Any(runway => runway.MatchesHeading(
                snapshot.TrueHeadingDegrees.Value,
                Phase == AutostartPhase.Readiness ? MaximumRunwayHeadingDifferenceDegrees : 20.0,
                snapshot.LatitudeDegrees.Value, snapshot.LongitudeDegrees.Value)))
                heading.Add(new(AutostartCheckCode.RunwayHeadingMismatch, "RUNWAY_TRUE_HEADING"));
        }
        if (!headingValid)
            heading.AddRange(telemetry.Where(detail => detail.Field == "PLANE HEADING DEGREES TRUE"));
        if (Phase is not (AutostartPhase.Readiness or AutostartPhase.StartRoll or AutostartPhase.Braking or AutostartPhase.Stopped or
            AutostartPhase.Rotation or AutostartPhase.InitialClimb or AutostartPhase.Handover))
            heading.Add(new(AutostartCheckCode.PhaseNotReady, "PHASE"));

        AutostartCheckResult[] checks =
        [
            CreateResult(AutostartCheck.OnGround, ground, nowUtc),
            CreateResult(AutostartCheck.CurrentTelemetry, telemetry, nowUtc),
            CreateResult(AutostartCheck.EnginesRunning, engines, nowUtc),
            CreateResult(AutostartCheck.TakeoffProfile, model, nowUtc),
            CreateResult(AutostartCheck.RunwayPosition, position, nowUtc),
            CreateResult(AutostartCheck.RunwayHeading, heading, nowUtc)
        ];
        return new(Phase, profile, Array.AsReadOnly(checks));
    }

    private static bool ValidateSample<T>(AutostartValue<T> sample, string field, DateTime nowUtc,
        bool requireFresh, List<AutostartDetail> details)
    {
        AutostartCheckCode? code = !sample.HasValue || sample.Value is null ? AutostartCheckCode.MissingValue
            : sample.LastUpdatedUtc == default || sample.LastUpdatedUtc.Kind != DateTimeKind.Utc ? AutostartCheckCode.InvalidTimestamp
            : sample.LastUpdatedUtc > nowUtc ? AutostartCheckCode.FutureTimestamp
            : requireFresh && (nowUtc - sample.LastUpdatedUtc).TotalSeconds > MaximumTelemetryAgeSeconds ? AutostartCheckCode.StaleTelemetry
            : null;
        if (code.HasValue)
            details.Add(new(code.Value, field));

        return !code.HasValue;
    }

    private static bool ValidateNumber(AutostartValue<double> sample, string field, DateTime nowUtc,
        bool requireFresh, Func<double, bool> validRange, List<AutostartDetail> details)
    {
        if (!ValidateSample(sample, field, nowUtc, requireFresh, details))
            return false;
        if (!double.IsFinite(sample.Value) || !validRange(sample.Value))
        {
            details.Add(new(AutostartCheckCode.InvalidValue, field));
            return false;
        }

        return true;
    }

    private AutostartCheckResult CreateResult(AutostartCheck check, List<AutostartDetail> details, DateTime nowUtc)
    {
        AutostartCheckResult result = new(check, details.Count == 0, Array.AsReadOnly(details.ToArray()));
        // Ab Phase 4 sind Boden-/Bahnprüfungen keine anwendbaren Flugdiagnosen.
        // Das Prüfergebnis bleibt erhalten; nur seine Logausgabe wird gesperrt.
        if ((Phase is AutostartPhase.InitialClimb or AutostartPhase.Handover) &&
            (check is AutostartCheck.OnGround or AutostartCheck.RunwayPosition or AutostartCheck.RunwayHeading))
            return result;

        if (lastLoggedChecks.TryGetValue(check, out AutostartCheckResult? previous) &&
            previous.Passed == result.Passed && previous.Details.SequenceEqual(result.Details))
            return result;

        lastLoggedChecks[check] = result;
        string task = check switch
        {
            AutostartCheck.OnGround => "Flugzeug am Boden",
            AutostartCheck.CurrentTelemetry => "Aktuelle Telemetrie",
            AutostartCheck.EnginesRunning => "Laufende Triebwerke",
            AutostartCheck.TakeoffProfile => "Gültiges vorläufiges Startprofil",
            AutostartCheck.RunwayPosition => "Position innerhalb einer erkannten Startbahn",
            _ => "Wahre Richtung derselben Startbahn"
        };
        string outcome = details.Count == 0 ? "erfüllt" : "nicht erfüllt";
        string diagnostic = string.Join(", ", details.Select(detail => $"{detail.Field}:{detail.Code}"));
        Debug.WriteLine(FormattableString.Invariant($"[Debug] {nowUtc:yyyy-MM-dd HH:mm:ss} - [Autostart] {task}: {outcome}; {diagnostic}"));
        return result;
    }

}

public partial class AutoflyMain
{
    private enum AutostartDefinitionId : uint
    {
        Readiness = 12000, Throttle1 = 12001, RudderPosition = 12005,
        ElevatorPosition = 12006, AileronPosition = 12007, AileronTrim = 12008
    }
    private enum AutostartRequestId : uint { First = 0xA0000000 }
    private enum AutostartEventId : uint
    {
        AXIS_LEFT_BRAKE_SET = 12000,
        AXIS_RIGHT_BRAKE_SET,
        PARKING_BRAKE_SET = 12004,
        FLAPS_UP = 12005,
        GEAR_UP = 12006,
        FLAPS_1 = 12007
    }
    private enum AutostartPriority : uint { Highest = 1 }

    // Ein Paket bindet Modellwerte und Messwerte an denselben TITLE. Einzelne CHANGED-
    // oder ONCE-Antworten könnten beim Flugzeugwechsel aus verschiedenen Modellen stammen.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
    private struct AutostartReadinessData
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Title;
        public double EngineCount;
        public double RotationSpeed;
        public double ClimbSpeed;
        public double StallLanding;
        public double StallClean;
        public double CruiseSpeed;
        public double OnGround;
        public double Airspeed;
        public double Altitude;
        public double VerticalSpeed;
        public double Pitch;
        public double Bank;
        public double TrueHeading;
        public double Latitude;
        public double Longitude;
        public double Combustion1;
        public double Combustion2;
        public double Combustion3;
        public double Combustion4;
        public double AutopilotMaster;
        public double GroundSpeed;
        public double RudderPercent;
        public double Throttle1;
        public double Throttle2;
        public double Throttle3;
        public double Throttle4;
        public double ElevatorPercent;
        public double AboveGroundFeet;
        public double MagneticHeading;
        public double AileronPercent;
        public double LeftBrakePosition;
        public double RightBrakePosition;
        public double IsGearRetractable;
        public double GearTotalPosition;
        public double GearCenterPosition;
        public double GearLeftPosition;
        public double GearRightPosition;
        public double TrailingFlapsLeft;
        public double TrailingFlapsRight;
        public double LeadingFlapsLeft;
        public double LeadingFlapsRight;
    }

    private readonly Autostart autostart = new()
    {
        RemainingRunwayAbortMeters = ReadAutostartRunwayReserve()
    };

    private static double ReadAutostartRunwayReserve()
    {
        string? configured = Environment.GetEnvironmentVariable("AUTOFLY_AUTOSTART_RUNWAY_RESERVE_METERS");

        return string.IsNullOrWhiteSpace(configured) ? 200.0 :
            double.TryParse(configured, NumberStyles.Float, CultureInfo.InvariantCulture, out double meters)
                ? meters : double.NaN;
    }
    private readonly System.Windows.Forms.Timer autostartTimer;
    private AutostartSnapshot autostartSnapshot = new();
    private AutostartValue<double> autostartSimulatorMaster;
    private SimConnect? autostartDataConnection;
    private enum AutostartPacketKind { Definition, Readiness, AutopilotOff, Control, Stop, GearRetraction, FlapsRetraction }
    private readonly Dictionary<uint, (AutostartPacketKind Kind, uint SessionId)> autostartPackets = [];
    private readonly Queue<uint> autostartControlPackets = [];
    private bool autostartControlsOwned;
    private bool autostartRollTrimOwned;
    private bool autostartCancelPending;
    private bool autostartConnectionLost;
    private string? autostartRollStatus;
    private bool autostartDefinitionRegistered;
    private bool autostartDefinitionFailed;
    private uint autostartRequestSequence = (uint)AutostartRequestId.First;
    private uint autostartExpectedRequest;
    private string? autostartAircraftTitle;
    private string? autostartObservedTitle;
    private string? autostartIntegrationError;
    private DateTime autostartLastDisableSampleUtc;
    private DateTime autostartReadinessDeadlineUtc;
    private bool autostartErrorLatched;
    private DateTime autostartLastFlightSendUtc;
    private AutostartInitialClimbController autostartInitialClimbController = new();
    private DateTime autostartLastInitialClimbLogUtc;
    private bool autostartFlapsUpSent;
    private bool autostartGearUpSent;
    // Vorläufige lokale Bewegungswartezeiten, keine zugesicherten Flugzeug-Modellwerte.
    private const double AutostartGearMovementSeconds = 30.0;
    private const double AutostartFlapsMovementSeconds = 15.0;
    private sealed class AutostartRetractionCheck
    {
        internal int Attempts;
        internal DateTime SentUtc;
        internal bool Finished;
        internal string? Failure;
        internal bool Pending => Attempts > 0 && !Finished;
    }
    private AutostartRetractionCheck autostartGearCheck = new();
    private AutostartRetractionCheck autostartFlapsCheck = new();
    private bool autostartRetractionMonitoring;
    private bool autostartRetractionWarningLogged;
    private bool AutostartRetractionPending => autostartGearCheck.Pending || autostartFlapsCheck.Pending;
    private bool autostartHandoverMonitoring;
    private string? autostartHandoverTitle;
    private bool autostartGroundProbePending;                           // Wird auf true gesetzt, wenn die letzte Flugdiagnose noch nicht bestätigt wurde.
    private DateTime autostartGroundProbeDeadlineUtc;
    private DateTime autostartLastFlightDiagnosticUtc;
    private AutostartPhase? autostartLastFlightDiagnosticPhase;
    private bool? autostartLastFlightDiagnosticNegativeVs;
    private bool autostartLastFlightDiagnosticPitchLimited;
    private bool autostartLastFlightDiagnosticSpeedLimited;

    private bool IsAutostartActive => autostart.Phase is not
        (AutostartPhase.Idle or AutostartPhase.Aborted or AutostartPhase.Completed);

    private bool AutostartAirborneSafety => autostart.RequiresAirborneSafety(autostartSnapshot, DateTime.UtcNow);

    private string AutostartText(string key) =>
        Resources_DE.GetString("Autostart." + key);

    private void RequestAutostartReadiness()
    {
        // Eine neue Anfrage darf keine Stellungsprüfung der vorigen Session übernehmen.
        autostartRetractionMonitoring = false;
        autostartGearCheck.Finished = true;
        autostartFlapsCheck.Finished = true;
        SimConnect connection = simconnect ?? throw new InvalidOperationException(AutostartText("Disconnected"));
        // Die Definitionen werden in RequestAutostartReadiness() erneut registriert, wenn die Verbindung gewechselt wurde.
        if (!ReferenceEquals(autostartDataConnection, connection))
        {
            autostartDataConnection = connection;           // Neue Verbindung, alle bisherigen Definitionen und Anfragen verwerfen.
            autostartPackets.Clear();                       // SessionId wird in TrackAutostartPacket() gesetzt.
            autostartControlPackets.Clear();                // Wird in SendAutostartControl() gesetzt.
            autostartDefinitionRegistered = false;          // Wird in RequestAutostartReadiness() gesetzt.
            autostartDefinitionFailed = false;              // Wird in RequestAutostartReadiness() gesetzt.
        }
        autostartConnectionLost = false;
        if (autostartDefinitionFailed)
            throw new InvalidOperationException(AutostartText("DataUnavailable"));
        if (!autostartDefinitionRegistered)
        {
            // Ein abgebrochener Teilaufbau bleibt bis zum Verbindungswechsel unbrauchbar.
            autostartDefinitionFailed = true;
            // Die Definitionen werden in RequestAutostartReadiness() erneut registriert, wenn die Verbindung gewechselt wurde.
            connection.AddToDataDefinition(AutostartDefinitionId.Readiness, "TITLE", null,
                SIMCONNECT_DATATYPE.STRING256, 0, SimConnect.SIMCONNECT_UNUSED);
            TrackAutostartPacket(connection, AutostartPacketKind.Definition, 0);
            // Die Reihenfolge der Felder muss exakt den zusätzlichen Feldern des gepackten Pakets entsprechen.
            DataItem[] numbers =
            [
                Flight_Model.NUMBER_OF_ENGINES, Flight_Model.DESIGN_SPEED_MIN_ROTATION,
                Flight_Model.DESIGN_SPEED_CLIMB, Flight_Model.DESIGN_SPEED_VS0,
                Flight_Model.DESIGN_SPEED_VS1, Flight_Model.DESIGN_SPEED_VC,
                Flight_Data.SIM_ON_GROUND, Flight_Data.AIRSPEED_INDICATED,
                Flight_Data.INDICATED_ALTITUDE, Flight_Data.VERTICAL_SPEED,
                Flight_Data.PLANE_PITCH_DEGREES, Flight_Data.PLANE_BANK_DEGREES,
                Flight_Data.PLANE_HEADING_DEGREES_TRUE, Flight_Data.PLANE_LATITUDE,
                Flight_Data.PLANE_LONGITUDE, Flight_Data.ENG_COMBUSTION_1,
                Flight_Data.ENG_COMBUSTION_2, Flight_Data.ENG_COMBUSTION_3,
                Flight_Data.ENG_COMBUSTION_4, Flight_Data.AUTOPILOT_MASTER
            ];
            // Nötige Variablen für den Startvorgang anmelden, auch wenn sie nicht in der Autostart-Logik verwendet werden.
            foreach (DataItem item in numbers)
            {
                connection.AddToDataDefinition(AutostartDefinitionId.Readiness, item.Name, item.Unit,
                    SIMCONNECT_DATATYPE.FLOAT64, 0, SimConnect.SIMCONNECT_UNUSED);
                TrackAutostartPacket(connection, AutostartPacketKind.Definition, 0);
            }
            connection.AddToDataDefinition(AutostartDefinitionId.Readiness, "GROUND VELOCITY", "knots",
                SIMCONNECT_DATATYPE.FLOAT64, 0, SimConnect.SIMCONNECT_UNUSED);
            TrackAutostartPacket(connection, AutostartPacketKind.Definition, 0);
            connection.AddToDataDefinition(AutostartDefinitionId.Readiness, "RUDDER POSITION", "percent",
                SIMCONNECT_DATATYPE.FLOAT64, 0, SimConnect.SIMCONNECT_UNUSED);
            TrackAutostartPacket(connection, AutostartPacketKind.Definition, 0);
            for (int engine = 0; engine < 4; engine++)
            {
                connection.AddToDataDefinition(AutostartDefinitionId.Readiness,
                    $"GENERAL ENG THROTTLE LEVER POSITION:{engine + 1}", "percent",
                    SIMCONNECT_DATATYPE.FLOAT64, 0, SimConnect.SIMCONNECT_UNUSED);
                TrackAutostartPacket(connection, AutostartPacketKind.Definition, 0);
                AutostartDefinitionId definition = (AutostartDefinitionId)((uint)AutostartDefinitionId.Throttle1 + engine);
                connection.AddToDataDefinition(definition, $"GENERAL ENG THROTTLE LEVER POSITION:{engine + 1}",
                    "percent", SIMCONNECT_DATATYPE.FLOAT64, 0, SimConnect.SIMCONNECT_UNUSED);
                TrackAutostartPacket(connection, AutostartPacketKind.Definition, 0);
                connection.RegisterDataDefineStruct<double>(definition);
            }
            connection.AddToDataDefinition(AutostartDefinitionId.RudderPosition, "RUDDER POSITION", "percent over 100",
                SIMCONNECT_DATATYPE.FLOAT64, 0, SimConnect.SIMCONNECT_UNUSED);
            TrackAutostartPacket(connection, AutostartPacketKind.Definition, 0);
            connection.RegisterDataDefineStruct<double>(AutostartDefinitionId.RudderPosition);
            // Reihenfolge muss exakt den zusätzlichen Feldern des gepackten Pakets entsprechen.
            foreach ((string name, string unit) in new[]
            {
                ("ELEVATOR POSITION", "percent"), ("PLANE ALT ABOVE GROUND", "feet"),
                ("PLANE HEADING DEGREES MAGNETIC", "degrees"), ("AILERON POSITION", "percent"),
                ("BRAKE LEFT POSITION", "percent over 100"), ("BRAKE RIGHT POSITION", "percent over 100"),
                ("IS GEAR RETRACTABLE", "bool"),
                ("GEAR TOTAL PCT EXTENDED", "percent over 100"),
                ("GEAR CENTER POSITION", "percent over 100"),
                ("GEAR LEFT POSITION", "percent over 100"),
                ("GEAR RIGHT POSITION", "percent over 100"),
                ("TRAILING EDGE FLAPS LEFT PERCENT", "percent over 100"),
                ("TRAILING EDGE FLAPS RIGHT PERCENT", "percent over 100"),
                ("LEADING EDGE FLAPS LEFT PERCENT", "percent over 100"),
                ("LEADING EDGE FLAPS RIGHT PERCENT", "percent over 100")
            })
            {
                connection.AddToDataDefinition(AutostartDefinitionId.Readiness, name, unit,
                    SIMCONNECT_DATATYPE.FLOAT64, 0, SimConnect.SIMCONNECT_UNUSED);
                TrackAutostartPacket(connection, AutostartPacketKind.Definition, 0);
            }
            foreach ((AutostartDefinitionId definition, string name) in new[]
            {
                (AutostartDefinitionId.ElevatorPosition, "ELEVATOR POSITION"),
                (AutostartDefinitionId.AileronPosition, "AILERON POSITION"),
                (AutostartDefinitionId.AileronTrim, "AILERON TRIM PCT")
            })
            {
                connection.AddToDataDefinition(definition, name, "percent over 100",
                    SIMCONNECT_DATATYPE.FLOAT64, 0, SimConnect.SIMCONNECT_UNUSED);
                TrackAutostartPacket(connection, AutostartPacketKind.Definition, 0);
                connection.RegisterDataDefineStruct<double>(definition);
            }
            foreach (AutostartEventId eventId in Enum.GetValues<AutostartEventId>())
            {
                connection.MapClientEventToSimEvent(eventId, eventId.ToString());
                TrackAutostartPacket(connection, AutostartPacketKind.Definition, 0);
            }
            connection.RegisterDataDefineStruct<AutostartReadinessData>(AutostartDefinitionId.Readiness);
            autostartDefinitionRegistered = true;
            autostartDefinitionFailed = false;
        }
        autostartExpectedRequest = checked(++autostartRequestSequence);
        connection.RequestDataOnSimObject((AutostartRequestId)autostartExpectedRequest,
            AutostartDefinitionId.Readiness, SimConnect.SIMCONNECT_OBJECT_ID_USER,
            SIMCONNECT_PERIOD.SIM_FRAME, SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT, 0, 0, 0);
        TrackAutostartPacket(connection, AutostartPacketKind.Readiness, autostartExpectedRequest);
    }

    private void TrackAutostartPacket(SimConnect connection, AutostartPacketKind kind, uint sessionId)
    {
        uint packetId = connection.GetLastSentPacketID();
        autostartPackets[packetId] = (kind, sessionId);
        if (kind is AutostartPacketKind.Control or AutostartPacketKind.GearRetraction or AutostartPacketKind.FlapsRetraction)
        {
            autostartControlPackets.Enqueue(packetId);
            if (autostartControlPackets.Count > 4096)
                autostartPackets.Remove(autostartControlPackets.Dequeue());
        }
    }

    private void SendAutostartControls(double throttle, bool brakes, double rudderPosition, bool release = false) =>
        SendAutostartControls([.. Enumerable.Repeat(throttle, autostart.ActiveEngineCount)],
            brakes, rudderPosition, release);

    private void SendAutostartControls(IReadOnlyList<double> throttles, bool brakes, double rudderPosition,
        bool release = false, double elevatorPosition = 0, double aileronPosition = 0, bool preserveThrottle = false,
        bool allowInitialClimb = false, double? aileronTrimPosition = null)
    {
        // Im Anfangssteigflug ausschließlich dessen freigegebene Ausgabe zulassen.
        if (autostart.AirborneObserved && !allowInitialClimb)
            return;
        if (autostartConnectionLost || !autostartControlsOwned || simconnect is not { } connection ||
            !ReferenceEquals(connection, autostartDataConnection))
            return;

        bool controlFailedBeforeSend = autostartIntegrationError != null;
        void send(Action action)
        {
            if (!autostartControlsOwned)
                return;
            try
            {
                action();
                TrackAutostartPacket(connection, AutostartPacketKind.Control, autostartExpectedRequest);
            }
            catch (COMException ex)
            {
                AutostartControlFailed(ex);
            }
            catch (InvalidOperationException ex)
            {
                AutostartControlFailed(ex);
            }
        }
        // Jeden Motor und jede Bremse unabhängig bedienen: ein Fehler darf die anderen nicht überspringen.
        for (int engine = 0; engine < autostart.ActiveEngineCount; engine++)
        {
            // Pro Aktor erneut prüfen: ein voriger Sendefehler kann die Sicherheitslage ändern.
            if (preserveThrottle || (AutostartAirborneSafety && autostartIntegrationError != null))
                continue;
            AutostartDefinitionId definition = (AutostartDefinitionId)((uint)AutostartDefinitionId.Throttle1 + engine);
            double throttle = engine < throttles.Count ? throttles[engine] : 0.0;
            send(() => connection.SetDataOnSimObject(definition, SimConnect.SIMCONNECT_OBJECT_ID_USER,
                SIMCONNECT_DATA_SET_FLAG.DEFAULT, autostartIntegrationError == null ? throttle : 0.0));
        }
        foreach ((AutostartDefinitionId definition, double value) in new[]
        {
            (AutostartDefinitionId.ElevatorPosition, elevatorPosition),
            (AutostartDefinitionId.AileronPosition, aileronPosition)
        })
            send(() => connection.SetDataOnSimObject(definition, SimConnect.SIMCONNECT_OBJECT_ID_USER,
                SIMCONNECT_DATA_SET_FLAG.DEFAULT,
                AutostartControlOutput.RudderPositionForSend(value, autostartIntegrationError != null)));
        if (aileronTrimPosition.HasValue || autostartRollTrimOwned)
        {
            // Auch bei Abbruch/Sendefehler nur die von Autostart übernommene Quertrimmung lösen.
            autostartRollTrimOwned = true;
            send(() =>
            {
                double trim = autostartIntegrationError == null &&
                    aileronTrimPosition is double requestedTrim && double.IsFinite(requestedTrim)
                    ? Math.Clamp(requestedTrim, -AutostartInitialClimbController.MaximumAileronTrimPosition,
                        AutostartInitialClimbController.MaximumAileronTrimPosition) : 0.0;
                connection.SetDataOnSimObject(AutostartDefinitionId.AileronTrim,
                    SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_DATA_SET_FLAG.DEFAULT, trim);
                autostartRollTrimOwned = trim != 0.0;
            });
        }
        // Ausschließlich Pomax' SimVar-Ausgabe; keine zweite Lenkachse mit unbekannter Einzelwirkung.
        send(() => connection.SetDataOnSimObject(AutostartDefinitionId.RudderPosition,
            SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_DATA_SET_FLAG.DEFAULT,
            AutostartControlOutput.RudderPositionForSend(rudderPosition, autostartIntegrationError != null)));
        foreach (AutostartEventId eventId in Enum.GetValues<AutostartEventId>())
        {
            if (eventId is AutostartEventId.FLAPS_UP or AutostartEventId.GEAR_UP or AutostartEventId.FLAPS_1)
                continue; // Einmalbefehle niemals zusammen mit den Bremsen senden.
            // Fehlerzustand für jeden Aktor neu lesen: der vorige send-Aufruf kann
            // ihn gesetzt haben. Dann keine Bremse mehr lösen und nicht weiter lenken.
            uint value = eventId switch
            {
                AutostartEventId.AXIS_LEFT_BRAKE_SET or AutostartEventId.AXIS_RIGHT_BRAKE_SET =>
                    AutostartAirborneSafety ? AutostartControlOutput.EncodeBrake(false, true, false) :
                        AutostartControlOutput.EncodeBrake(brakes, release, autostartIntegrationError != null),
                AutostartEventId.PARKING_BRAKE_SET => 0u,
                _ => throw new InvalidOperationException()
            };
            send(() => connection.TransmitClientEvent(SimConnect.SIMCONNECT_OBJECT_ID_USER, eventId,
                value, AutostartPriority.Highest, SIMCONNECT_EVENT_FLAG.GROUPID_IS_PRIORITY));
        }
        // Genau ein Nachlauf: am Boden Leerlauf/Vollbremsen, bei Flugsicherheitslage
        // nur Achsen neutral und Radbremsen lösen, ohne den Schub anzutasten.
        if (!controlFailedBeforeSend && autostartIntegrationError != null)
            SendAutostartSafetyControls();
    }

    private void SendAutostartSafetyControls() => SendAutostartControls(
        [.. Enumerable.Repeat(0.0, autostart.ActiveEngineCount)], !AutostartAirborneSafety, 0,
        release: AutostartAirborneSafety, preserveThrottle: AutostartAirborneSafety, allowInitialClimb: true);

    private void AutostartControlFailed(Exception error)
    {
        if (autostartIntegrationError == null)
            Debug.WriteLine($"[Debug] {DateTime.Now:yyyy-MM-dd HH:mm:ss} - [Autostart] {error}");
        autostartIntegrationError = AutostartText("ControlFailure");
        if (!AutostartAirborneSafety)
            autostart.Brake("ControlFailure");
    }

    private void UpdateAutostartRoll(DateTime nowUtc)
    {
        if (!autostartControlsOwned || CompleteAutostartAtLiftoff(nowUtc))
            return;
        string? block = GetAutostartBlock(nowUtc);
        if (block != null && AutostartAirborneSafety)
        {
            EndAutostartForManualTakeover("FlightManualTakeover");
            return;
        }
        if (block != null)
            autostart.Brake(autostartIntegrationError != null ? "ControlFailure" : "RollInterlock");
        var (throttles, fullBrakes, rudderPosition) = autostart.UpdateRoll(autostartSnapshot, airportRunwayData, nowUtc);
        if (CompleteAutostartAtLiftoff(nowUtc))
            return;
        if (autostart.RollStopReason != null && AutostartAirborneSafety)
        {
            EndAutostartForManualTakeover(autostart.FlightOutput?.FailureKey ?? autostart.RollStopReason);
            return;
        }
        if (autostart.UsesFlightControl && autostart.FlightOutput is { } flight)
        {
            LogAutostartFlight(nowUtc, flight, throttles, rudderPosition);
            DateTime sampleUtc = autostartSnapshot.OnGround.LastUpdatedUtc;
            if (!flight.IsNewSample || sampleUtc <= autostartLastFlightSendUtc)
                return;
            autostartLastFlightSendUtc = sampleUtc;
            if (!IsAutostartActive)
                return;
            SendAutostartControls(throttles, false, autostartSnapshot.OnGround.Value == 0 ? 0 : rudderPosition,
                release: true, elevatorPosition: flight.ElevatorPosition, aileronPosition: flight.AileronPosition);
            if (autostartIntegrationError != null && AutostartAirborneSafety)
            {
                EndAutostartForManualTakeover("FlightManualTakeover", sendSafety: false);
                return;
            }
        }
        else
            SendAutostartControls(throttles, fullBrakes, rudderPosition);
        string status = autostart.RollStopReason ?? (autostart.Phase switch
        {
            AutostartPhase.StartRoll => "StartRoll",
            AutostartPhase.Rotation => "FlightRotation",
            AutostartPhase.Stopped => autostart.RotationSpeedReached ? "RollStopped" : "AbortStopped",
            _ => "VrBraking"
        });
        if (autostart.Phase == AutostartPhase.Stopped && autostart.RollStopReason != null)
            status = "AbortStopped";
        if (status != autostartRollStatus)
        {
            autostartRollStatus = status;
            Debug.WriteLine($"[Debug] {nowUtc:yyyy-MM-dd HH:mm:ss} - [Autostart] " + AutostartText(status));
        }
        if (autostart.RollStopReason != null && !autostartCancelPending)
            ShowAutostartBlocked(autostart.Evaluate(autostartSnapshot, airportRunwayData, nowUtc),
                block ?? AutostartText(autostart.RollStopReason));
        if (autostartCancelPending && autostart.Phase == AutostartPhase.Stopped)
            CancelAutostart();
    }

    private void LogAutostartFlight(DateTime nowUtc, AutostartFlightOutput flight,
        IReadOnlyList<double> throttles, double rudder)
    {
        if (!flight.IsNewSample)
            return;
        AutostartSnapshot s = autostartSnapshot;
        bool negativeVs = s.VerticalSpeedFeetPerMinute.Value < 0;
        if (autostartLastFlightDiagnosticPhase == autostart.Phase &&
            autostartLastFlightDiagnosticNegativeVs == negativeVs &&
            autostartLastFlightDiagnosticPitchLimited == flight.PitchLimited &&
            autostartLastFlightDiagnosticSpeedLimited == flight.SpeedLimited &&
            (nowUtc - autostartLastFlightDiagnosticUtc).TotalSeconds < 0.5)
            return;
        //Debug.WriteLine(FormattableString.Invariant($"[Debug] {nowUtc:yyyy-MM-dd HH:mm:ss} - [Autostart] Phase={autostart.Phase}, SampleUTC={s.OnGround.LastUpdatedUtc:O}, dt={flight.SampleIntervalSeconds:F3} s, OnGround={s.OnGround.Value:F0}, IAS={s.IndicatedAirspeedKnots.Value:F2} kt, VY={s.ClimbSpeedKnots.Value:F2} kt, VSSoll={AutostartFlightController.TargetVerticalSpeedFeetPerMinute:F0} ft/min, VS={s.VerticalSpeedFeetPerMinute.Value:F1} ft/min, VSFehler={flight.VerticalSpeedErrorFeetPerMinute:F1}, dVSdt={flight.VerticalSpeedChangeFeetPerMinutePerSecond:F1} ft/min/s, AGL={s.AboveGroundFeet.Value:F1} ft, PitchSim={s.PitchDegrees.Value:F2} deg, Bank={s.BankDegrees.Value:F2} deg, BankSoll={flight.TargetBankDegrees:F2} deg, Rollrate={flight.RollRateDegreesPerSecond:F2} deg/s, TrueIst={s.TrueHeadingDegrees.Value:F3} deg, TrueSoll={autostart.RunwayTrueHeading:F3} deg, Kursfehler={flight.HeadingErrorDegrees:F3} deg, ElevatorIst={s.ElevatorPercent.Value / 100.0:F6}, ElevatorDelta={flight.ElevatorCorrection:F6}, ElevatorSoll={flight.ElevatorPosition:F6}, PitchLimit={flight.PitchLimited}, SpeedLimit={flight.SpeedLimited}, AileronIst={s.AileronPercent.Value / 100.0:F6}, AileronSoll={flight.AileronPosition:F6}, RudderIst={s.RudderPercent.Value / 100.0:F6}, RudderSoll={(s.OnGround.Value == 0 ? 0 : rudder):F6}, ThrottleSoll={string.Join("/", throttles.Select(t => t.ToString("F2", CultureInfo.InvariantCulture)))}"));
        autostartLastFlightDiagnosticUtc = nowUtc;
        autostartLastFlightDiagnosticPhase = autostart.Phase;
        autostartLastFlightDiagnosticNegativeVs = negativeVs;
        autostartLastFlightDiagnosticPitchLimited = flight.PitchLimited;
        autostartLastFlightDiagnosticSpeedLimited = flight.SpeedLimited;
    }

    private static bool TryAutostartRegulatorValue(DataItem item, DateTime nowUtc, bool fresh, out double value)
    {
        value = item.Value switch
        {
            double number => number, float number => number, int number => number,
            uint number => number, bool state => state ? 1 : 0, _ => double.NaN
        };
        return double.IsFinite(value) && item.LastUpdated != default && item.LastUpdated.Kind == DateTimeKind.Utc &&
            item.LastUpdated <= nowUtc && (!fresh || (nowUtc - item.LastUpdated).TotalSeconds <= 2);
    }

    private void RetractAutostartGearIfDue(DateTime nowUtc)
    {
        AutostartSnapshot s = autostartSnapshot;
        bool fresh(AutostartValue<double> value) => value.HasValue &&
            value.LastUpdatedUtc.Kind == DateTimeKind.Utc && value.LastUpdatedUtc <= nowUtc &&
            (nowUtc - value.LastUpdatedUtc).TotalSeconds <= Autostart.MaximumTelemetryAgeSeconds;
        if (autostartGearUpSent || s.SessionId != autostartExpectedRequest ||
            !fresh(s.AboveGroundFeet) || !fresh(s.IsGearRetractable))
            return;
        AutostartGearRetractionOutput gear = AutostartControlOutput.CalculateGearRetraction(new(
            autostart.AirborneObserved, autostartGearUpSent, s.AboveGroundFeet.Value, s.IsGearRetractable.Value));
        if (!gear.RequestGearUp)
            return;
        autostartGearUpSent = true;
        SendAutostartRetraction(autostartGearCheck, AutostartEventId.GEAR_UP, nowUtc);
    }

    private void SendAutostartRetraction(AutostartRetractionCheck check, AutostartEventId eventId, DateTime nowUtc)
    {
        if (check.Attempts >= 2 || check.Finished)
            return;
        autostartRetractionMonitoring = true;
        // Vor dem Senden zählen: Auch ein Übertragungsfehler darf keine Sendeschleife erzeugen.
        check.Attempts++;
        check.SentUtc = nowUtc;
        try
        {
            SimConnect connection = simconnect ?? throw new InvalidOperationException("Disconnected");
            connection.TransmitClientEvent(SimConnect.SIMCONNECT_OBJECT_ID_USER, eventId,
                0u, AutostartPriority.Highest, SIMCONNECT_EVENT_FLAG.GROUPID_IS_PRIORITY);
            TrackAutostartPacket(connection, eventId == AutostartEventId.GEAR_UP
                ? AutostartPacketKind.GearRetraction : AutostartPacketKind.FlapsRetraction, autostartExpectedRequest);
            if (check.Attempts == 1)
                Debug.WriteLine($"[Debug] {nowUtc:yyyy-MM-dd HH:mm:ss} - [Autostart] {eventId} angefordert");
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            check.Finished = true;
            check.Failure = $"Übertragungsfehler: {ex.Message}";
        }
    }

    private void UpdateAutostartRetractionChecks(DateTime nowUtc)
    {
        if (!autostartRetractionMonitoring)
            return;
        AutostartSnapshot s = autostartSnapshot;
        bool fresh(AutostartValue<double> value) => value.HasValue &&
            value.LastUpdatedUtc != default && value.LastUpdatedUtc.Kind == DateTimeKind.Utc &&
            value.LastUpdatedUtc <= nowUtc &&
            (nowUtc - value.LastUpdatedUtc).TotalSeconds <= Autostart.MaximumTelemetryAgeSeconds;
        bool identity = autostartExpectedRequest != 0 && s.SessionId == autostartExpectedRequest &&
            !string.IsNullOrWhiteSpace(s.Title.Value) && s.Title.Value == autostartAircraftTitle &&
            (autostartObservedTitle == null || s.Title.Value == autostartObservedTitle) &&
            !autostartConnectionLost && ReferenceEquals(simconnect, autostartDataConnection) && simconnect != null &&
            !relocationInProgress && !relocationControlsSuspended;

        void update(AutostartRetractionCheck check, AutostartEventId eventId, double movementSeconds,
            params AutostartValue<double>[] positions)
        {
            if (!check.Pending)
                return;
            bool current = identity && positions.All(value => fresh(value) &&
                value.LastUpdatedUtc > check.SentUtc && double.IsFinite(value.Value) && value.Value is >= 0 and <= 1);
            if (current && positions.All(value => value.Value == 0.0))
            {
                check.Finished = true;
                return;
            }
            double elapsed = (nowUtc - check.SentUtc).TotalSeconds;
            if (elapsed < movementSeconds)
                return;
            // Nur eine aktuelle negative Iststellung rechtfertigt die eine Wiederholung.
            // Fehlende/veraltete Werte sind keine negative Stellungsrückmeldung.
            if (check.Attempts == 1 && elapsed < 2 * movementSeconds && current &&
                fresh(s.OnGround) && s.OnGround.Value == 0.0 &&
                autostart.AirborneObserved && (eventId != AutostartEventId.GEAR_UP ||
                    (fresh(s.IsGearRetractable) && s.IsGearRetractable.Value == 1.0)))
            {
                SendAutostartRetraction(check, eventId, nowUtc);
                return;
            }
            if (check.Attempts == 1 && !current && elapsed < 2 * movementSeconds)
                return;
            check.Finished = true;
            check.Failure = current
                ? $"Endstellung nicht erreicht (Ist={string.Join("/", positions.Select(value => value.Value.ToString("P1", CultureInfo.InvariantCulture)))}, Sendeversuche={check.Attempts})"
                : $"keine aktuelle gültige Stellungsrückmeldung (Sendeversuche={check.Attempts})";
        }

        update(autostartGearCheck, AutostartEventId.GEAR_UP, AutostartGearMovementSeconds,
            s.GearTotalPosition, s.GearCenterPosition, s.GearLeftPosition, s.GearRightPosition);
        update(autostartFlapsCheck, AutostartEventId.FLAPS_UP, AutostartFlapsMovementSeconds,
            s.TrailingFlapsLeft, s.TrailingFlapsRight, s.LeadingFlapsLeft, s.LeadingFlapsRight);

        if (!AutostartRetractionPending && !autostartHandoverMonitoring && !IsAutostartActive)
        {
            if (!autostartRetractionWarningLogged &&
                (autostartGearCheck.Failure != null || autostartFlapsCheck.Failure != null))
            {
                autostartRetractionWarningLogged = true;
                Debug.WriteLine($"[Debug] {nowUtc:yyyy-MM-dd HH:mm:ss} - [Autostart] Erfolgskontrolle: Fahrwerk: {autostartGearCheck.Failure ?? "kein Fehler"}; Klappen: {autostartFlapsCheck.Failure ?? "kein Fehler"}. Keine weiteren Sendeversuche.");
            }
            if (autostart.Phase == AutostartPhase.Completed && autostartExpectedRequest != 0)
            {
                autostartTimer.Stop();
                StopAutostartReadinessRequest();
            }
        }
    }

    private bool CompleteAutostartAtLiftoff(DateTime nowUtc)
    {
        if (!autostartControlsOwned)
            return false;
        autostart.ObserveAirborne(autostartSnapshot, nowUtc);
        if (!autostart.AirborneObserved || autostart.Phase != AutostartPhase.InitialClimb)
            return false;

        // Nicht blockierend: ein Regelschritt je neuem, zusammengehörigem Empfangspaket.
        if (GetAutostartBlock(nowUtc) != null || autostart.RollStopReason != null)
        {
            EndAutostartForManualTakeover("FlightManualTakeover");
            return true;
        }
        AutostartSnapshot s = autostartSnapshot;
        AutostartFlightOutput climb = autostartInitialClimbController.Update(new(
            s.SessionId, s.Title.Value ?? string.Empty, s.OnGround.LastUpdatedUtc, !autostart.AirborneObserved,
            s.IndicatedAirspeedKnots.Value, s.RotationSpeedKnots.Value, s.ClimbSpeedKnots.Value,
            s.VerticalSpeedFeetPerMinute.Value, s.AboveGroundFeet.Value, s.PitchDegrees.Value,
            s.BankDegrees.Value, s.ElevatorPercent.Value,
            s.Title.Value?.Contains("Pitts", StringComparison.OrdinalIgnoreCase) == true ||
            s.Title.Value?.Contains("Gee Bee", StringComparison.OrdinalIgnoreCase) == true)
        {
            TrueHeadingDegrees = s.TrueHeadingDegrees.Value,
            RunwayTrueHeadingDegrees = autostart.RunwayTrueHeading,
            AileronPercent = s.AileronPercent.Value
        }, nowUtc);
        if (climb.FailureKey != null)
        {
            Debug.WriteLine($"[Debug] {nowUtc:yyyy-MM-dd HH:mm:ss} - [Autostart] {climb.FailureKey}: {autostartInitialClimbController.TelemetryFailureDetails ?? "Steigflug-Zeitgrenze überschritten"}; OnGroundRaw={s.OnGround.Value:R}, AirborneObserved={autostart.AirborneObserved}");
            EndAutostartForManualTakeover(climb.FailureKey);
            return true;
        }
        if (!climb.IsNewSample)
            return true;
        try
        {
            RetractAutostartGearIfDue(nowUtc);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            AutostartControlFailed(ex);
            EndAutostartForManualTakeover("ControlFailure");
            return true;
        }
        if (s.AboveGroundFeet.Value >= 300 && !autostartFlapsUpSent)
        {
            try
            {
                autostartFlapsUpSent = true;
                SendAutostartRetraction(autostartFlapsCheck, AutostartEventId.FLAPS_UP, nowUtc);
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException)
            {
                AutostartControlFailed(ex);
                EndAutostartForManualTakeover("ControlFailure");
                return true;
            }
        }
        if (climb.Stage != AutostartFlightStage.ReadyForHandover)
        {
            SendAutostartControls([], false, 0, release: true,
                elevatorPosition: climb.ElevatorPosition, aileronPosition: climb.AileronPosition,
                preserveThrottle: true, allowInitialClimb: true, aileronTrimPosition: climb.AileronTrimPosition);
            if ((nowUtc - autostartLastInitialClimbLogUtc).TotalSeconds >= 1)
            {
                autostartLastInitialClimbLogUtc = nowUtc;
                //Debug.WriteLine(FormattableString.Invariant($"[Debug] {nowUtc:yyyy-MM-dd HH:mm:ss} - [Autostart] Anfangssteigflug: AGL={s.AboveGroundFeet.Value:F1} ft, ElevatorSoll={climb.ElevatorPosition * 100:F2} %, QuerSoll={climb.AileronPosition * 100:F2} %, QuerTrimSoll={climb.AileronTrimPosition * 100:F3} %, KursFehler={climb.HeadingErrorDegrees:F2} Grad, BankSoll={climb.TargetBankDegrees:F2} Grad, BankIst={s.BankDegrees.Value:F2} Grad. Vorläufige Quertrimmgrenze +/-5 %, Rate maximal 0.5 Prozentpunkte/s."));
            }
            if (autostartIntegrationError != null)
                EndAutostartForManualTakeover("ControlFailure", sendSafety: false);
            return true;
        }

        // Bei 200 ft AGL ohne Neutralisierung übergeben; bis 500 ft 10 % Höhenruder.
        try
        {
            SimConnect connection = simconnect ?? throw new InvalidOperationException("Disconnected");
            AutoPilot controller = autopilot ?? throw new InvalidOperationException("Autopilot unavailable");
            if (!controller.IsReady)
                throw new InvalidOperationException("Autopilot not ready");
            int heading = AutostartControlOutput.CalculateHandoverTargets(0,
                autostart.RunwayTrueHeading, s.TrueHeadingDegrees.Value, s.MagneticHeadingDegrees.Value,
                targetAltitudeFeet: 2000).MagneticHeadingDegrees;
            connection.SetDataOnSimObject(AutostartDefinitionId.ElevatorPosition,
                SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_DATA_SET_FLAG.DEFAULT, climb.ElevatorPosition);
            TrackAutostartPacket(connection, AutostartPacketKind.Control, autostartExpectedRequest);
            _targetHeading = heading;
            tstbHeading.Text = heading.ToString(CultureInfo.CurrentCulture);
            controller.LocalTargetHdgDeg = heading;
            controller.LvlOnOff(true);
            controller.HdgOnOff(true);
            controller.AutopilotLocalOnOff(true);
            controller.PrepareAutostartHeadingHandover(s.Title.Value ??
                throw new InvalidOperationException("Aircraft title unavailable"));
            _isLocalAutopilotActive = _isHDGActive = _isLVLActive = true;
            tsbAP.BackColor = tsbHDG.BackColor = TsbLVL.BackColor = System.Drawing.Color.LightGreen;
            autostartHandoverTitle = s.Title.Value;
            autostartHandoverMonitoring = true;
            // Eigentum abgeben, aber den übergebenen Trimmwert nicht zurücksetzen.
            autostartRollTrimOwned = false;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            AutostartControlFailed(ex);
            EndAutostartForManualTakeover("ControlFailure");
            return true;
        }
        // Keine weitere Autostart-Achssteuerung. Nur 300-/500-ft-Überwachung bleibt.
        autostartControlsOwned = false;
        //Debug.WriteLine(FormattableString.Invariant($"[Debug] {nowUtc:yyyy-MM-dd HH:mm:ss} - [Autostart] Phase=InitialClimb, OnGround=false, IAS={autostartSnapshot.IndicatedAirspeedKnots.Value:F2} kt, VS={autostartSnapshot.VerticalSpeedFeetPerMinute.Value:F1} ft/min, AGL={autostartSnapshot.AboveGroundFeet.Value:F1} ft, Steuerung beendet; manuell übernehmen. Keine weiteren Aktorbefehle."));
        CancelAirportFacilities(preserveRunwayData: true);
        autostart.Complete();
        FinishAutostartUi();
        //Debug.WriteLine($"[Debug] {nowUtc:yyyy-MM-dd HH:mm:ss} - [Autostart] Phase=Completed, ManualControl, AP/ALT/HDG/LVL/AutoThrottle bleiben OFF.");
        Debug.WriteLine($"[Debug] {nowUtc:yyyy-MM-dd HH:mm:ss} - [Autostart] 200 ft Höhe erreicht: AP/HDG/LVL ON, Kurs={_targetHeading}°. Quertrimmung und Quer-/Seitenruder unverändert. Höhenruder 10 %");
        UpdateAutostartHandoverMonitoring(nowUtc);
        return true;
    }

    private void StopAutostartHandoverMonitoring(string reason, bool preserveRetractionChecks = false)
    {
        autostartHandoverMonitoring = false;
        autostartHandoverTitle = null;
        if (!preserveRetractionChecks)
        {
            autostartTimer.Stop();
            StopAutostartReadinessRequest();
        }
        else
            UpdateAutostartRetractionChecks(DateTime.UtcNow);
        Debug.WriteLine($"[Debug] {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} - [Autostart] Nachlaufüberwachung beendet: {reason}");
    }

    private void UpdateAutostartHandoverMonitoring(DateTime nowUtc)
    {
        if (!autostartHandoverMonitoring)
            return;
        AutostartSnapshot s = autostartSnapshot;
        if (GetAutostartBlock(nowUtc) != null || !_isLocalAutopilotActive || !_isHDGActive || !_isLVLActive ||
            _isALTActive || _isWayPointFollow || terrainFollow.Enabled ||
            s.SessionId != autostartExpectedRequest || s.Title.Value != autostartHandoverTitle ||
            !s.OnGround.HasValue || !autostart.AirborneObserved || !s.AboveGroundFeet.HasValue ||
            !double.IsFinite(s.AboveGroundFeet.Value) || s.AboveGroundFeet.Value < 0 ||
            s.OnGround.LastUpdatedUtc.Kind != DateTimeKind.Utc || s.OnGround.LastUpdatedUtc > nowUtc ||
            (nowUtc - s.OnGround.LastUpdatedUtc).TotalSeconds > 2)
        {
            StopAutostartHandoverMonitoring("Telemetrie ungültig oder manuelle Modusänderung; keine weitere automatische Schaltung.");
            return;
        }
        try
        {
            SimConnect connection = simconnect ?? throw new InvalidOperationException("Disconnected");
            RetractAutostartGearIfDue(nowUtc);
            if (s.AboveGroundFeet.Value >= 300 && !autostartFlapsUpSent)
            {
                autostartFlapsUpSent = true;
                SendAutostartRetraction(autostartFlapsCheck, AutostartEventId.FLAPS_UP, nowUtc);
            }
            if (s.AboveGroundFeet.Value >= 500)
            {
                AutoPilot controller = autopilot ?? throw new InvalidOperationException("Autopilot unavailable");
                // Erst jetzt den festen Höhenruderausschlag lösen, dann ALT übernehmen lassen.
                connection.SetDataOnSimObject(AutostartDefinitionId.ElevatorPosition,
                    SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_DATA_SET_FLAG.DEFAULT, 0.0);
                TrackAutostartPacket(connection, AutostartPacketKind.Control, autostartExpectedRequest);
                _targetAltitude = 2000;
                tstbAltitude.Text = _targetAltitude.ToString(CultureInfo.CurrentCulture);
                controller.localTargetAltDeg = _targetAltitude;
                controller.AltOnOff(true);
                _isALTActive = true;
                tsbALT.BackColor = System.Drawing.Color.LightGreen;
                StopAutostartHandoverMonitoring("500 ft AGL erreicht: ALT aktiv, Sollhöhe 2000 ft angezeigte Höhe. AP/HDG/LVL bleiben aktiv.", preserveRetractionChecks: true);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            Debug.WriteLine($"[Debug] {nowUtc:yyyy-MM-dd HH:mm:ss} - [Autostart] Nachlaufüberwachung: {ex}");
            StopAutostartHandoverMonitoring("Übertragungsfehler; manuell übernehmen.");
        }
    }

    private void FinishAutostartUi()
    {
        autostartCancelPending = false;
        autoStartDialog.Visible = false;
        autostartErrorLatched = false;
        foreach ((ToolStripItem item, bool enabled) in autostartPreviousEnabled)
            item.Enabled = enabled;
        autostartPreviousEnabled.Clear();
        SetAutostartButton(false);
    }

    private void EndAutostartForManualTakeover(string reasonKey, bool sendSafety = true)
    {
        if (autostart.Phase == AutostartPhase.Completed)
            return;
        if (sendSafety)
            SendAutostartSafetyControls();
        if (autostart.Phase == AutostartPhase.Completed)
            return;
        autostartControlsOwned = false;
        autostartTimer.Stop();
        StopAutostartReadinessRequest();
        CancelAirportFacilities(preserveRunwayData: true);
        autostart.Cancel();
        StopAutostartCompetingModes();
        FinishAutostartUi();
        Debug.WriteLine($"[Debug] {DateTime.Now:yyyy-MM-dd HH:mm:ss} - [Autostart] Phase=Aborted, ManualTakeover, Reason={reasonKey}");
    }

    private void DisableAutostartPlaneAutopilot()
    {
        if (autostartIntegrationError != null || simconnect is not { } connection || autopilot == null ||
            autostartExpectedRequest == 0 || !ReferenceEquals(connection, autostartDataConnection))
            return;
        try
        {
            autopilot.DisablePlaneAutopilot();
            TrackAutostartPacket(connection, AutostartPacketKind.AutopilotOff, autostartExpectedRequest);
        }
        catch (COMException ex)
        {
            autostartIntegrationError = AutostartText("ControlFailure");
            Debug.WriteLine($"[Debug] {DateTime.Now:yyyy-MM-dd HH:mm:ss} - [Autostart] {ex}");
        }
        catch (InvalidOperationException ex)
        {
            autostartIntegrationError = AutostartText("ControlFailure");
            Debug.WriteLine($"[Debug] {DateTime.Now:yyyy-MM-dd HH:mm:ss} - [Autostart] {ex}");
        }
    }

    private bool ReceiveAutostartException(SimConnect sender, SIMCONNECT_RECV_EXCEPTION data)
    {
        if (!ReferenceEquals(sender, simconnect) || !ReferenceEquals(sender, autostartDataConnection) ||
            !autostartPackets.TryGetValue(data.dwSendID, out var packet))
            return false;

        if (packet.Kind is AutostartPacketKind.GearRetraction or AutostartPacketKind.FlapsRetraction)
        {
            if (packet.SessionId == autostartExpectedRequest)
            {
                AutostartRetractionCheck check = packet.Kind == AutostartPacketKind.GearRetraction
                    ? autostartGearCheck : autostartFlapsCheck;
                if (check.Pending)
                {
                    check.Finished = true;
                    check.Failure = $"SimConnect-Fehler {data.dwException}";
                }
            }
            return true;
        }

        bool definition = packet.Kind == AutostartPacketKind.Definition;
        bool newDefinitionFailure = definition && !autostartDefinitionFailed;
        if (definition)
        {
            autostartDefinitionFailed = true;
            autostartDefinitionRegistered = false;
        }
        // Nach der manuellen Freigabe keine Regler mehr abschalten oder Aktoren
        // betätigen. Späte Fehler eines Startbefehls bleiben als Diagnose sichtbar.
        if (autostart.Phase == AutostartPhase.Completed)
        {
            if (autostartHandoverMonitoring && packet.Kind != AutostartPacketKind.Stop)
                StopAutostartHandoverMonitoring("SimConnect-Fehler nach Übergabe; keine weitere automatische Schaltung.");
            Debug.WriteLine($"[Debug] {DateTime.Now:yyyy-MM-dd HH:mm:ss} - [Autostart] Late start exception after manual release: {packet.Kind}, SimConnect exception={data.dwException}, SendID={data.dwSendID}, Index={data.dwIndex}");
            return true;
        }
        bool active = (IsAutostartActive || autostartGroundProbePending) &&
            (autostartGroundProbePending || !autostartErrorLatched || autostartControlsOwned) &&
            (definition || (autostartExpectedRequest != 0 && packet.SessionId == autostartExpectedRequest &&
                packet.Kind != AutostartPacketKind.Stop));
        bool newIntegrationFailure = active && autostartIntegrationError == null;
        if (newIntegrationFailure)
            autostartIntegrationError = AutostartText(packet.Kind is AutostartPacketKind.AutopilotOff or AutostartPacketKind.Control
                ? "ControlFailure" : "DataUnavailable");
        if (newIntegrationFailure && autostartControlsOwned)
        {
            if (!AutostartAirborneSafety)
                autostart.Brake("ControlFailure");
            SendAutostartSafetyControls();
            if (AutostartAirborneSafety)
                EndAutostartForManualTakeover("FlightManualTakeover", sendSafety: false);
        }
        if (newDefinitionFailure || newIntegrationFailure)
            Debug.WriteLine($"[Debug] {DateTime.Now:yyyy-MM-dd HH:mm:ss} - [Autostart] {packet.Kind}: SimConnect exception={data.dwException}, SendID={data.dwSendID}, Index={data.dwIndex}");
        return true;
    }

    private bool ReceiveAutostartReadiness(SIMCONNECT_RECV_SIMOBJECT_DATA data)
    {
        if (data.dwDefineID != (uint)AutostartDefinitionId.Readiness)
            return false;
        if ((!IsAutostartActive && !autostartGroundProbePending && !autostartHandoverMonitoring && !AutostartRetractionPending) ||
            (!autostartGroundProbePending && autostartErrorLatched && !autostartControlsOwned) ||
            data.dwRequestID != autostartExpectedRequest)
            return true;
        if (autostartGroundProbePending)
        {
            DateTime receivedUtc = DateTime.UtcNow;
            bool ground = receivedUtc < autostartGroundProbeDeadlineUtc && autostartIntegrationError == null &&
                ReferenceEquals(simconnect, autostartDataConnection) &&
                data.dwData.Length == 1 && data.dwData[0] is AutostartReadinessData probe &&
                AutostartControlOutput.IsFreshGroundSample(probe.OnGround, receivedUtc, receivedUtc) &&
                !string.IsNullOrWhiteSpace(probe.Title);
            StopAutostartGroundProbe();
            if (ground)
                BeginAutostartReadiness();
            else
                Debug.WriteLine($"[Debug] {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} - [Autostart] {AutostartGateBlockedText}");
            return true;
        }
        if (data.dwData.Length != 1 || data.dwData[0] is not AutostartReadinessData sample)
        {
            autostartSnapshot = new();
            autostartSimulatorMaster = default;
            autostartIntegrationError = AutostartText("DataUnavailable");
            UpdateAutostartReadiness();
            return true;
        }

        if (autostartObservedTitle != null &&
            !string.Equals(autostartObservedTitle, sample.Title, StringComparison.Ordinal))
        {
            if (AutostartRetractionPending && !autostartControlsOwned)
                autostartSnapshot = new();
            if (autostartControlsOwned)
            {
                autostartSnapshot = new();
                autostartSimulatorMaster = default;
                if (!AutostartAirborneSafety)
                    autostart.Brake("AircraftChanged");
                SendAutostartSafetyControls();
                if (AutostartAirborneSafety)
                    EndAutostartForManualTakeover("FlightManualTakeover", sendSafety: false);
            }
            return true;
        }
        if (autostartAircraftTitle != null &&
            !string.Equals(autostartAircraftTitle, sample.Title, StringComparison.Ordinal))
        {
            RestartAutostartReadiness();
            return true;
        }
        DateTime nowUtc = DateTime.UtcNow;
        AutostartValue<double> number(double value) => new(value, nowUtc, true);
        autostartSnapshot = new()
        {
            SessionId = autostartExpectedRequest,
            Title = new(sample.Title, nowUtc, true),
            NumberOfEngines = number(sample.EngineCount),
            RotationSpeedKnots = number(sample.RotationSpeed),
            ClimbSpeedKnots = number(sample.ClimbSpeed),
            StallSpeedLandingKnots = number(sample.StallLanding),
            StallSpeedCleanKnots = number(sample.StallClean),
            CruiseSpeedKnots = number(sample.CruiseSpeed),
            OnGround = number(sample.OnGround),
            IndicatedAirspeedKnots = number(sample.Airspeed),
            GroundSpeedKnots = number(sample.GroundSpeed),
            IndicatedAltitudeFeet = number(sample.Altitude),
            VerticalSpeedFeetPerMinute = number(sample.VerticalSpeed),
            PitchDegrees = number(sample.Pitch),
            BankDegrees = number(sample.Bank),
            TrueHeadingDegrees = number(sample.TrueHeading),
            LatitudeDegrees = number(sample.Latitude),
            LongitudeDegrees = number(sample.Longitude),
            EngineCombustion1 = number(sample.Combustion1),
            EngineCombustion2 = number(sample.Combustion2),
            EngineCombustion3 = number(sample.Combustion3),
            EngineCombustion4 = number(sample.Combustion4),
            RudderPercent = number(sample.RudderPercent),
            ElevatorPercent = number(sample.ElevatorPercent),
            AboveGroundFeet = number(sample.AboveGroundFeet),
            MagneticHeadingDegrees = number(sample.MagneticHeading),
            AileronPercent = number(sample.AileronPercent),
            LeftBrakePosition = number(sample.LeftBrakePosition),
            RightBrakePosition = number(sample.RightBrakePosition),
            IsGearRetractable = number(sample.IsGearRetractable),
            GearTotalPosition = number(sample.GearTotalPosition),
            GearCenterPosition = number(sample.GearCenterPosition),
            GearLeftPosition = number(sample.GearLeftPosition),
            GearRightPosition = number(sample.GearRightPosition),
            TrailingFlapsLeft = number(sample.TrailingFlapsLeft),
            TrailingFlapsRight = number(sample.TrailingFlapsRight),
            LeadingFlapsLeft = number(sample.LeadingFlapsLeft),
            LeadingFlapsRight = number(sample.LeadingFlapsRight),
            EngineThrottles = [number(sample.Throttle1), number(sample.Throttle2),
                number(sample.Throttle3), number(sample.Throttle4)]
        };
        autostartSimulatorMaster = number(sample.AutopilotMaster);
        autostartAircraftTitle = sample.Title;
        autostart.ObserveAirborne(autostartSnapshot, nowUtc);
        CompleteAutostartAtLiftoff(nowUtc);
        UpdateAutostartHandoverMonitoring(nowUtc);
        UpdateAutostartRetractionChecks(nowUtc);

        return true;
    }

    private void ObserveAutostartDataItem(DataItem item)
    {
        if (!IsAutostartActive || (autostartErrorLatched && !autostartControlsOwned))
            return;
        if (ReferenceEquals(item, Flight_Data.AUTOPILOT_MASTER))
        {
            double master = item.Value switch
            {
                double value => value,
                float value => value,
                int value => value,
                uint value => value,
                bool value => value ? 1.0 : 0.0,
                _ => double.NaN
            };
            autostartSimulatorMaster = new(master, item.LastUpdated, true);
        }
        if (ReferenceEquals(item, Flight_Model.TITLE))
        {
            autostartObservedTitle = item.Value as string;
            if (autostartSnapshot.Title.HasValue &&
                !string.Equals(autostartSnapshot.Title.Value, autostartObservedTitle, StringComparison.Ordinal))
            {
                RestartAutostartReadiness();
            }
        }
    }

    private readonly Dictionary<ToolStripItem, bool> autostartPreviousEnabled = [];
    private string? autostartLastBlock;

    private ToolStripItem[] AutostartLockedItems =>
    [
        TsbFollow, tsbAP, tsbALT, TsbATT, tsbHDG, TsbLVL, tsbAutoThrottle,
        TsbAutoElevation, tsbAutoland, tstbHeading, tstbAltitude, GetCurrHDG, GetCurrALT
    ];

    private void InitializeAutostartControls()
    {
        tsbTakeOff.AccessibleName = AutostartText("TakeoffName");
        autoStartDialog.Visible = false;
        autoStartDialog.Acknowledged -= AutostartBlockedAcknowledged;
        autoStartDialog.Acknowledged += AutostartBlockedAcknowledged;
        Disposed -= AutostartControlsDisposed;
        Disposed += AutostartControlsDisposed;
        SetAutostartButton(false);
    }

    private void AutostartControlsDisposed(object? sender, EventArgs e)
    {
        CancelAutostart(closing: true);
        autostartTimer.Stop();
        CancelAirportFacilities();
        StopAutostartReadinessRequest();
        autostart.Cancel();
        autoStartDialog.Acknowledged -= AutostartBlockedAcknowledged;
        autostartTimer.Tick -= AutostartTimer_Tick;
        Disposed -= AutostartControlsDisposed;
    }

    private void SetAutostartButton(bool active)
    {
        tsbTakeOff.Checked = active;
        tsbTakeOff.BackColor = active ? System.Drawing.Color.LightGreen : System.Drawing.Color.LightGray;
        tsbTakeOff.Text = AutostartText(active ? "AbortStart" : "TakeoffName");
    }

    private void StopAutostartCompetingModes()
    {
        SetWaypointFollowing(false);
        terrainFollow.SetEnabled(false);
        _isLocalAutopilotActive = false;
        _isALTActive = false;
        _isATTActive = false;
        _isHDGActive = false;
        _isLVLActive = false;
        _isAutoThrottleActive = false;
        foreach (ToolStripButton button in AutostartLockedItems.OfType<ToolStripButton>())
        {
            button.Checked = false;
            button.BackColor = System.Drawing.Color.LightGray;
        }
        try
        {
            autopilot?.AutoThrottleOnOff(false);
            autopilot?.AltOnOff(false);
            autopilot?.AttOnOff(false);
            autopilot?.HdgOnOff(false);
            autopilot?.LvlOnOff(false);
            autopilot?.AutopilotLocalOnOff(false);
        }
        catch (COMException ex)
        {
            // Ein nicht sicher abgeschaltetes Reglerobjekt darf nach Abbruch nicht wieder anlaufen.
            autopilot = null;
            autostartIntegrationError = AutostartText("ControlFailure");
            Debug.WriteLine($"[Debug] {DateTime.Now:yyyy-MM-dd HH:mm:ss} - [Autostart] {ex}");
        }
        catch (InvalidOperationException ex)
        {
            autopilot = null;
            autostartIntegrationError = AutostartText("ControlFailure");
            Debug.WriteLine($"[Debug] {DateTime.Now:yyyy-MM-dd HH:mm:ss} - [Autostart] {ex}");
        }
    }

    private void TsbTakeOff_Click(object? sender, EventArgs e)
    {
        Debug.WriteLine($"[Debug] {DateTime.Now:yyyy-MM-dd HH:mm:ss} - [Autostart] Auto-Start Click");
        if (autostartGroundProbePending)
        {
            // Autostart-Ground-Probe abbrechen, wenn noch ausstehend.
            StopAutostartGroundProbe();
            return;
        }
        if (IsAutostartActive)
        {
            // Autostart abbrechen, wenn bereits aktiv.
            CancelAutostart();
            return;
        }
        if (simconnect == null || autopilot == null || relocationInProgress || relocationControlsSuspended)
        {
            // Autostart nur starten, wenn SimConnect und Autopilot verfügbar sind.
            Debug.WriteLine($"[Debug] {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} - [Autostart] {AutostartGateBlockedText}");
            return;
        }
        // Autostart nur starten, wenn die Reglerdaten aktuell sind.
        DateTime nowUtc = DateTime.UtcNow;
        bool normalFresh = TryAutostartRegulatorValue(Flight_Data.SIM_ON_GROUND, nowUtc, true, out double onGround);
        // DataItems überleben Verbindungen; im Zweifel zuerst ein Paket dieser Verbindung anfordern.
        normalFresh &= ReferenceEquals(simconnect, autostartDataConnection);
        DateTime groundSampleUtc = normalFresh ? Flight_Data.SIM_ON_GROUND.LastUpdated : default;
        AutostartValue<double> ground = autostartSnapshot.OnGround;
        if (ReferenceEquals(simconnect, autostartDataConnection) && ground.HasValue &&
            ground.LastUpdatedUtc.Kind == DateTimeKind.Utc && ground.LastUpdatedUtc <= nowUtc &&
            (nowUtc - ground.LastUpdatedUtc).TotalSeconds <= 2 && ground.LastUpdatedUtc > groundSampleUtc)
        {
            groundSampleUtc = ground.LastUpdatedUtc;
            onGround = ground.Value;
        }
        if (groundSampleUtc != default)
        {
            if (AutostartControlOutput.IsFreshGroundSample(onGround, groundSampleUtc, nowUtc))
                BeginAutostartReadiness();
            else
                Debug.WriteLine($"[Debug] {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} - [Autostart] {AutostartGateBlockedText}");
            return;
        }
        // CHANGED kann am Boden veraltet sein. Die Probe besitzt weder Modi noch Steuerachsen.
        autostartGroundProbePending = true;
        autostartGroundProbeDeadlineUtc = nowUtc.AddSeconds(2);
        autostartIntegrationError = null;
        try
        {
            // Variablen anmelden und Autostart-Ground-Probe anfordern, um die aktuelle Flugzustandsinformation zu erhalten.
            RequestAutostartReadiness();
            // Autostart-Ground-Probe läuft asynchron, daher Timer starten, um die Probe ggf. abzubrechen.
            autostartTimer.Start();
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            // Autostart-Ground-Probe kann nicht angefordert werden, daher sofort abbrechen.
            StopAutostartGroundProbe();
            Debug.WriteLine($"[Debug] {DateTime.Now:yyyy-MM-dd HH:mm:ss} - [Autostart] Ground probe: {ex}");
            Debug.WriteLine($"[Debug] {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} - [Autostart] {AutostartGateBlockedText}");
        }
    }

    private string AutostartGateBlockedText =>
        Resources_DE.ResourceManager.GetString("Autostart.StartGateBlocked", CultureInfo.CurrentUICulture) ?? AutostartText("ReadinessFailed");

    private void StopAutostartGroundProbe()
    {
        if (!autostartGroundProbePending)
            return;
        autostartGroundProbePending = false;
        autostartTimer.Stop();
        StopAutostartReadinessRequest();
    }

    private void BeginAutostartReadiness()
    {
        if (simconnect == null || autopilot == null || relocationInProgress || relocationControlsSuspended)
        {
            // Autostart nur starten, wenn SimConnect und Autopilot verfügbar sind.
            Debug.WriteLine($"[Debug] {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} - [Autostart] {AutostartGateBlockedText}");
            return;
        }
        if (!autostart.Begin())
            // Autostart kann nicht gestartet werden, daher sofort abbrechen.
            return;

        Debug.WriteLine($"[Debug] {DateTime.Now:yyyy-MM-dd HH:mm:ss} - [Autostart] Prüfe Bereitschaft");
        // Autostart-UI zurücksetzen, um die aktuelle Bereitschaft zu prüfen.
        autoStartDialog.Visible = false;                      // Blockierte UI ausblenden, um die aktuelle Bereitschaft zu prüfen.
        autostartErrorLatched = false;                              // Fehlerstatus zurücksetzen, um die aktuelle Bereitschaft zu prüfen.
        CancelAirportFacilities();                                  // Flughafeninformationen zurücksetzen, um die aktuelle Bereitschaft zu prüfen.
        airportText = null;                                         // Flughafeninformationen zurücksetzen, um die aktuelle Bereitschaft zu prüfen.
        autostartSnapshot = new();                                  // Autostart-Status zurücksetzen, um die aktuelle Bereitschaft zu prüfen.
        autostartSimulatorMaster = default;                         
        autostartAircraftTitle = null;                              
        autostartObservedTitle = Flight_Model.TITLE.Value as string;
        autostartIntegrationError = null;                           
        autostartLastBlock = null;                                  
        autostartLastDisableSampleUtc = default;
        autostartCancelPending = false;
        autostartRollStatus = null;
        autostartLastFlightSendUtc = default;
        autostartLastFlightDiagnosticUtc = default;
        autostartLastFlightDiagnosticPhase = null;
        autostartLastFlightDiagnosticNegativeVs = null;
        autostartLastFlightDiagnosticPitchLimited = false;
        autostartLastFlightDiagnosticSpeedLimited = false;
        autostartReadinessDeadlineUtc = DateTime.UtcNow.AddSeconds(45);
        autostartPreviousEnabled.Clear();
        // Autostart Button auf light-green setzen, um den laufenden Autostart zu signalisieren. Die UI wird im Timer-Event aktualisiert.
        SetAutostartButton(true);
        if (simconnect == null || autopilot == null || relocationInProgress || relocationControlsSuspended)
        {
            // Autostart nur starten, wenn SimConnect und Autopilot verfügbar sind.
            ShowAutostartBlocked(autostart.Evaluate(autostartSnapshot, airportRunwayData, DateTime.UtcNow),
                AutostartText(relocationInProgress || relocationControlsSuspended ? "RelocationBlocked" :
                    simconnect == null ? "Disconnected" : "ControlFailure"));
            return;
        }
        foreach (ToolStripItem item in AutostartLockedItems)
        {
            // Vorherigen Aktivierungsstatus merken, um ihn nach Autostart wiederherzustellen.
            autostartPreviousEnabled.Add(item, item.Enabled);
            item.Enabled = false;
        }
        // Autostart-UI zurücksetzen, um die aktuelle Bereitschaft zu prüfen.
        StopAutostartCompetingModes();
        try
        {
            if (autopilot != null)
            {
                // Variablen anmelden und Autostart-Bereitschaft anfordern, um die aktuelle Flugzustandsinformation zu erhalten.
                RequestAutostartReadiness();
                // Autostart-Bereitschaft läuft asynchron, daher Timer starten, um die Bereitstellung ggf. abzubrechen.
                DisableAutostartPlaneAutopilot();
            }
        }
        catch (COMException ex)
        {
            //
            autostartIntegrationError = AutostartText("DataUnavailable");
            Debug.WriteLine($"[Debug] {DateTime.Now:yyyy-MM-dd HH:mm:ss} - [Autostart] {ex}");
        }
        catch (InvalidOperationException ex)
        {
            autostartIntegrationError = AutostartText("DataUnavailable");
            Debug.WriteLine($"[Debug] {DateTime.Now:yyyy-MM-dd HH:mm:ss} - [Autostart] {ex}");
        }
        // Flughafeninformationen zurücksetzen, um die aktuelle Bereitschaft zu prüfen.
        ResetAirportFacilities();
        // Autostart-Timer läuft, um die Autostart-Bereitschaft zu prüfen und ggf. abzubrechen.
        autostartTimer.Start();
        // Autostart-UI aktualisieren, um die aktuelle Bereitschaft zu prüfen.
        UpdateAutostartReadiness();
    }

    private void RestartAutostartReadiness()
    {
        if (autostartControlsOwned)
        {
            autostartSnapshot = new();
            autostartSimulatorMaster = default;
            if (!AutostartAirborneSafety)
                autostart.Brake("AircraftChanged");
            SendAutostartSafetyControls();
            if (AutostartAirborneSafety)
                EndAutostartForManualTakeover("FlightManualTakeover", sendSafety: false);
            return;
        }
        if (autostartErrorLatched)
            return;
        bool wasReadiness = autostart.Phase == AutostartPhase.Readiness;
        StopAutostartReadinessRequest();
        CancelAirportFacilities();
        autostart.Cancel();
        autostart.Begin();
        autostartSnapshot = new();
        autostartSimulatorMaster = default;
        autostartAircraftTitle = null;
        autostartIntegrationError = null;
        autostartLastDisableSampleUtc = default;
        if (!wasReadiness)
            autostartReadinessDeadlineUtc = DateTime.UtcNow.AddSeconds(45);
        try
        {
            RequestAutostartReadiness();
        }
        catch (COMException ex)
        {
            autostartIntegrationError = AutostartText("DataUnavailable");
            Debug.WriteLine($"[Debug] {DateTime.Now:yyyy-MM-dd HH:mm:ss} - [Autostart] {ex}");
        }
        catch (InvalidOperationException ex)
        {
            autostartIntegrationError = AutostartText("DataUnavailable");
            Debug.WriteLine($"[Debug] {DateTime.Now:yyyy-MM-dd HH:mm:ss} - [Autostart] {ex}");
        }
        ResetAirportFacilities();
        UpdateAutostartReadiness();
    }

    private string? GetAutostartBlock(DateTime nowUtc)
    {
        if (relocationInProgress || relocationControlsSuspended)
            return AutostartText("RelocationBlocked");
        if (simconnect == null || autostartConnectionLost)
            return AutostartText("Disconnected");
        if (autostartIntegrationError != null)
            return autostartIntegrationError;
        if (autopilot == null)
            return AutostartText("ControlFailure");
        if (autostartDataConnection != null && !ReferenceEquals(simconnect, autostartDataConnection))
            return AutostartText("DataUnavailable");
        AutostartValue<double> master = autostartSimulatorMaster;
        if (!master.HasValue || master.LastUpdatedUtc == default || master.LastUpdatedUtc.Kind != DateTimeKind.Utc ||
            master.LastUpdatedUtc > nowUtc ||
            (nowUtc - master.LastUpdatedUtc).TotalSeconds > Autostart.MaximumTelemetryAgeSeconds ||
            !double.IsFinite(master.Value) || master.Value is not (0.0 or 1.0))
            return AutostartText("AutopilotUnknown");
        if (master.Value != 0.0)
            return AutostartText("AutopilotStillOn");
        if (!waypointClientReady || webView2.CoreWebView2 == null ||
            !string.Equals(webView2.CoreWebView2.Source, WaypointSource, StringComparison.Ordinal))
            return AutostartText("MapUnavailable");

        return null;
    }

    private void AutostartTimer_Tick(object? sender, EventArgs e)
    {
        try
        {
            if (autostartHandoverMonitoring)
            {
                UpdateAutostartHandoverMonitoring(DateTime.UtcNow);
                UpdateAutostartRetractionChecks(DateTime.UtcNow);
                return;
            }
            // Autostart-Timer läuft, um die Autostart-Bereitschaft zu prüfen und ggf. abzubrechen.
            UpdateAutostartReadiness();
            UpdateAutostartRetractionChecks(DateTime.UtcNow);
        }
        catch (COMException ex)
        {
            // Ein COMException-Fehler beim Autostart ist kritisch, daher sofort abbrechen.
            HandleAutostartExecutionFailure(ex);
        }
        catch (InvalidOperationException ex)
        {
            // Ein InvalidOperationException-Fehler beim Autostart ist kritisch, daher sofort abbrechen.
            HandleAutostartExecutionFailure(ex);
        }
        catch (ArgumentException ex)
        {
            // Ein ArgumentException-Fehler beim Autostart ist kritisch, daher sofort abbrechen.
            HandleAutostartExecutionFailure(ex);
        }
    }

    private void HandleAutostartExecutionFailure(Exception error)
    {
        AutostartControlFailed(error);
        if (autostartControlsOwned)
        {
            SendAutostartSafetyControls();
            if (AutostartAirborneSafety)
                EndAutostartForManualTakeover("FlightManualTakeover", sendSafety: false);
        }
        else
            ShowAutostartBlocked(autostart.Evaluate(autostartSnapshot, airportRunwayData, DateTime.UtcNow),
                AutostartText("ControlFailure"));
    }

    // Prüft die Autostart-Bereitschaft und aktualisiert die Autostart-UI.
    private void UpdateAutostartReadiness()
    {
        // Autostart-Ground-Probe läuft, daher prüfen, ob sie abgebrochen werden muss.
        if (autostartGroundProbePending)
        {
            if (DateTime.UtcNow >= autostartGroundProbeDeadlineUtc || autostartIntegrationError != null ||
                autostartConnectionLost || !ReferenceEquals(simconnect, autostartDataConnection) ||
                relocationInProgress || relocationControlsSuspended)
            {
                // Autostart-Ground-Probe abbrechen, wenn sie abgelaufen ist oder fehlerhaft ist.
                StopAutostartGroundProbe();
                Debug.WriteLine($"[Debug] {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} - [Autostart] {AutostartGateBlockedText}");
            }
            return;
        }
        // Autostart ist nicht aktiv oder es liegt ein Fehler vor, daher keine weiteren Aktionen.
        if (!IsAutostartActive || (autostartErrorLatched && !autostartControlsOwned))
            return;
        // Die normalen Flugregler bleiben bis zur geordneten Übergabe ausgesperrt.
        foreach (ToolStripItem item in autostartPreviousEnabled.Keys)
            item.Enabled = false;
        // Autostart-Fehlerstatus zurücksetzen, um die aktuelle Bereitschaft zu prüfen.
        if (autostartIntegrationError == null && autostartSimulatorMaster.HasValue && autostartSimulatorMaster.Value == 1.0 &&
            autostartSimulatorMaster.LastUpdatedUtc > autostartLastDisableSampleUtc)
        {
            // Autopilot wurde wieder eingeschaltet, daher Autostart abbrechen.
            autostartLastDisableSampleUtc = autostartSimulatorMaster.LastUpdatedUtc;
            // Autopilot wurde wieder eingeschaltet, daher Autostart abbrechen.
            DisableAutostartPlaneAutopilot();
        }
        DateTime nowUtc = DateTime.UtcNow;
        if (autostartControlsOwned)
        {
            // Autostart ist aktiv und die Steuerung wurde übernommen, daher den Startrollvorgang fortsetzen.
            UpdateAutostartRoll(nowUtc);
            return;
        }
        if (simconnect == null || autopilot == null || autostartIntegrationError != null ||
            (autostartDataConnection != null && !ReferenceEquals(simconnect, autostartDataConnection)) ||
            relocationInProgress || relocationControlsSuspended)
        {
            // Autostart ist aktiv, aber es liegt ein Fehler vor, daher den Startrollvorgang abbrechen.
            ShowAutostartBlocked(autostart.Evaluate(autostartSnapshot, airportRunwayData, nowUtc),
                GetAutostartBlock(nowUtc));
            return;
        }
        // Autostart ist aktiv und es liegt kein Fehler vor, daher die Flughafeninformationen aktualisieren.
        UpdateAirportFacilities(nowUtc);
        // Autostart-Blockierungsgrund ermitteln und die Autostart-UI aktualisieren.
        string? block = GetAutostartBlock(nowUtc);
        if (block != autostartLastBlock)
        {
            // Autostart-Blockierungsgrund hat sich geändert, daher die Autostart-UI aktualisieren.
            if (block != AutostartText("AutopilotUnknown"))
                Debug.WriteLine($"[Debug] {nowUtc:yyyy-MM-dd HH:mm:ss} - [Autostart] {block ?? AutostartText("InterlockReady")}");
            autostartLastBlock = block;
            if (autostart.Phase == AutostartPhase.StartRoll)
            {
                // Autostart ist aktiv und die Steuerung wurde noch nicht übernommen, daher den Startrollvorgang fortsetzen.
                if (block == null || block == AutostartText("AutopilotUnknown"))
                    Debug.WriteLine($"[Debug] {nowUtc:yyyy-MM-dd HH:mm:ss} - [Autostart] {block ?? AutostartText("StartRoll")}");
                else
                    Debug.WriteLine($"[Debug] {nowUtc:yyyy-MM-dd HH:mm:ss} - [Autostart] {block}");
            }
        }
        // Autostart-Phase ist nicht Bereitschaft, daher den Startrollvorgang fortsetzen.
        if (autostart.Phase != AutostartPhase.Readiness)
        {
            // Autostart ist aktiv und die Steuerung wurde noch nicht übernommen, daher den Startrollvorgang fortsetzen.
            AutostartEvaluation current = autostart.Evaluate(autostartSnapshot, airportRunwayData, nowUtc);
            if (block != null || current.Checks.Any(check => !check.Passed))
                // Autostart ist aktiv und die Steuerung wurde noch nicht übernommen, daher den Startrollvorgang fortsetzen.
                ShowAutostartBlocked(current, block ?? AutostartText("ReadinessFailed"));
            return;
        }
        if (airportRunwayData.Status is AutostartRunwayDataStatus.Failed or AutostartRunwayDataStatus.Invalid)
        {
            // Autostart ist aktiv und die Steuerung wurde noch nicht übernommen, daher den Startrollvorgang fortsetzen.
            ShowAutostartBlocked(autostart.Evaluate(autostartSnapshot, airportRunwayData, nowUtc),
                block ?? AutostartText("ReadinessFailed"));
            return;
        }
        if (nowUtc >= autostartReadinessDeadlineUtc)
        {
            // Autostart ist aktiv und die Steuerung wurde noch nicht übernommen, daher den Startrollvorgang fortsetzen.
            ShowAutostartBlocked(autostart.Evaluate(autostartSnapshot, airportRunwayData, nowUtc),
                block ?? AutostartText("ReadinessTimeout"));
            return;
        }
        // Das erste vollständige Paket und das Ende der Facility-Abfrage abwarten.
        AutostartValue<string?> title = autostartSnapshot.Title;
        if (!AirportFacilitiesFinished || !title.HasValue ||
            autostartSnapshot.SessionId != autostartExpectedRequest ||
            title.LastUpdatedUtc.Kind != DateTimeKind.Utc || title.LastUpdatedUtc > nowUtc ||
            (nowUtc - title.LastUpdatedUtc).TotalSeconds > Autostart.MaximumTelemetryAgeSeconds)
            return;
        AutostartEvaluation evaluation = autostart.Evaluate(autostartSnapshot, airportRunwayData, nowUtc);
        // AP-Bestätigung und Kartenladen dürfen bis zur bestehenden Bereitschaftsfrist dauern.
        if (evaluation.IsReady && (block == AutostartText("AutopilotUnknown") ||
            block == AutostartText("AutopilotStillOn") || block == AutostartText("MapUnavailable")))
            return;
        if (block != null || !evaluation.IsReady)
        {
            ShowAutostartBlocked(evaluation, block ?? AutostartText("ReadinessFailed"));
            return;
        }
        // Frischen Snapshot und Interlocks unmittelbar vor dem Phasenwechsel erneut prüfen.
        nowUtc = DateTime.UtcNow;
        block = GetAutostartBlock(nowUtc);
        if (block != null ||
            !autostart.TryAdvanceToStartRoll(autostartSnapshot, airportRunwayData, nowUtc, out evaluation))
        {
            ShowAutostartBlocked(autostart.Evaluate(autostartSnapshot, airportRunwayData, nowUtc),
                block ?? AutostartText("ReadinessFailed"));
            return;
        }
        Debug.WriteLine($"[Debug] {DateTime.Now:yyyy-MM-dd HH:mm:ss} - [Autostart] Startfreigabe liegt vor");
        autostartControlsOwned = true;
        autostartInitialClimbController = new();
        autostartLastInitialClimbLogUtc = default;
        autostartFlapsUpSent = false;
        autostartGearUpSent = false;
        autostartGearCheck = new();
        autostartFlapsCheck = new();
        autostartRetractionMonitoring = false;
        autostartRetractionWarningLogged = false;
        autostartHandoverMonitoring = false;
        autostartHandoverTitle = null;
        SendAutostartControls(0.0, true, 0);
        if (autostartIntegrationError != null)
            return;
        try
        {
            SimConnect connection = simconnect ?? throw new InvalidOperationException("Disconnected");
            connection.TransmitClientEvent(SimConnect.SIMCONNECT_OBJECT_ID_USER, AutostartEventId.FLAPS_1,
                0u, AutostartPriority.Highest, SIMCONNECT_EVENT_FLAG.GROUPID_IS_PRIORITY);
            TrackAutostartPacket(connection, AutostartPacketKind.Control, autostartExpectedRequest);
            Debug.WriteLine($"[Debug] {nowUtc:yyyy-MM-dd HH:mm:ss} - [Autostart] {AutostartEventId.FLAPS_1} angefordert");
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            AutostartControlFailed(ex);
            return;
        }
        CancelAirportFacilities(preserveRunwayData: true);
        autoStartDialog.Visible = false;
        webView2.Visible = true;
        webView2.Focus();
        Debug.WriteLine($"[Debug] {DateTime.Now:yyyy-MM-dd HH:mm:ss} - [Autostart] Startablauf beginnt");
        UpdateAutostartRoll(nowUtc);
    }

    private void ShowAutostartBlocked(AutostartEvaluation evaluation, string? blockingReason)
    {
        if (autostartErrorLatched)
            return;
        autoStartDialog.UpdateStatus(evaluation, blockingReason,
            airportText ?? AutostartText("Unavailable"));
        autoStartDialog.Visible = true;
        autoStartDialog.BringToFront();
        autoStartDialog.Focus();
        autostartErrorLatched = true;
        if (!autostartControlsOwned)
        {
            autostartTimer.Stop();
            StopAutostartReadinessRequest();
        }
        CancelAirportFacilities(preserveRunwayData: true);
        if (blockingReason != autostartLastBlock)
        {
            if (blockingReason != AutostartText("AutopilotUnknown"))
                Debug.WriteLine($"[Debug] {DateTime.Now:yyyy-MM-dd HH:mm:ss} - [Autostart] {blockingReason}");
            autostartLastBlock = blockingReason;
        }
    }

    private void AutostartBlockedAcknowledged(object? sender, EventArgs e)
    {
        if (autostartErrorLatched)
            CancelAutostart();
    }

    private void StopAutostartReadinessRequest()
    {
        autostartRetractionMonitoring = false;
        autostartGearCheck.Finished = true;
        autostartFlapsCheck.Finished = true;
        uint request = autostartExpectedRequest;
        autostartExpectedRequest = 0;
        if (autostartConnectionLost || autostartDataConnection == null || request == 0 ||
            !ReferenceEquals(simconnect, autostartDataConnection))
            return;
        try
        {
            autostartDataConnection.RequestDataOnSimObject((AutostartRequestId)request,
                AutostartDefinitionId.Readiness, SimConnect.SIMCONNECT_OBJECT_ID_USER,
                SIMCONNECT_PERIOD.NEVER, SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT, 0, 0, 0);
            TrackAutostartPacket(autostartDataConnection, AutostartPacketKind.Stop, request);
        }
        catch (COMException ex)
        {
            Debug.WriteLine($"[Debug] {DateTime.Now:yyyy-MM-dd HH:mm:ss} - [Autostart] {ex}");
        }
        catch (InvalidOperationException ex)
        {
            Debug.WriteLine($"[Debug] {DateTime.Now:yyyy-MM-dd HH:mm:ss} - [Autostart] {ex}");
        }
    }

    private void CancelAutostart(bool connectionLost = false, bool closing = false, bool preserveModes = false)
    {
        autostartConnectionLost |= connectionLost;
        if (autostartHandoverMonitoring)
            StopAutostartHandoverMonitoring("Abbruch oder Verbindungsende.");
        StopAutostartGroundProbe();
        if (preserveModes && autostartControlsOwned)
        {
            // AutoLand übernimmt nur die Planung, nicht Schub oder Reglerzustände.
            SendAutostartControls([], false, 0, release: true, preserveThrottle: true, allowInitialClimb: true);
            autostartControlsOwned = false;
        }
        if (autostartControlsOwned && AutostartAirborneSafety)
        {
            EndAutostartForManualTakeover("FlightManualTakeover");
            return;
        }
        if (autostartControlsOwned && !connectionLost)
        {
            if (!closing && autostart.Phase != AutostartPhase.Stopped)
            {
                autostartCancelPending = true;
                autostart.Brake("CancelledBraking");
                SendAutostartControls(0.0, true, 0);
                Debug.WriteLine($"[Debug] {DateTime.Now:yyyy-MM-dd HH:mm:ss} - [Autostart] {AutostartText("CancelledBraking")}");
                return;
            }
            bool stopped = autostart.Phase == AutostartPhase.Stopped;
            SendAutostartControls(0.0, !stopped, 0, release: stopped);
        }
        autostartControlsOwned = false;
        autostartCancelPending = false;
        autostartTimer.Stop();
        CancelAirportFacilities();
        bool wasActive = IsAutostartActive;
        autostart.Cancel();
        autoStartDialog.Visible = false;
        autostartErrorLatched = false;
        StopAutostartReadinessRequest();
        if (wasActive && autostartPreviousEnabled.Count != 0 && !autostartConnectionLost && !preserveModes)
            StopAutostartCompetingModes();
        autostartSnapshot = new();
        autostartSimulatorMaster = default;
        foreach ((ToolStripItem item, bool enabled) in autostartPreviousEnabled)
            item.Enabled = enabled;
        autostartPreviousEnabled.Clear();
        SetAutostartButton(false);
        if (wasActive)
            Debug.WriteLine($"[Debug] {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} - [Autostart] {AutostartText("Aborted")}");
    }
}