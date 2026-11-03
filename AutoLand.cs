namespace AutoFly;

/// <summary>AutoLand-Phasen für Planung, Markierung und Führung bis P5.</summary>
public enum AutoLandPhase { Inactive, Planning, Marking, Aligning }

/// <summary>Position in Grad.</summary>
public sealed record AutoLandPosition(double LatitudeDegrees, double LongitudeDegrees);

/// <summary>SimConnect-Bahngeometrie mit Höhe über Meer in Metern.</summary>
public sealed record AutoLandRunway(string PrimaryIdentifier, string SecondaryIdentifier,
    AutoLandPosition Center, double HeadingTrueDegrees, double LengthMeters, double WidthMeters,
    double AltitudeMeters, int SurfaceType = 0);

/// <summary>Vom Benutzer aus der Umgebungssuche ausgewählter Flughafen.</summary>
public sealed record AutoLandAirport(string Icao, string Name, AutoLandPosition Position,
    IReadOnlyList<AutoLandRunway> Runways);

/// <summary>Planungseingabe mit den Geschwindigkeiten des aktiven Modells in Knoten.</summary>
public sealed record AutoLandPlanningInput(AutoLandAirport Airport, AutoLandPosition AircraftPosition,
    double ClimbSpeedKnots, double CruiseSpeedKnots, bool WaterLanding = false);

/// <summary>Markierung mit absoluter Höhe in Fuß und Entfernung vor der Anflugschwelle.</summary>
public sealed record AutoLandPoint(string Label, AutoLandPosition Position, double AltitudeFeet,
    double DistanceBeforeThresholdMeters);

/// <summary>Reiner Markierungsplan. PA liegt eine Seemeile vor P1 auf gleicher Höhe.</summary>
public sealed record AutoLandPlan(AutoLandAirport Airport, AutoLandRunway Runway,
    string SelectedDirection, double CourseTrueDegrees, AutoLandPosition SearchReference,
    double DistanceToP1Meters, IReadOnlyList<AutoLandPoint> Points);

/// <summary>Unveränderlicher Zustand mit Sitzungsschutz gegen verspätete Ergebnisse.</summary>
public sealed record AutoLandState(long Session = 0, AutoLandPhase Phase = AutoLandPhase.Inactive,
    AutoLandPlan? Plan = null);

/// <summary>Explizite Benutzer- beziehungsweise Planungseingabe.</summary>
public enum AutoLandAction { Begin, Mark, Cancel, Align }

/// <summary>Ein- und Ausgabe bleiben ohne UI oder Simulator testbar.</summary>
public sealed record AutoLandInput(AutoLandAction Action, long Session = 0, AutoLandPlan? Plan = null);

/// <summary>Startfreigabe nach der Markierung; fehlende Daten verwerfen die Sitzung nicht.</summary>
public sealed record AutoLandAlignmentStartInput(AutoLandState State, long Session,
    bool AutopilotReady, bool TelemetryReady);

public sealed record AutoLandAlignmentStartOutput(bool Start, bool Waiting);

/// <summary>Reine AutoLand-Zustandsübergänge und sphärische Bahnplanung ohne Flugregelung.</summary>
public static class AutoLand
{
    private const double EarthRadiusMeters = 6371000.0;
    private const double MetersPerNauticalMile = 1852.0;

    /// <summary>Sitzungsschutz für Planung, Markierung und Ausrichtung bis zum manuellen Abbruch.</summary>
    public static AutoLandState Update(AutoLandState state, AutoLandInput input)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(input);
        return input.Action switch
        {
            AutoLandAction.Begin when state.Phase == AutoLandPhase.Inactive =>
                new(checked(state.Session + 1), AutoLandPhase.Planning),
            AutoLandAction.Cancel when input.Session == state.Session => new(state.Session),
            AutoLandAction.Mark when state.Phase == AutoLandPhase.Planning &&
                input.Session == state.Session && input.Plan != null =>
                state with { Phase = AutoLandPhase.Marking, Plan = input.Plan },
            AutoLandAction.Align when state.Phase == AutoLandPhase.Marking &&
                input.Session == state.Session && state.Plan != null =>
                state with { Phase = AutoLandPhase.Aligning },
            _ => state
        };
    }

    /// <summary>Startet nur die aktuelle markierte Sitzung; vorübergehende Datenlücken bleiben wartend.</summary>
    public static AutoLandAlignmentStartOutput GetAlignmentStart(AutoLandAlignmentStartInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.State);
        bool pending = input.State.Phase == AutoLandPhase.Marking && input.State.Plan != null &&
            input.State.Session == input.Session;
        bool start = pending && input.AutopilotReady && input.TelemetryReady;
        return new(start, pending && !start);
    }

    /// <summary>Filtert nutzbare Bahnen und ordnet Flughäfen wie Pomax nach ihrer längsten Bahn, ohne Mindestlandestrecke.</summary>
    public static IReadOnlyList<AutoLandAirport> GetLandingCandidates(IEnumerable<AutoLandAirport> airports,
        bool waterLanding) => GetLandingCandidates(airports, waterLanding, false);

    /// <summary>Erkennt die vom Simulator gelieferte Hubschrauberkategorie; fehlende Kategorien gewähren keine Ausnahme.</summary>
    public static bool IsHelicopter(string? aircraftCategory) =>
        string.Equals(aircraftCategory?.Trim(), "Helicopter", StringComparison.OrdinalIgnoreCase);

    /// <summary>Erlaubt Hubschraubern zusätzlich vollständig geladene Flughäfen ohne Landebahn.</summary>
    public static IReadOnlyList<AutoLandAirport> GetLandingCandidates(IEnumerable<AutoLandAirport> airports,
        bool waterLanding, bool helicopter)
    {
        ArgumentNullException.ThrowIfNull(airports);
        List<AutoLandAirport> candidates = [];
        foreach (AutoLandAirport airport in airports)
        {
            ArgumentNullException.ThrowIfNull(airport);
            ArgumentNullException.ThrowIfNull(airport.Runways);
            if (string.IsNullOrWhiteSpace(airport.Icao) || string.IsNullOrWhiteSpace(airport.Name))
                continue;
            AutoLandRunway[] runways = [.. airport.Runways.Where(runway => IsUsableRunway(runway, waterLanding))];
            if (runways.Length > 0 || (helicopter && airport.Runways.Count == 0))
                candidates.Add(airport with { Runways = Array.AsReadOnly(runways) });
        }

        return Array.AsReadOnly(candidates.OrderByDescending(airport => airport.Runways.Select(runway => runway.LengthMeters).DefaultIfEmpty(0.0).Max())
            .ThenBy(airport => airport.Icao, StringComparer.Ordinal).ToArray());
    }

    private static bool IsUsableRunway(AutoLandRunway runway, bool waterLanding) =>
        runway is not null && runway.Center is not null &&
        double.IsFinite(runway.Center.LatitudeDegrees) && Math.Abs(runway.Center.LatitudeDegrees) <= 90.0 &&
        double.IsFinite(runway.Center.LongitudeDegrees) && Math.Abs(runway.Center.LongitudeDegrees) <= 180.0 &&
        double.IsFinite(runway.LengthMeters) && runway.LengthMeters > 0.0 &&
        double.IsFinite(runway.WidthMeters) && runway.WidthMeters > 0.0 &&
        double.IsFinite(runway.HeadingTrueDegrees) && double.IsFinite(runway.AltitudeMeters) &&
        (waterLanding || runway.SurfaceType is not (2 or >= 26 and <= 31));

    /// <summary>Wählt die längste nutzbare Bahn und bei gleicher Länge den nächstgelegenen Anflug zu P1.</summary>
    public static AutoLandPlan CreatePlan(AutoLandPlanningInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.Airport);
        ArgumentNullException.ThrowIfNull(input.Airport.Runways);
        ValidatePosition(input.AircraftPosition);
        ValidatePosition(input.Airport.Position);
        RequirePositive(input.ClimbSpeedKnots, nameof(input.ClimbSpeedKnots));
        RequirePositive(input.CruiseSpeedKnots, nameof(input.CruiseSpeedKnots));
        if (input.Airport.Runways.Count == 0)
            throw new ArgumentException("Der Flughafen muss mindestens eine Start-/Landebahn enthalten.", nameof(input));

        AutoLandPlan? best = null;
        foreach (AutoLandRunway runway in input.Airport.Runways)
        {
            ArgumentNullException.ThrowIfNull(runway);
            ValidatePosition(runway.Center);
            RequirePositive(runway.LengthMeters, nameof(runway.LengthMeters));
            RequirePositive(runway.WidthMeters, nameof(runway.WidthMeters));
            if (!double.IsFinite(runway.HeadingTrueDegrees) || !double.IsFinite(runway.AltitudeMeters))
                throw new ArgumentException(null, nameof(input));
            if (!IsUsableRunway(runway, input.WaterLanding))
                continue;
            for (int direction = 0; direction < 2; direction++)
            {
                AutoLandPlan candidate = CreateDirection(input, runway, direction);
                if (best == null || runway.LengthMeters > best.Runway.LengthMeters ||
                    (runway.LengthMeters == best.Runway.LengthMeters && candidate.DistanceToP1Meters < best.DistanceToP1Meters))
                    best = candidate;
            }
        }
        return best ?? throw new ArgumentException(null, nameof(input));
    }

    private static AutoLandPlan CreateDirection(AutoLandPlanningInput input, AutoLandRunway runway, int direction)
    {
        double course = Normalize(runway.HeadingTrueDegrees + direction * 180.0);
        // Alle Punkte verwenden denselben Großkreis durch die Bahnmitte, auch bei hohen Breitengraden.
        // Die Abfangkurve muss bis 20 % Bahnlänge stetig flacher werden können.
        double glideSlope = Math.Tan(3.0 * Math.PI / 180.0);
        double touchdown = runway.LengthMeters * 0.2;
        double p3 = Math.Min((500.0 - 60.0) * 0.3048 / glideSlope, 440.0 * touchdown / 120.0);
        double p2 = p3 + (1500.0 - 500.0) * 0.3048 / glideSlope;
        double p1 = p2 + 1.4 * MetersPerNauticalMile;
        double pa = p1 + MetersPerNauticalMile;
        double height = runway.AltitudeMeters / 0.3048;
        AutoLandPosition before(double distance) => Destination(runway.Center, course + 180.0,
            runway.LengthMeters / 2.0 + distance);
        AutoLandPosition first = before(p1);
        AutoLandPoint[] points =
        [
            new("PA", before(pa), height + 1500.0, pa),
            new("P1", first, height + 1500.0, p1),
            new("P2", before(p2), height + 1500.0 - 1000.0 * (p1 - p2) / (p1 - p3), p2),
            new("P3", before(p3), height + 500.0, p3),
            new("P4", before(0.0), height + 60.0, 0.0),
            new("P5", Destination(runway.Center, course, runway.LengthMeters / 2.0), height, -runway.LengthMeters)
        ];
        return new(input.Airport, runway,
            direction == 0 ? runway.PrimaryIdentifier : runway.SecondaryIdentifier,
            course, input.AircraftPosition, DistanceMeters(input.AircraftPosition, first),
            Array.AsReadOnly(points));
    }

    /// <summary>Berechnet die Großkreisentfernung in Metern ohne Entfernungsbeschränkung.</summary>
    public static double DistanceMeters(AutoLandPosition from, AutoLandPosition to)
    {
        ValidatePosition(from);
        ValidatePosition(to);
        double lat1 = Radians(from.LatitudeDegrees);
        double lat2 = Radians(to.LatitudeDegrees);
        double dlat = lat2 - lat1;
        double dlon = Radians(to.LongitudeDegrees - from.LongitudeDegrees);
        double a = Math.Pow(Math.Sin(dlat / 2.0), 2) +
            Math.Cos(lat1) * Math.Cos(lat2) * Math.Pow(Math.Sin(dlon / 2.0), 2);
        return 2.0 * EarthRadiusMeters * Math.Asin(Math.Sqrt(Math.Clamp(a, 0.0, 1.0)));
    }

    /// <summary>Berechnet eine Position auf einem Großkreis aus wahrem Anfangskurs und Metern.</summary>
    public static AutoLandPosition Destination(AutoLandPosition origin, double courseTrueDegrees, double distanceMeters)
    {
        ValidatePosition(origin);
        if (!double.IsFinite(courseTrueDegrees) || !double.IsFinite(distanceMeters))
            throw new ArgumentException(null, nameof(distanceMeters));
        double lat = Radians(origin.LatitudeDegrees);
        double lon = Radians(origin.LongitudeDegrees);
        double bearing = Radians(courseTrueDegrees);
        double angle = distanceMeters / EarthRadiusMeters;
        double resultLat = Math.Asin(Math.Clamp(Math.Sin(lat) * Math.Cos(angle) +
            Math.Cos(lat) * Math.Sin(angle) * Math.Cos(bearing), -1.0, 1.0));
        double resultLon = lon + Math.Atan2(Math.Sin(bearing) * Math.Sin(angle) * Math.Cos(lat),
            Math.Cos(angle) - Math.Sin(lat) * Math.Sin(resultLat));
        return new(resultLat * 180.0 / Math.PI, (resultLon * 180.0 / Math.PI + 540.0) % 360.0 - 180.0);
    }

    private static void ValidatePosition(AutoLandPosition position)
    {
        ArgumentNullException.ThrowIfNull(position);
        if (!double.IsFinite(position.LatitudeDegrees) || Math.Abs(position.LatitudeDegrees) > 90.0 ||
            !double.IsFinite(position.LongitudeDegrees) || Math.Abs(position.LongitudeDegrees) > 180.0)
            throw new ArgumentOutOfRangeException(nameof(position));
    }

    private static void RequirePositive(double value, string name)
    {
        if (!double.IsFinite(value) || value <= 0.0)
            throw new ArgumentOutOfRangeException(name);
    }

    private static double Radians(double value) => value * Math.PI / 180.0;
    private static double Normalize(double value) => (value % 360.0 + 360.0) % 360.0;
}

/// <summary>Kurvenauswertung entlang der Bahn; Längskoordinate ab P4 vorwärts positiv.</summary>
public sealed record AutoLandCurveInput(AutoLandPlan Plan, double AlongMeters,
    double PaEntrySpeedKnots, double ClimbSpeedKnots);

/// <summary>Bahrelative Höhe, analytische Steigung in ft/m und gestaffelter IAS-Sollwert.</summary>
public sealed record AutoLandCurveOutput(double HeightAboveRunwayFeet, double SlopeFeetPerMeter,
    double TargetSpeedKnots, double TouchdownAlongMeters);

/// <summary>Lineare Sinksegmente und tangential anschließende, monoton abflachende Aufsetzkurve.</summary>
public static class AutoLandDescentCurve
{
    /// <summary>Wertet PA–Aufsetzen aus; nach dem Aufsetzpunkt bleiben Höhe und Steigung null.</summary>
    public static AutoLandCurveOutput Evaluate(AutoLandCurveInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.Plan);
        IReadOnlyList<AutoLandPoint> points = input.Plan.Points;
        if (points.Count != 6 || points[0].Label != "PA" || points[1].Label != "P1" ||
            points[3].Label != "P3" || points[4].Label != "P4" || points[4].DistanceBeforeThresholdMeters != 0 ||
            !double.IsFinite(input.AlongMeters) || !double.IsFinite(input.PaEntrySpeedKnots) ||
            !double.IsFinite(input.ClimbSpeedKnots) || input.ClimbSpeedKnots <= 0 ||
            input.PaEntrySpeedKnots <= 0 ||
            !double.IsFinite(input.Plan.Runway.LengthMeters) || input.Plan.Runway.LengthMeters <= 0)
            throw new ArgumentException(null, nameof(input));

        double pa = -points[0].DistanceBeforeThresholdMeters;
        double p1 = -points[1].DistanceBeforeThresholdMeters;
        double p3 = -points[3].DistanceBeforeThresholdMeters;
        double touchdown = input.Plan.Runway.LengthMeters * 0.2;
        if (!(pa < p1 && p1 < p3 && p3 < 0))
            throw new ArgumentException(null, nameof(input));

        double x = input.AlongMeters;
        double height, slope;
        if (x <= p1)
        {
            height = 1500;
            slope = 0;
        }
        else if (x <= p3)
        {
            slope = -1000 / (p3 - p1);
            height = 1500 + slope * (x - p1);
        }
        else if (x <= 0)
        {
            slope = 440 / p3;
            height = 60 + slope * x;
        }
        else if (x < touchdown)
        {
            // n >= 2 sichert abnehmenden Sinkgradienten und Steigung null am Boden.
            double exponent = -440 / p3 * touchdown / 60;
            if (exponent < 2 - 1e-12)
                throw new ArgumentException(null, nameof(input));
            double remaining = 1 - x / touchdown;
            height = 60 * Math.Pow(remaining, exponent);
            slope = -60 * exponent / touchdown * Math.Pow(remaining, exponent - 1);
        }
        else
        {
            height = slope = 0;
        }
        double speed = x >= p3 ? input.ClimbSpeedKnots :
            x >= p1 ? input.ClimbSpeedKnots + 20 : input.PaEntrySpeedKnots;

        return new(height, slope, speed, touchdown);
    }
}

/// <summary>Zustand der direkten Längsführung; Bodenkontakt und Schubabschaltung werden verriegelt.</summary>
public sealed record AutoLandLandingState(DateTime? SampleUtc = null, double? PreviousVerticalSpeedFeetPerSecond = null,
    double? PreviousSpeedKnots = null, bool AirborneObserved = false, int GroundContactSamples = 0,
    bool TouchdownConfirmed = false, bool ThrottleCut = false, bool FlapsStageOne = false, int StandstillSamples = 0,
    double? SimulationSeconds = null, double? PreviousPitchNoseUpDegrees = null,
    double PitchRateDegreesPerSecond = 0,
    double? PitchPathOffsetDegrees = null, double ElevatorBias = 0, double? ElevatorCommand = null,
    IReadOnlyList<double>? ThrottleCommandsPercent = null,
    int RunwayContactSamples = 0, bool RunwayTouchdownConfirmed = false,
    double? PreviousCurvePathDegrees = null, double CurvePathRateDegreesPerSecond = 0,
    double? RawPitchRateDegreesPerSecond = null, IReadOnlyList<double>? PitchDynamics = null,
    IReadOnlyList<double>? PitchDynamicsCovariance = null, double? PreviousMeasuredOffsetDegrees = null,
    double OffsetRateDegreesPerSecond = 0, double? PreviousDesiredPitchRate = null,
    double DesiredPitchAcceleration = 0, double? PreviousFlightPathDegrees = null,
    IReadOnlyList<double>? FlightPathDynamics = null, IReadOnlyList<double>? FlightPathCovariance = null,
    double? PreviousFeedforwardPitch = null, double? PreviousTargetPathDegrees = null,
    double TargetPathRateDegreesPerSecond = 0, bool GearDownRequested = false,
    double? PaEntrySpeedKnots = null, bool PassedP1 = false, bool PassedP3 = false,
    double ThrottleElapsedSeconds = 0, int RequestedFlapsStage = 0,
    double? FlapsStageOneReleaseAlongMeters = null);

/// <summary>Frischer vollständiger Flugframe und Modellgeschwindigkeiten, ohne Simulatorabhängigkeit.</summary>
public sealed record AutoLandLandingInput(AutoLandPlan Plan, AutoLandPosition Position,
    double AltitudeMslFeet, double AltitudeAboveGroundFeet, double VerticalSpeedFeetPerSecond,
    double GroundSpeedMetersPerSecond, double IndicatedAirspeedKnots, double ElevatorPosition,
    IReadOnlyList<double> ThrottlePositionsPercent, double ApproachSpeedKnots, double ClimbSpeedKnots,
    bool OnGround, DateTime SampleUtc, DateTime NowUtc,
    double PitchDegrees = double.NaN, double SimulationSeconds = double.NaN,
    double TrueAirspeedKnots = double.NaN, double WeightPounds = double.NaN);

/// <summary>Absolute Aktuatorausgabe; FlapsIndex fordert einmalig 0=Einfahren, 1=Stufe 1, 2=Stufe 2, 3=voll an,
/// nicht den tatsächlichen Flugzeug-Hebelindex. Klappen und Fahrwerk sind keine Positionsbestätigung.</summary>
public sealed record AutoLandLandingOutput(AutoLandLandingState State, bool HasCommand, bool Completed = false,
    double ElevatorPosition = 0, IReadOnlyList<double>? ThrottlePositionsPercent = null,
    bool FullBrakes = false, int? FlapsIndex = null, AutoLandCurveOutput? Curve = null,
    double TargetVerticalSpeedFeetPerSecond = 0, double TargetPitchNoseUpDegrees = 0,
    double PitchRateDegreesPerSecond = 0, bool InsideRunway = false, bool StoppedOutsideRunway = false,
    bool RequestGearDown = false);

/// <summary>Direkte Sinkkurvenführung und bestätigtes Aufsetzen ohne Positionsabbruch oder AP-Regler.</summary>
public static class AutoLandLandingController
{
    // Vorläufige lokale Auslegung, keine Flugzeugparameter oder zusätzlichen Flug-/Abbruchlimits.
    // Räumliche Höhenrückführung mit messwertbasierter Vorsteuerung der Pitchdynamik.
    private const double PitchGain = 0.04;
    private const double PitchIntegralGain = 0.004;
    private const double PitchRateGain = 0.01;
    private const double PitchPathOffsetTimeConstantSeconds = 1;
    // Eigene AutoLand-Schrittperiode, unabhängig von der SIM_FRAME-Rate.
    private const double ThrottlePeriodSeconds = 0.5;

    /// <summary>Verarbeitet nur neue gültige Frames; positive Elevatorstellung zieht wie im Autostart hoch.</summary>
    public static AutoLandLandingOutput Update(AutoLandLandingState state, AutoLandLandingInput input)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.Plan);
        ArgumentNullException.ThrowIfNull(input.Position);
        ArgumentNullException.ThrowIfNull(input.ThrottlePositionsPercent);
        if (input.Plan.Points.Count != 6)
            throw new ArgumentException(null, nameof(input));
        if (input.SampleUtc == default || input.SampleUtc.Kind != DateTimeKind.Utc ||
            input.NowUtc.Kind != DateTimeKind.Utc || input.SampleUtc > input.NowUtc ||
            (input.NowUtc - input.SampleUtc).TotalSeconds > 2 ||
            !double.IsFinite(input.Position.LatitudeDegrees) || Math.Abs(input.Position.LatitudeDegrees) > 90 ||
            !double.IsFinite(input.Position.LongitudeDegrees) || Math.Abs(input.Position.LongitudeDegrees) > 180 ||
            !double.IsFinite(input.AltitudeMslFeet) || !double.IsFinite(input.AltitudeAboveGroundFeet) ||
            !double.IsFinite(input.VerticalSpeedFeetPerSecond) ||
            !double.IsFinite(input.PitchDegrees) || Math.Abs(input.PitchDegrees) > 180 ||
            !double.IsFinite(input.SimulationSeconds) || input.SimulationSeconds < 0 ||
            !double.IsFinite(input.TrueAirspeedKnots) || input.TrueAirspeedKnots < 0 ||
            !double.IsFinite(input.GroundSpeedMetersPerSecond) || input.GroundSpeedMetersPerSecond < 0 ||
            !double.IsFinite(input.IndicatedAirspeedKnots) || input.IndicatedAirspeedKnots < 0 ||
            (state.PaEntrySpeedKnots is null && input.IndicatedAirspeedKnots <= 0) ||
            !double.IsFinite(input.ApproachSpeedKnots) || input.ApproachSpeedKnots <= 0 ||
            !double.IsFinite(input.ClimbSpeedKnots) || input.ClimbSpeedKnots <= 0 ||
            !double.IsFinite(input.ElevatorPosition) || Math.Abs(input.ElevatorPosition) > 1 ||
            input.ThrottlePositionsPercent.Count is < 1 or > 4 ||
            input.ThrottlePositionsPercent.Any(value => !double.IsFinite(value) || value < 0 || value > 100))
            return new(state with { PreviousVerticalSpeedFeetPerSecond = null,
                PreviousSpeedKnots = null, PreviousPitchNoseUpDegrees = null,
                ThrottleElapsedSeconds = 0,
                GroundContactSamples = 0, RunwayContactSamples = 0, StandstillSamples = 0 }, false);
        if (state.SampleUtc is { } last && input.SampleUtc <= last)
            return new(state, false);
        if (state.SimulationSeconds is { } lastSimulation && input.SimulationSeconds == lastSimulation)
            return new(state, false);
        if (state.SimulationSeconds is { } futureSimulation && input.SimulationSeconds < futureSimulation)
            return new(state with { SimulationSeconds = null, PreviousPitchNoseUpDegrees = null,
                PreviousVerticalSpeedFeetPerSecond = null, PreviousSpeedKnots = null,
                ThrottleElapsedSeconds = 0,
                GroundContactSamples = 0, RunwayContactSamples = 0, StandstillSamples = 0 }, false);
        double dt = state.SimulationSeconds is { } time ? input.SimulationSeconds - time : 0;
        bool continuous = dt > 0 && dt <= 2 && state.PreviousVerticalSpeedFeetPerSecond.HasValue &&
            state.PreviousSpeedKnots.HasValue && state.PreviousPitchNoseUpDegrees.HasValue;
        (double along, double cross) = AutoLandDirectController.GetOffsetsMeters(input.Plan.Points[4].Position,
            input.Plan.CourseTrueDegrees, input.Position);
        bool insideRunway = along >= 0 && along <= input.Plan.Runway.LengthMeters &&
            Math.Abs(cross) <= input.Plan.Runway.WidthMeters / 2;
        double paEntrySpeed = state.PaEntrySpeedKnots ?? input.IndicatedAirspeedKnots;
        bool passedP3 = state.PassedP3 || along >= -input.Plan.Points[3].DistanceBeforeThresholdMeters;
        bool passedP1 = state.PassedP1 || passedP3 || along >= -input.Plan.Points[1].DistanceBeforeThresholdMeters;
        double targetSpeed = passedP3 ? input.ClimbSpeedKnots :
            passedP1 ? input.ClimbSpeedKnots + 20 : paEntrySpeed;
        AutoLandCurveOutput curve = AutoLandDescentCurve.Evaluate(new(input.Plan, along,
            paEntrySpeed, input.ClimbSpeedKnots)) with { TargetSpeedKnots = targetSpeed };
        bool airborne = state.AirborneObserved || !input.OnGround;
        int contacts = input.OnGround && airborne ? (continuous ? state.GroundContactSamples : 0) + 1 : 0;
        bool touchdown = state.TouchdownConfirmed || contacts >= 2;
        int runwayContacts = input.OnGround && airborne && insideRunway
            ? (continuous ? state.RunwayContactSamples : 0) + 1 : 0;
        bool runwayTouchdown = state.RunwayTouchdownConfirmed || runwayContacts >= 2;
        bool cut = state.ThrottleCut || input.AltitudeAboveGroundFeet <= 5 || touchdown;
        if (!cut && (!double.IsFinite(input.WeightPounds) || input.WeightPounds <= 0))
            return new(state with { PreviousVerticalSpeedFeetPerSecond = null,
                PreviousSpeedKnots = null, PreviousPitchNoseUpDegrees = null,
                ThrottleElapsedSeconds = 0,
                GroundContactSamples = 0, RunwayContactSamples = 0, StandstillSamples = 0 }, false);
        int stopped = touchdown && input.OnGround && input.GroundSpeedMetersPerSecond <= 0.1
            ? (continuous ? state.StandstillSamples : 0) + 1 : 0;
        bool completed = stopped >= 2 && runwayTouchdown && insideRunway;
        bool stoppedOutside = stopped >= 2 && !insideRunway;
        double p2 = -input.Plan.Points[2].DistanceBeforeThresholdMeters;
        double p3 = -input.Plan.Points[3].DistanceBeforeThresholdMeters;
        // Das Freigabeband bleibt auch nach P3 unabhängig vom dort niedrigeren Phasen-Sollwert.
        double flapsGateSpeed = input.ClimbSpeedKnots + 20;
        int requestedFlapsStage = state.RequestedFlapsStage;
        double? releaseAlong = state.FlapsStageOneReleaseAlongMeters;
        if (!completed)
        {
            if (requestedFlapsStage == 0 && along >= p2 &&
                Math.Abs(input.IndicatedAirspeedKnots - flapsGateSpeed) <= 2)
            {
                requestedFlapsStage = 1;
                releaseAlong ??= along;
            }
            else if (requestedFlapsStage == 1 && releaseAlong is { } released &&
                along >= (released + p3) / 2)
                requestedFlapsStage = 2;
            else if (requestedFlapsStage == 2 && passedP3)
                requestedFlapsStage = 3;
        }
        bool flaps = state.FlapsStageOne || requestedFlapsStage >= 1;
        int? flapsIndex = completed ? 0 : requestedFlapsStage > state.RequestedFlapsStage ? requestedFlapsStage : null;
        bool requestGearDown = passedP3 && !state.GearDownRequested;
        double targetAltitude = input.Plan.Runway.AltitudeMeters / 0.3048 + curve.HeightAboveRunwayFeet;
        double heightFeedbackRate = 0.2;
        if (along >= 0)
        {
            // Relative statt absolute Resthöhenrückführung: h' = -n*h/(D-x).
            // Der Abtasthorizont vermeidet die Singularität und den Rückführungssprung am Zielpunkt.
            double exponent = 440 * curve.TouchdownAlongMeters /
                (60 * input.Plan.Points[3].DistanceBeforeThresholdMeters);
            double remainingDistance = Math.Max(curve.TouchdownAlongMeters - along,
                input.GroundSpeedMetersPerSecond * dt);
            if (remainingDistance > 0)
                heightFeedbackRate += exponent * input.GroundSpeedMetersPerSecond / remainingDistance;
        }
        double targetVs = curve.SlopeFeetPerMeter * input.GroundSpeedMetersPerSecond +
            (targetAltitude - input.AltitudeMslFeet) * heightFeedbackRate;
        double pitch = -input.PitchDegrees;
        // Nur Istwerte schätzen den aerodynamischen Versatz; Sollabweichungen dürfen nicht darin verschwinden.
        // Der Tiefpass trennt die langsame Anpassung von der unmittelbaren Pitch-/Ratendämpfung.
        double measuredOffset = pitch - PathAngle(input.VerticalSpeedFeetPerSecond, input.TrueAirspeedKnots);
        double pathOffset = state.PitchPathOffsetDegrees ?? measuredOffset;
        if (continuous && !input.OnGround)
            pathOffset += (measuredOffset - pathOffset) * (1 - Math.Exp(-dt / PitchPathOffsetTimeConstantSeconds));
        double rateWeight = continuous ? -Math.Exp(-dt / 0.25) + 1 : 0;
        double measuredRateWeight = continuous ? -Math.Exp(-dt / 0.10) + 1 : 0;
        double rawPitchRate = continuous ? (pitch - state.PreviousPitchNoseUpDegrees.GetValueOrDefault()) / dt : 0;
        double pitchRate = continuous ? state.PitchRateDegreesPerSecond + measuredRateWeight *
            (rawPitchRate - state.PitchRateDegreesPerSecond) : 0;
        // Die Kurvenrotation vorsteuern, damit die Dämpfung nicht das verlangte Abfangen bremst.
        // Nur die geometrische Steigung differenzieren, nicht Höhensprünge oder die Versatzschätzung.
        double curvePath = Math.Atan(curve.SlopeFeetPerMeter * 0.3048) * 180 / Math.PI;
        double curvePathRate = continuous && state.PreviousCurvePathDegrees is { } previousCurvePath
            ? state.CurvePathRateDegreesPerSecond + rateWeight *
                ((curvePath - previousCurvePath) / dt - state.CurvePathRateDegreesPerSecond) : 0;
        double offsetRate = continuous && state.PreviousMeasuredOffsetDegrees is { } previousOffset
            ? state.OffsetRateDegreesPerSecond + rateWeight *
                ((measuredOffset - previousOffset) / dt - state.OffsetRateDegreesPerSecond) : 0;
        double flightPath = PathAngle(input.VerticalSpeedFeetPerSecond, input.TrueAirspeedKnots);
        double flightPathRate = continuous && state.PreviousFlightPathDegrees is { } previousFlightPath
            ? (flightPath - previousFlightPath) / dt : 0;
        double[] pathDynamics = state.FlightPathDynamics?.ToArray() ?? [measuredOffset, 0, 0, 0];
        double[] pathCovariance = state.FlightPathCovariance?.ToArray() ??
            [1e6, 0, 0, 0, 0, 1e6, 0, 0, 0, 0, 1e6, 0, 0, 0, 0, 1e6];
        if (continuous && !input.OnGround)
            Identify(pathDynamics, pathCovariance,
                [1, input.IndicatedAirspeedKnots - input.ApproachSpeedKnots,
                    state.FlapsStageOne ? 1 : 0, flightPathRate], measuredOffset, dt);
        // Lokal linearisierter Anstellwinkel über IAS/Klappen und identifizierter Flugbahnverzug.
        // Die Vorsteuerung verwendet ausschließlich Istwerte, nicht die Sollhöhenabweichung.
        bool pathIdentified = pathDynamics[3] > 0 && pathCovariance[15] < 0.01 * pathDynamics[3] * pathDynamics[3];
        double targetPath = PathAngle(targetVs, input.TrueAirspeedKnots);
        double targetPathRate = continuous && state.PreviousTargetPathDegrees is { } previousTargetPath
            ? state.TargetPathRateDegreesPerSecond + rateWeight *
                ((targetPath - previousTargetPath) / dt - state.TargetPathRateDegreesPerSecond) : 0;
        double predictedOffset = pathIdentified
            ? pathDynamics[0] + pathDynamics[1] * (input.IndicatedAirspeedKnots - input.ApproachSpeedKnots) +
                pathDynamics[2] * (state.FlapsStageOne ? 1 : 0) + pathDynamics[3] * targetPathRate : pathOffset;
        double targetPitch = predictedOffset + targetPath;
        double pitchError = targetPitch - pitch;
        double feedforwardPitch = targetPitch;
        double desiredPitchRate = continuous && state.PreviousFeedforwardPitch is { } previousFeedforwardPitch
            ? state.PreviousDesiredPitchRate.GetValueOrDefault() + rateWeight *
                ((feedforwardPitch - previousFeedforwardPitch) / dt - state.PreviousDesiredPitchRate.GetValueOrDefault()) : 0;
        double desiredPitchAcceleration = continuous && state.PreviousDesiredPitchRate is { } previousDesiredRate
            ? state.DesiredPitchAcceleration + rateWeight *
                ((desiredPitchRate - previousDesiredRate) / dt - state.DesiredPitchAcceleration) : 0;
        double[] dynamics = state.PitchDynamics?.ToArray() ?? new double[3];
        double[] covariance = state.PitchDynamicsCovariance?.ToArray() ?? [1e6, 0, 0, 0, 1e6, 0, 0, 0, 1e6];
        if (continuous && !input.OnGround && state.RawPitchRateDegreesPerSecond is { } previousRawRate &&
            state.ElevatorCommand is { } appliedElevator)
            Identify(dynamics, covariance, [appliedElevator, -previousRawRate, 1],
                (rawPitchRate - previousRawRate) / dt, dt);
        // q' = A*u - B*q + C wird nur aus gemessenen Bewegungen und eigenen Befehlen gelernt.
        // Keine Flugzeugautorität oder Dämpfung aus den synthetischen Tests übernehmen.
        bool pitchIdentified = dynamics[0] > 0 && dynamics[1] >= 0 && covariance[0] < 0.01 * dynamics[0] * dynamics[0];
        double feedforward = pitchIdentified
            ? (dynamics[1] * desiredPitchRate + desiredPitchAcceleration) / dynamics[0] : 0;
        // Gemessene Dämpfung/Autorität ergeben die gleiche Fehlerabklingzeit statt modellabhängiger Verzögerung.
        double pitchGain = 0.5 * (pitchIdentified ? (PitchGain + dynamics[1] / dynamics[0]) /
            PitchPathOffsetTimeConstantSeconds : PitchGain);
        double pitchFeedback = pitchGain * pitchError - PitchRateGain * (pitchRate - desiredPitchRate) + feedforward;
        double elevatorBias = state.PitchPathOffsetDegrees.HasValue ? state.ElevatorBias :
            input.ElevatorPosition - pitchFeedback;
        if (continuous && !touchdown)
            elevatorBias = Integrate(elevatorBias, pitchFeedback, PitchIntegralGain * pitchError * dt, -1, 1);
        double elevator = touchdown ? 0 : continuous ? Math.Clamp(elevatorBias + pitchFeedback, -1, 1) :
            state.ElevatorCommand ?? input.ElevatorPosition;
        bool reduceForFlaps = passedP1 && requestedFlapsStage == 0 &&
            input.IndicatedAirspeedKnots > flapsGateSpeed + 2;
        // Die absolute Reduktion darf weder auf die Schrittperiode warten noch Schritte ansammeln.
        double throttleElapsed = continuous && !cut && !reduceForFlaps ? state.ThrottleElapsedSeconds + dt : 0;
        int throttleSteps = (int)Math.Floor((throttleElapsed + 1e-9) / ThrottlePeriodSeconds);
        double[] changes = throttleSteps > 0 ? AutoPilot.CalculateThrottleChanges(
            input.IndicatedAirspeedKnots,
            (input.IndicatedAirspeedKnots - state.PreviousSpeedKnots.GetValueOrDefault()) / dt,
            targetSpeed, input.WeightPounds, input.AltitudeMslFeet) : [];
        throttleElapsed = Math.Max(0, throttleElapsed - throttleSteps * ThrottlePeriodSeconds);
        double[] throttles = state.ThrottleCommandsPercent is { } held &&
            held.Count == input.ThrottlePositionsPercent.Count ? [.. held] : [.. input.ThrottlePositionsPercent];
        for (int engine = 0; engine < throttles.Length; engine++)
        {
            if (cut)
            {
                throttles[engine] = 0;
                continue;
            }
            if (reduceForFlaps)
            {
                throttles[engine] = 25;
                continue;
            }
            foreach (double change in changes)
                if ((change < 0 && throttles[engine] > 25) || (change > 0 && throttles[engine] < 100))
                    throttles[engine] = Math.Clamp(throttles[engine] + change * throttleSteps, 25, 100);
        }
        AutoLandLandingState updated = state with
        {
            SampleUtc = input.SampleUtc, SimulationSeconds = input.SimulationSeconds,
            PreviousVerticalSpeedFeetPerSecond = input.VerticalSpeedFeetPerSecond,
            PreviousSpeedKnots = input.IndicatedAirspeedKnots, PreviousPitchNoseUpDegrees = pitch,
            PitchRateDegreesPerSecond = pitchRate,
            PreviousCurvePathDegrees = curvePath, CurvePathRateDegreesPerSecond = curvePathRate,
            RawPitchRateDegreesPerSecond = continuous ? rawPitchRate : null,
            PitchDynamics = Array.AsReadOnly(dynamics), PitchDynamicsCovariance = Array.AsReadOnly(covariance),
            PreviousMeasuredOffsetDegrees = measuredOffset, OffsetRateDegreesPerSecond = offsetRate,
            PreviousDesiredPitchRate = desiredPitchRate, DesiredPitchAcceleration = desiredPitchAcceleration,
            PreviousFlightPathDegrees = flightPath, FlightPathDynamics = Array.AsReadOnly(pathDynamics),
            FlightPathCovariance = Array.AsReadOnly(pathCovariance), PreviousFeedforwardPitch = feedforwardPitch,
            PreviousTargetPathDegrees = targetPath, TargetPathRateDegreesPerSecond = targetPathRate,
            PitchPathOffsetDegrees = pathOffset, ElevatorBias = elevatorBias, ElevatorCommand = elevator,
            ThrottleCommandsPercent = Array.AsReadOnly(throttles), ThrottleElapsedSeconds = throttleElapsed,
            AirborneObserved = airborne, GroundContactSamples = contacts, TouchdownConfirmed = touchdown,
            ThrottleCut = cut, FlapsStageOne = flaps, StandstillSamples = stopped,
            RunwayContactSamples = runwayContacts, RunwayTouchdownConfirmed = runwayTouchdown,
            GearDownRequested = state.GearDownRequested || requestGearDown,
            PaEntrySpeedKnots = paEntrySpeed, PassedP1 = passedP1, PassedP3 = passedP3,
            RequestedFlapsStage = requestedFlapsStage, FlapsStageOneReleaseAlongMeters = releaseAlong
        };

        return new(updated, true, completed, elevator, Array.AsReadOnly(throttles), touchdown,
            flapsIndex, curve, targetVs, targetPitch, pitchRate, insideRunway, stoppedOutside, requestGearDown);
    }

    private static double PathAngle(double verticalSpeedFeetPerSecond, double trueAirspeedKnots)
    {
        // GS bestimmt die räumliche Sinkrate; der aerodynamische Winkel bezieht sich auf TAS.
        double speed = trueAirspeedKnots * 1852 / 3600;
        return speed > 0 ? Math.Asin(Math.Clamp(verticalSpeedFeetPerSecond * 0.3048 / speed, -1, 1)) *
            180 / Math.PI : 0;
    }

    private static void Identify(double[] parameters, double[] covariance, double[] features, double measured, double dt)
    {
        int count = parameters.Length;
        double[] projected = new double[count];
        double forgetting = Math.Exp(-dt / 300);
        double denominator = forgetting;
        for (int row = 0; row < count; row++)
        {
            for (int column = 0; column < count; column++)
                projected[row] += covariance[row * count + column] * features[column];
            denominator += features[row] * projected[row];
        }
        double residual = measured;
        for (int row = 0; row < count; row++)
            residual -= parameters[row] * features[row];
        for (int row = 0; row < count; row++)
        {
            parameters[row] += projected[row] / denominator * residual;
            for (int column = 0; column < count; column++)
                covariance[row * count + column] =
                    (covariance[row * count + column] - projected[row] * projected[column] / denominator) / forgetting;
        }
    }

    private static double Integrate(double bias, double feedback, double increment, double minimum, double maximum)
    {
        // Nur den Integrationsschritt begrenzen. Eine kurzzeitige P-/D-Sättigung darf
        // den übernommenen Gleichgewichtsbias nicht durch Rückrechnung verschieben.
        double command = bias + feedback;
        return bias + (increment >= 0 ? Math.Min(increment, Math.Max(0, maximum - command)) :
            Math.Max(increment, Math.Min(0, minimum - command)));
    }
}

/// <summary>Umgebungssuche mit höchstens drei Abrufen; nur ein leeres Ergebnis vergrößert den Radius.</summary>
internal static class AutoLandAirportSearch
{
    internal const double InitialRadiusNauticalMiles = 50.0;
    internal const int MaximumAttempts = 3;
    internal const double MetersPerNauticalMile = 1852.0;

    internal static async Task<IReadOnlyList<T>> FindAsync<T>(
        Func<double, CancellationToken, Task<IReadOnlyList<T>>> request,
        CancellationToken cancellationToken,
        double initialRadiusNauticalMiles = InitialRadiusNauticalMiles)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!double.IsFinite(initialRadiusNauticalMiles) || initialRadiusNauticalMiles <= 0.0 ||
            !double.IsFinite(initialRadiusNauticalMiles * 4.0))
            throw new ArgumentOutOfRangeException(nameof(initialRadiusNauticalMiles));
        double radius = initialRadiusNauticalMiles;
        for (int attempt = 0; attempt < MaximumAttempts; attempt++, radius *= 2.0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<T> airports = await request(radius, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (airports.Count != 0)
                return airports;
        }
        return [];
    }

    internal static bool IsWithinRadius(AutoLandPosition origin, AutoLandPosition airport,
        double radiusNauticalMiles)
    {
        if (!double.IsFinite(radiusNauticalMiles) || radiusNauticalMiles <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(radiusNauticalMiles));
        // Kleine Toleranz für Rundungsfehler bei Punkten exakt auf dem Rand.
        return AutoLand.DistanceMeters(origin, airport) <= radiusNauticalMiles * MetersPerNauticalMile + 0.001;
    }
}

/// <summary>Begrenzter Detailabruf ohne UI- oder SimConnect-Abhängigkeit. Nutzung auf dem UI-Thread.</summary>
internal sealed class AutoLandFacilityLoadQueue<T> where T : notnull
{
    private readonly LinkedList<T> waiting = new();
    private readonly Dictionary<T, LinkedListNode<T>> queued = [];
    private readonly Dictionary<T, DateTimeOffset> pending = [];
    private readonly int maximumPending;
    private readonly TimeSpan timeout;

    internal AutoLandFacilityLoadQueue(IEnumerable<T> items, int maximumPending, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumPending, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        this.maximumPending = maximumPending;
        this.timeout = timeout;
        foreach (T item in items)
        {
            if (!queued.ContainsKey(item))
                queued.Add(item, waiting.AddLast(item));
        }
    }

    internal int PendingCount => pending.Count;
    internal int WaitingCount => queued.Count;

    internal void Prioritize(T item)
    {
        if (!queued.TryGetValue(item, out LinkedListNode<T>? node))
            return;
        waiting.Remove(node);
        waiting.AddFirst(node);
    }

    internal IReadOnlyList<T> TakeReady(DateTimeOffset now)
    {
        List<T> result = [];
        while (pending.Count < maximumPending && waiting.First is { } node)
        {
            waiting.RemoveFirst();
            queued.Remove(node.Value);
            pending.Add(node.Value, now + timeout);
            result.Add(node.Value);
        }
        return result;
    }

    internal bool Complete(T item) => pending.Remove(item);

    internal IReadOnlyList<T> Expire(DateTimeOffset now)
    {
        T[] expired = [.. pending.Where(pair => pair.Value <= now).Select(pair => pair.Key)];
        foreach (T item in expired)
            pending.Remove(item);
        return expired;
    }
}

/// <summary>Nur Kartenplanung; Radius ist eine explizite Darstellungsgröße, kein Reglerlimit.</summary>
public sealed record AutoLandEntryInput(AutoLandPosition AircraftPosition, double HeadingTrueDegrees,
    AutoLandPosition Pa, double ApproachTrueDegrees, double RadiusMeters);

public enum AutoLandEntryKind { Direct, TurnBack, PassPaThenTurnBack }

/// <summary>Geordnete Pfeilposition mit wahrer Flugrichtung.</summary>
public sealed record AutoLandEntryArrow(AutoLandPosition Position, double HeadingTrueDegrees);

public sealed record AutoLandEntryPlan(AutoLandEntryKind Kind, double StraightBeforeTurnMeters,
    double LengthMeters, IReadOnlyList<AutoLandEntryArrow> Arrows);

/// <summary>Reine geometrische Anflugmarkierung ohne Simulatorzugriff oder Flugsteuerung.</summary>
public static class AutoLandEntryPath
{
    private const double Tau = 2 * Math.PI;
    private const double Degrees = 180 / Math.PI;

    private readonly record struct Point(double X, double Y)
    {
        public static Point operator +(Point a, Point b) => new(a.X + b.X, a.Y + b.Y);
        public static Point operator -(Point a, Point b) => new(a.X - b.X, a.Y - b.Y);
        public static Point operator *(double scale, Point p) => new(scale * p.X, scale * p.Y);
        public double Length => Math.Sqrt(X * X + Y * Y);
    }

    private sealed record Curve(Point StartCenter, Point EndCenter, int StartSide, int EndSide,
        double Tangent, double FirstAngle, double LastAngle, double Straight, double Length);

    public static AutoLandEntryPlan Create(AutoLandEntryInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        // Die vorhandenen öffentlichen Geometrie-APIs validieren beide Positionen.
        double distance = AutoLand.DistanceMeters(input.Pa, input.AircraftPosition);
        if (!double.IsFinite(input.HeadingTrueDegrees) || !double.IsFinite(input.ApproachTrueDegrees))
            throw new ArgumentException("Wahre Kurse müssen endlich sein.", nameof(input));
        if (!double.IsFinite(input.RadiusMeters) || input.RadiusMeters <= 0)
            throw new ArgumentOutOfRangeException(nameof(input), input.RadiusMeters, "RadiusMeters muss endlich und größer als 0 sein.");

        double course = Normalize(input.ApproachTrueDegrees / Degrees);
        double theta = Normalize(course - input.HeadingTrueDegrees / Degrees);
        double bearing = Bearing(input.Pa, input.AircraftPosition);
        double relative = course - bearing;
        Point original = new(distance * Math.Cos(relative), distance * Math.Sin(relative));
        Point start = original;
        double radius = input.RadiusMeters;
        bool opposite = Math.Cos(theta) < -1e-12;
        double forward = 0;
        AutoLandEntryKind kind = opposite ? AutoLandEntryKind.TurnBack : AutoLandEntryKind.Direct;
        // Alle CSC-Kandidaten unmittelbar ab der Flugzeugposition vergleichen.
        // Kein erzwungenes Passieren der PA-Querlinie vor der ersten Kurve.

        Curve? best = null;
        foreach (int firstSide in new[] { -1, 1 })
        foreach (int lastSide in new[] { -1, 1 })
        {
            Point firstCenter = start + (firstSide * radius) * Left(theta);
            Point lastCenter = new(0, lastSide * radius);
            Point delta = lastCenter - firstCenter;
            double d = delta.Length;
            double offset = (lastSide - firstSide) * radius;
            double tangent;
            double straight;
            if (d < 1e-7)
            {
                if (firstSide != lastSide)
                    continue;
                tangent = 0;
                straight = 0;
            }
            else
            {
                if (Math.Abs(offset) > d + 1e-7)
                    continue;
                tangent = Math.Atan2(delta.Y, delta.X) - Math.Asin(Math.Clamp(offset / d, -1, 1));
                straight = Math.Sqrt(Math.Max(0, d * d - offset * offset));
            }
            double firstAngle = Normalize(firstSide * (tangent - theta));
            double lastAngle = Normalize(lastSide * -tangent);
            double length = radius * (firstAngle + lastAngle) + straight;
            if (best == null || length < best.Length)
                best = new(firstCenter, lastCenter, firstSide, lastSide,
                    tangent, firstAngle, lastAngle, straight, length);
        }
        Curve curve = best ?? throw new ArgumentException("Keine tangentiale Anflugkurve möglich.", nameof(input));
        List<AutoLandEntryArrow> arrows = [];
        // Gleichmäßige Pfeilabstände; Kreise mit mindestens 36 Positionen pro Vollkreis.
        double spacing = Math.Min(250, radius * Math.PI / 18);
        double total = forward + curve.Length;
        if (total < 1e-7)
            return new(kind, forward, 0, Array.AsReadOnly<AutoLandEntryArrow>(
                [new AutoLandEntryArrow(input.Pa, Normalize(course) * Degrees)]));
        int steps = Math.Max(1, (int)Math.Ceiling(total / spacing));
        for (int i = 0; i <= steps; i++)
        {
            double along = total * i / steps;
            Point point;
            double heading;
            if (along < forward)
            {
                point = original + along * Direction(theta);
                heading = theta;
            }
            else
            {
                double remaining = along - forward;
                double firstLength = radius * curve.FirstAngle;
                if (remaining < firstLength)
                {
                    heading = theta + curve.StartSide * remaining / radius;
                    point = curve.StartCenter - (curve.StartSide * radius) * Left(heading);
                }
                else if (remaining < firstLength + curve.Straight)
                {
                    heading = curve.Tangent;
                    point = curve.StartCenter - (curve.StartSide * radius) * Left(heading)
                        + (remaining - firstLength) * Direction(heading);
                }
                else
                {
                    heading = curve.Tangent + curve.EndSide *
                        Math.Clamp((remaining - firstLength - curve.Straight) / radius, 0, curve.LastAngle);
                    point = curve.EndCenter - (curve.EndSide * radius) * Left(heading);
                }
            }
            AutoLandPosition position = i == steps ? input.Pa :
                AutoLand.Destination(input.Pa, (course - Math.Atan2(point.Y, point.X)) * Degrees, point.Length);
            arrows.Add(new(position, i == steps ? Normalize(course) * Degrees : Normalize(course - heading) * Degrees));
        }
        return new(kind, forward, total, arrows.AsReadOnly());
    }

    private static Point Direction(double angle) => new(Math.Cos(angle), Math.Sin(angle));
    private static Point Left(double angle) => new(-Math.Sin(angle), Math.Cos(angle));
    private static double Normalize(double angle)
    {
        double value = (angle % Tau + Tau) % Tau;
        return value < 1e-10 || Tau - value < 1e-10 ? 0 : value;
    }

    private static double Bearing(AutoLandPosition from, AutoLandPosition to)
    {
        double lat1 = from.LatitudeDegrees / Degrees;
        double lat2 = to.LatitudeDegrees / Degrees;
        double lon = (to.LongitudeDegrees - from.LongitudeDegrees) / Degrees;
        return Math.Atan2(Math.Sin(lon) * Math.Cos(lat2),
            Math.Cos(lat1) * Math.Sin(lat2) - Math.Sin(lat1) * Math.Cos(lat2) * Math.Cos(lon));
    }
}

/// <summary>Unveränderlicher Fortschritt auf genau der gezeichneten Kurve, keine P1-Navigation.</summary>
public sealed record AutoLandAlignmentState(int Segment = 0, AutoLandPosition? PreviousPosition = null,
    DateTime? SampleUtc = null, bool PaReached = false, double MaximumCrossTrackMeters = 0,
    double MaximumAltitudeErrorFeet = 0, int Samples = 0,
    double MinimumDistanceToPaMeters = double.PositiveInfinity, double MaximumRequiredBankDegrees = 0);

/// <summary>Telemetriesatz für die Ausrichtung entlang der Einflugkurve.</summary>
public sealed record AutoLandAlignmentInput(AutoLandEntryPlan Path, AutoLandPosition Position,
    double HeadingTrueDegrees, double GroundTrackTrueDegrees, double IndicatedAirspeedKnots,
    double StallSpeedKnots, double CruiseSpeedKnots, double AltitudeFeet,
    DateTime SampleUtc);

public sealed record AutoLandAlignmentOutput(AutoLandAlignmentState State, double HeadingTrueDegrees,
    double CrossTrackMeters, double DistanceToPaMeters, double RemainingMeters,
    double GroundSpeedMetersPerSecond, double EstimatedRequiredBankDegrees);

/// <summary>Reine Kurvenführung über HDG-Sollwerte; bestehende ALT-/HDG-Regler bleiben unverändert.</summary>
public static class AutoLandAlignment
{
    public const double TargetAltitudeFeet = 1500;
    // 10-s-HDG-Nachführung plus 0,5-Drehratenvorhalt des vorhandenen HeadingTurnController.
    // Dies ist eine Führungsannahme, kein zusätzliches Bank-, Geschwindigkeits- oder Abbruchlimit.
    private const double HeadingResponseSeconds = 15;

    public static AutoLandAlignmentOutput Update(AutoLandAlignmentState state, AutoLandAlignmentInput input)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.Path);
        IReadOnlyList<AutoLandEntryArrow> points = input.Path.Arrows;
        if (points.Count == 0 || !double.IsFinite(input.HeadingTrueDegrees) ||
            !double.IsFinite(input.GroundTrackTrueDegrees) ||
            !double.IsFinite(input.IndicatedAirspeedKnots) || input.IndicatedAirspeedKnots <= 0 ||
            !double.IsFinite(input.StallSpeedKnots) || input.StallSpeedKnots <= 0 ||
            !double.IsFinite(input.CruiseSpeedKnots) || input.CruiseSpeedKnots <= input.StallSpeedKnots ||
            !double.IsFinite(input.AltitudeFeet))
            throw new ArgumentException("Ungültige Ausrichtungstelemetrie oder Einflugkurve.", nameof(input));
        double paDistance = AutoLand.DistanceMeters(input.Position, points[^1].Position);
        if (state.SampleUtc is DateTime previousTime && input.SampleUtc <= previousTime)
            throw new ArgumentException("Die Ausrichtung benötigt einen neuen Positionsmesswert.", nameof(input));
        double dt = state.SampleUtc.HasValue ? (input.SampleUtc - state.SampleUtc.Value).TotalSeconds : 0;
        double travelled = state.PreviousPosition is { } previous ? AutoLand.DistanceMeters(previous, input.Position) : 0;
        // Dieselbe 2-s-Datenlückenerkennung wie im bestehenden HDG-Regler.
        bool continuous = dt > 0 && dt <= 2 && state.PreviousPosition != null;
        double groundSpeed = continuous ? travelled / dt : input.IndicatedAirspeedKnots * 0.5144444444444445;
        int segment = Math.Clamp(state.Segment, 0, Math.Max(0, points.Count - 2));
        // Nur ein Vorwärtsübertritt über die Querlinie zählt, nicht die Nähe zum Punkt.
        bool crossedPa = continuous && state.PreviousPosition is { } previousPosition &&
            CrossedPointLine(points[^1].Position, points[^1].HeadingTrueDegrees, previousPosition, input.Position);
        if (points.Count == 1)
            return new(state with { PaReached = state.PaReached || crossedPa, PreviousPosition = input.Position,
                SampleUtc = input.SampleUtc, Samples = state.Samples + 1 }, points[0].HeadingTrueDegrees,
                paDistance, paDistance, paDistance, groundSpeed, 0);

        var projection = Project(points[segment].Position, points[segment + 1].Position, input.Position);
        // Nur benachbarte Segmente vorwärts verfolgen: keine Sprünge auf eine spätere Schleife bei PA.
        while (segment + 2 < points.Count)
        {
            var next = Project(points[segment + 1].Position, points[segment + 2].Position, input.Position);
            if (next.Distance >= projection.Distance || projection.Fraction < 1)
                break;
            segment++;
            projection = next;
        }
        double fraction = Math.Clamp(projection.Fraction, 0, 1);
        double curvatureDegreesPerMeter = Difference(points[segment].HeadingTrueDegrees,
            points[segment + 1].HeadingTrueDegrees) / Math.Max(1e-6, projection.Length);
        double turnRate = curvatureDegreesPerMeter * groundSpeed;
        double speedFactor = 0.5 + 0.5 * Math.Clamp((input.IndicatedAirspeedKnots - input.StallSpeedKnots) /
            (input.CruiseSpeedKnots - input.StallSpeedKnots), 0, 1);
        double correction = -Math.Atan2(projection.Cross, Math.Max(1, groundSpeed * HeadingResponseSeconds)) * 180 / Math.PI;
        double windCorrection = Difference(input.GroundTrackTrueDegrees, input.HeadingTrueDegrees);
        // Den vorhandenen Vorhalthorizont entlang der geplanten Kurve auswerten,
        // statt die lokale Krümmung über Kurvenende/Krümmungswechsel hinaus fortzusetzen.
        // Bei konstanter Krümmung bleibt der bisherige Vorhalt unverändert.
        double previewHeading = PreviewHeading(points, segment, fraction,
            groundSpeed * HeadingResponseSeconds / speedFactor);
        double target = Normalize(previewHeading + correction + windCorrection);
        // Auch am letzten Segment tangential führen: direktes Zurückpeilen auf PA
        // würde bei seitlichem Vorbeiflug einen unnötigen Vollkreis verlangen.
        double remaining = (1 - fraction) * projection.Length;
        for (int i = segment + 1; i + 1 < points.Count; i++)
            remaining += AutoLand.DistanceMeters(points[i].Position, points[i + 1].Position);
        // Passieren bedeutet vorwärts über die Querlinie durch PA, senkrecht zum
        // Endkurs (PA -> P1). Kein seitliches Samplelängen-Limit: das verwechselt
        // das Passieren mit einem exakten Punkttreffer. Nur das letzte Segment
        // und aufeinanderfolgende Samples dürfen diesen Übergang auslösen.
        bool reached = state.PaReached || (segment == points.Count - 2 && crossedPa);
        double bank = Math.Atan(groundSpeed * (turnRate * Math.PI / 180) / 9.80665) * 180 / Math.PI;
        AutoLandAlignmentState updated = state with
        {
            Segment = segment, PreviousPosition = input.Position, SampleUtc = input.SampleUtc,
            PaReached = reached, Samples = state.Samples + 1,
            MaximumCrossTrackMeters = Math.Max(state.MaximumCrossTrackMeters, projection.Distance),
            MaximumAltitudeErrorFeet = Math.Max(state.MaximumAltitudeErrorFeet,
                Math.Abs(input.AltitudeFeet - TargetAltitudeFeet)),
            MinimumDistanceToPaMeters = Math.Min(state.MinimumDistanceToPaMeters, paDistance),
            MaximumRequiredBankDegrees = Math.Max(state.MaximumRequiredBankDegrees, Math.Abs(bank))
        };
        return new(updated, target, projection.Cross, paDistance, remaining, groundSpeed, bank);
    }

    private static double PreviewHeading(IReadOnlyList<AutoLandEntryArrow> points, int segment,
        double fraction, double distance)
    {
        for (int i = segment; i + 1 < points.Count; i++)
        {
            double length = AutoLand.DistanceMeters(points[i].Position, points[i + 1].Position);
            double available = (1 - fraction) * length;
            if (length > 1e-6 && distance <= available)
                return points[i].HeadingTrueDegrees +
                    Difference(points[i].HeadingTrueDegrees, points[i + 1].HeadingTrueDegrees) *
                    (fraction + distance / length);
            distance -= available;
            fraction = 0;
        }
        // Hinter PA tangential weiterführen, ohne eine neue Flugphase vorwegzunehmen.
        return points[^1].HeadingTrueDegrees;
    }

    private static bool CrossedPointLine(AutoLandPosition point, double courseTrueDegrees,
        AutoLandPosition previous, AutoLandPosition current) =>
        AlongTrackMeters(point, courseTrueDegrees, previous) < 0 &&
        AlongTrackMeters(point, courseTrueDegrees, current) >= 0;

    private static double AlongTrackMeters(AutoLandPosition origin, double heading, AutoLandPosition position) =>
        AutoLand.DistanceMeters(origin, position) *
        Math.Cos(Difference(heading, Bearing(origin, position)) * Math.PI / 180);

    private static (double Fraction, double Cross, double Length, double Distance) Project(
        AutoLandPosition start, AutoLandPosition end, AutoLandPosition position)
    {
        double length = AutoLand.DistanceMeters(start, end);
        double distance = AutoLand.DistanceMeters(start, position);
        double angle = Difference(Bearing(start, end), Bearing(start, position)) * Math.PI / 180;
        double along = distance * Math.Cos(angle);
        double cross = distance * Math.Sin(angle); // Rechts der Flugrichtung positiv.
        double fraction = length > 1e-6 ? along / length : 1;
        double outside = along - Math.Clamp(along, 0, length);
        return (fraction, cross, length, Math.Sqrt(cross * cross + outside * outside));
    }

    private static double Bearing(AutoLandPosition from, AutoLandPosition to)
    {
        double a = from.LatitudeDegrees * Math.PI / 180, b = to.LatitudeDegrees * Math.PI / 180;
        double lon = (to.LongitudeDegrees - from.LongitudeDegrees) * Math.PI / 180;
        return Normalize(Math.Atan2(Math.Sin(lon) * Math.Cos(b),
            Math.Cos(a) * Math.Sin(b) - Math.Sin(a) * Math.Cos(b) * Math.Cos(lon)) * 180 / Math.PI);
    }

    private static double Normalize(double value) => (value % 360 + 360) % 360;
    private static double Difference(double from, double to) => Normalize(to - from + 180) - 180;
}

/// <summary>Fortschritt ab PA; NextPointIndex bezeichnet das nächste Ziel im Plan (P1 = 1).</summary>
public sealed record AutoLandDirectState(int NextPointIndex = 1, AutoLandPosition? PreviousPosition = null,
    DateTime? SampleUtc = null, double? PreviousBankDegrees = null);

/// <summary>Reine SIM_FRAME-Eingabe ohne Höhen-, Schub- oder Bremsvorgaben.</summary>
public sealed record AutoLandDirectInput(AutoLandPlan Plan, AutoLandPosition Position,
    double HeadingTrueDegrees, double GroundTrackTrueDegrees, double BankDegrees,
    double IndicatedAirspeedKnots, DateTime SampleUtc, DateTime NowUtc, bool ContinueAfterP5 = false);

/// <summary>Normierte direkte Querruderstellung; bei fehlender Freigabe ist die Achse zu neutralisieren.</summary>
public sealed record AutoLandDirectOutput(AutoLandDirectState State, bool HasCommand, bool Completed,
    double AileronPosition = 0, double TargetHeadingTrueDegrees = 0, double TargetBankDegrees = 0,
    double RollRateDegreesPerSecond = 0);

/// <summary>Geordnete Querlinienpassagen PA–P5 und direkte Querruderführung wie im Autostart-Luftpfad.</summary>
public static class AutoLandDirectController
{
    /// <summary>Führt ausschließlich vorwärts; ungültige, alte oder nicht neue Telemetrie liefert keinen Achsenbefehl.</summary>
    public static AutoLandDirectOutput Update(AutoLandDirectState state, AutoLandDirectInput input)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.Plan);
        ArgumentNullException.ThrowIfNull(input.Position);
        IReadOnlyList<AutoLandPoint> points = input.Plan.Points;
        if (points.Count != 6 || points[0].Label != "PA" || points[1].Label != "P1" ||
            points[2].Label != "P2" || points[3].Label != "P3" || points[4].Label != "P4" || points[5].Label != "P5" ||
            !double.IsFinite(input.Plan.CourseTrueDegrees) || state.NextPointIndex is < 1 or > 6)
            throw new ArgumentException("Die Direktführung benötigt den geordneten Plan PA–P5.", nameof(input));
        if (state.NextPointIndex == points.Count && !input.ContinueAfterP5)
            return new(state, false, true);
        if (!ValidPosition(input.Position) || !double.IsFinite(input.HeadingTrueDegrees) ||
            !double.IsFinite(input.GroundTrackTrueDegrees) || !double.IsFinite(input.BankDegrees) ||
            !double.IsFinite(input.IndicatedAirspeedKnots) || input.IndicatedAirspeedKnots <= 0 ||
            input.SampleUtc.Kind != DateTimeKind.Utc || input.SampleUtc == default ||
            input.NowUtc.Kind != DateTimeKind.Utc || input.SampleUtc > input.NowUtc ||
            (input.NowUtc - input.SampleUtc).TotalSeconds > 2)
            return new(state with { PreviousPosition = null, PreviousBankDegrees = null }, false, false);
        if (state.SampleUtc is DateTime previousTime && input.SampleUtc <= previousTime)
            return new(state, false, false);

        double gap = state.SampleUtc is DateTime time ? (input.SampleUtc - time).TotalSeconds : 0;
        int next = state.NextPointIndex;
        if (gap > 0 && gap <= 2 && state.PreviousPosition is { } previous)
        {
            while (next < points.Count)
            {
                double before = GetOffsetsMeters(points[next].Position, input.Plan.CourseTrueDegrees, previous).Along;
                double after = GetOffsetsMeters(points[next].Position, input.Plan.CourseTrueDegrees, input.Position).Along;
                if (!(before < 0 && after >= 0 || before == 0 && after > 0))
                    break;
                next++;
            }
        }
        AutoLandDirectState updated = new(next, input.Position, input.SampleUtc, input.BankDegrees);
        if (next == points.Count && !input.ContinueAfterP5)
            return new(updated, false, true);

        double cross = GetOffsetsMeters(points[Math.Min(next, points.Count - 1)].Position, input.Plan.CourseTrueDegrees, input.Position).Cross;
        // Lokale Führungsannahme: 15 s Querabweichungsrückführung wie beim bisherigen Kurvenvorhalt,
        // inklusive gemessenem Windvorhalt; keine zusätzliche Bank- oder Abbruchgrenze.
        double correction = -Math.Atan2(cross, Math.Max(1, input.IndicatedAirspeedKnots * (1852.0 / 3600) * 15)) * 180 / Math.PI;
        double targetHeading = (input.Plan.CourseTrueDegrees + correction +
            Math.IEEERemainder(input.HeadingTrueDegrees - input.GroundTrackTrueDegrees, 360) + 360) % 360;
        double headingError = Math.IEEERemainder(targetHeading - input.HeadingTrueDegrees, 360);
        double rollRate = gap >= 0.05 && gap <= 0.25 && state.PreviousBankDegrees is double bank
            ? (input.BankDegrees - bank) / gap : 0;
        // Unverändert aus AutostartInitialClimbController: Rechtsbank negativ, Rechtsquerruder positiv.
        double targetBank = -Math.Clamp(headingError * 0.5, -5, 5);
        if (next >= 4)
        {
            // Ab P3 feste Bahnmittellinie statt wechselnder Zielpunkt-Querlinien.
            // Vorläufige Auslegung: omega=0,2/s, kritische Dämpfung im idealen Querbewegungsmodell.
            // Keine Garantie für +/-1 m im realen Flugzeugmodell.
            double fixedCross = GetOffsetsMeters(input.Plan.Runway.Center,
                input.Plan.CourseTrueDegrees, input.Position).Cross;
            if (gap <= 0 || gap > 2 || state.PreviousPosition is not { } lastPosition)
                return new(updated, false, false);
            double previousCross = GetOffsetsMeters(input.Plan.Runway.Center,
                input.Plan.CourseTrueDegrees, lastPosition).Cross;
            double crossSpeed = (fixedCross - previousCross) / gap;
            double acceleration = -0.04 * fixedCross - 0.4 * crossSpeed;
            // Rechtsbeschleunigung positiv, SimConnect-Rechtsbank negativ.
            targetBank = -Math.Clamp(Math.Atan(acceleration / 9.80665) * 180 / Math.PI, -5, 5);
            rollRate = state.PreviousBankDegrees is double previousBank
                ? (input.BankDegrees - previousBank) / gap : 0;
        }
        double aileron = Math.Clamp((input.BankDegrees - targetBank) * 0.02 + rollRate * 0.01, -0.15, 0.15);
        return new(updated, true, false, aileron, targetHeading, targetBank, rollRate);
    }

    /// <summary>Sphärische Querlinienkoordinaten: längs vorwärts positiv, quer rechts positiv.</summary>
    public static (double Along, double Cross) GetOffsetsMeters(AutoLandPosition point,
        double courseTrueDegrees, AutoLandPosition position)
    {
        ArgumentNullException.ThrowIfNull(point);
        ArgumentNullException.ThrowIfNull(position);
        if (!ValidPosition(point) || !ValidPosition(position) || !double.IsFinite(courseTrueDegrees))
            throw new ArgumentException("Ungültige Querliniengeometrie.", nameof(position));
        double latitude = position.LatitudeDegrees * Math.PI / 180;
        double pointLatitude = point.LatitudeDegrees * Math.PI / 180;
        double longitudeDifference = (position.LongitudeDegrees - point.LongitudeDegrees) * Math.PI / 180;
        double course = courseTrueDegrees * Math.PI / 180;
        double east = Math.Cos(latitude) * Math.Sin(longitudeDifference);
        double north = Math.Cos(pointLatitude) * Math.Sin(latitude) -
            Math.Sin(pointLatitude) * Math.Cos(latitude) * Math.Cos(longitudeDifference);
        double forward = east * Math.Sin(course) + north * Math.Cos(course);
        double right = east * Math.Cos(course) - north * Math.Sin(course);
        double dot = Math.Sin(pointLatitude) * Math.Sin(latitude) +
            Math.Cos(pointLatitude) * Math.Cos(latitude) * Math.Cos(longitudeDifference);
        return (6371000.0 * Math.Atan2(forward, dot),
            6371000.0 * Math.Asin(Math.Clamp(right, -1.0, 1.0)));
    }

    private static bool ValidPosition(AutoLandPosition position) =>
        double.IsFinite(position.LatitudeDegrees) && Math.Abs(position.LatitudeDegrees) <= 90 &&
        double.IsFinite(position.LongitudeDegrees) && Math.Abs(position.LongitudeDegrees) <= 180;
}

/// <summary>Zusammenhängender AutoLand-Empfang; unveränderte Werte behalten den Frame-Zeitstempel.</summary>
public sealed record AutoLandFlightSample(DateTime SampleUtc, double LatitudeDegrees, double LongitudeDegrees,
    double HeadingTrueDegrees, double HeadingMagneticDegrees, double GroundTrackTrueDegrees,
    double IndicatedAirspeedKnots, double IndicatedAltitudeFeet)
{
    public bool IsFreshAt(DateTime nowUtc) => SampleUtc.Kind == DateTimeKind.Utc && SampleUtc != default &&
        nowUtc.Kind == DateTimeKind.Utc && SampleUtc <= nowUtc && (nowUtc - SampleUtc).TotalSeconds <= 2 &&
        double.IsFinite(LatitudeDegrees) && double.IsFinite(LongitudeDegrees) &&
        double.IsFinite(HeadingTrueDegrees) && double.IsFinite(HeadingMagneticDegrees) &&
        double.IsFinite(GroundTrackTrueDegrees) && double.IsFinite(IndicatedAirspeedKnots) &&
        double.IsFinite(IndicatedAltitudeFeet);
}

public sealed record AutoLandRolloutState(DateTime? SampleUtc = null, double? HeadingErrorDegrees = null);
public sealed record AutoLandRolloutInput(AutoLandPlan Plan, AutoLandPosition Position,
    double HeadingTrueDegrees, double GroundSpeedMetersPerSecond, double RudderPosition,
    bool OnGround, DateTime SampleUtc, DateTime NowUtc);
public sealed record AutoLandRolloutOutput(AutoLandRolloutState State, bool HasCommand, double RudderPosition = 0);
public static class AutoLandRolloutController
{
    public static AutoLandRolloutOutput Update(AutoLandRolloutState state, AutoLandRolloutInput input)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(input);
        if (!input.OnGround || !double.IsFinite(input.GroundSpeedMetersPerSecond) || input.GroundSpeedMetersPerSecond <= 0.1 ||
            !double.IsFinite(input.HeadingTrueDegrees) || !double.IsFinite(input.RudderPosition) ||
            Math.Abs(input.RudderPosition) > 1 || input.SampleUtc == default ||
            input.SampleUtc.Kind != DateTimeKind.Utc || input.NowUtc.Kind != DateTimeKind.Utc ||
            input.SampleUtc > input.NowUtc || (input.NowUtc - input.SampleUtc).TotalSeconds > 2)
            return new(new(), false);
        double dt = state.SampleUtc is { } time ? (input.SampleUtc - time).TotalSeconds : 0.02;
        if (dt <= 0 || dt > 2) return new(new(), false);
        double cross = AutoLandDirectController.GetOffsetsMeters(input.Plan.Runway.Center,
            input.Plan.CourseTrueDegrees, input.Position).Cross;
        double correction = -Math.Atan2(cross, Math.Max(5, input.GroundSpeedMetersPerSecond * 3)) * 180 / Math.PI;
        double error = Math.IEEERemainder(input.Plan.CourseTrueDegrees + correction - input.HeadingTrueDegrees, 360);
        double derivative = state.HeadingErrorDegrees is double previous
            ? Math.IEEERemainder(error - previous, 360) / dt : 0;
        // Vorläufige lokale Boden-Auslegung, Vorzeichen wie vorhandene Autostart-Seitenruderführung.
        double rudder = Math.Clamp(input.RudderPosition + (error / 60 + derivative * 0.1) * dt, -1, 1);
        return new(new(input.SampleUtc, error), true, rudder);
    }
}
