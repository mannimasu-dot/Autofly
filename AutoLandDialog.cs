using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.FlightSimulator.SimConnect;

namespace AutoFly;

public partial class AutoflyMain
{
    private enum AutoLandDefinitionId : uint
    {
        Airport = 14000, FlightSample = 14006, AileronPosition = 14007, RudderPosition = 14009,
        ElevatorPosition = 14010, Throttle1 = 14011, Throttle2, Throttle3, Throttle4
    }
    private enum AutoLandEventId : uint { LeftBrake = 14020, RightBrake, FlapsStageOne, FlapsRetract, GearDown, FlapsStageTwo, FlapsFull }
    private enum AutoLandRequestId : uint { First = 0xC0000000 }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct AutoLandFlightData
    {
        public double Latitude, Longitude, HeadingTrue, HeadingMagnetic, GroundTrack, IndicatedAirspeed,
            IndicatedAltitude, AltitudeAboveGroundFeet, AltitudeMsl, BankDegrees, GroundSpeed, OnGround, RudderPosition,
            VerticalSpeedFeetPerSecond, ElevatorPosition, EngineCount, Throttle1, Throttle2, Throttle3, Throttle4,
            PitchDegrees, SimulationSeconds, TrueAirspeedKnots, WeightPounds;
    }

    private sealed class AutoLandFacility(string icao, string region)
    {
        internal string Icao { get; } = icao;
        internal string Region { get; } = region;
        internal AirportFields? Airport;
        internal uint AirportUniqueId;
        internal bool Invalid;
        internal bool Complete;
        internal uint Request;
        internal double DistanceNauticalMiles;
        internal Dictionary<uint, AirportRunwayItem> Runways { get; } = [];
    }

    private sealed record AutoLandChoice(AutoLandFacility Facility, AutoLandAirport Airport);

    private readonly Dictionary<ToolStripItem, bool> autoLandPreviousEnabled = [];
    private readonly Dictionary<uint, AutoLandFacility> autoLandFacilities = [];
    private readonly Dictionary<string, AutoLandFacility> autoLandAirportList = [];
    private readonly HashSet<uint> autoLandListPackets = [];
    private readonly Dictionary<uint, uint> autoLandSentPackets = [];
    private AutoLandState autoLandState = new();
    private AutoLandEntryPlan? autoLandEntry;
    private AutoLandAlignmentState autoLandAlignment = new();
    private AutoLandDirectState autoLandDirect = new();
    private bool autoLandDirectActive;
    private bool autoLandPastP4;
    private AutoLandLandingState autoLandLanding = new();
    private AutoLandFlightData? autoLandLongitudinalFrame;
    private AutoLandLandingOutput? autoLandLongitudinalOutput;
    private double? autoLandLastLandingLogSeconds;
    private AutoLandFlightData? autoLandLastAirborneFrame;
    private bool autoLandLandingTelemetryMissing, autoLandOutsideStopReported;
    private double autoLandApproachSpeedKnots, autoLandClimbSpeedKnots;
    private SimConnect? autoLandElevatorConnection;
    private AutoLandRolloutState autoLandRollout = new();
    private SimConnect? autoLandRudderConnection;
    private bool autoLandAxisOwned;
    private SimConnect? autoLandAxisConnection;
    private double autoLandBankDegrees = double.NaN;
    private uint autoLandFlightRequest = 0xAFFFFFFF;
    private AutoLandFlightSample? autoLandFlightSample;
    private double autoLandAltitudeMslFeet = double.NaN;
    private SimConnect? autoLandFlightConnection;
    private DateTime autoLandAlignmentStartedUtc;
    private bool autoLandTelemetryMissing;
    private CancellationTokenSource? autoLandCancellation;
    private SimConnect? autoLandConnection;
    private SimConnect? autoLandDefinitionConnection;
    private bool autoLandDefinitionRegistered;
    private uint autoLandSequence = (uint)AutoLandRequestId.First;
    private uint autoLandListRequest;
    private uint? autoLandListPacketCount;
    private TaskCompletionSource<bool>? autoLandListReady;
    private TaskCompletionSource<AutoLandPlan?>? autoLandSelection;
    private Form? autoLandDialog;
    private ListView? autoLandChoices;
    private bool? autoLandWaterLanding;
    private AutoLandFacilityLoadQueue<AutoLandFacility>? autoLandLoadQueue;
    private AutoLandPosition? autoLandSearchOrigin;
    private double autoLandSearchRadiusNauticalMiles;

    private string AutoLandText(string key) => Resources_DE.GetString("AutoLand." + key);

    private ToolStripItem[] AutoLandLockedItems =>
    [
        tsbAP, tsbALT, TsbATT, tsbHDG, TsbLVL, tsbAutoThrottle,
        TsbAutoElevation, tsbTakeOff, TsbFollow, tstbHeading, tstbAltitude, GetCurrHDG, GetCurrALT
    ];

    private void InitializeAutoLandControls()
    {
        tsbAutoland.Click += TsbAutoland_ClickAsync;
        tsbAutoland.AccessibleName = AutoLandText("Land");
        tsbAutoland.AccessibleDescription = AutoLandText("Description");
        tsbAutoland.ToolTipText = AutoLandText("Description");
        SetAutoLandButton(false);
        Disposed += AutoLandDisposed;
    }

    private void SetAutoLandButton(bool active)
    {
        tsbAutoland.Checked = active;
        tsbAutoland.BackColor = active ? Color.LightGreen : Color.LightGray;
        tsbAutoland.Text = AutoLandText(active ? "Abort" : "Land");
        tsbAutoland.Enabled = true;
    }

    private async void TsbAutoland_ClickAsync(object? sender, EventArgs e)
    {
        long session = autoLandState.Session;
        try
        {
            if (autoLandState.Phase != AutoLandPhase.Inactive)
            {
                await CancelAutoLandAsync();
                return;
            }

            if (!TryGetWaypointTelemetry(Flight_Data.SIM_ON_GROUND.Value, out double onGround) || onGround != 0)
                return;

            // Autostart stellt seine Sperren selbst wieder her, bevor AutoLand sie übernimmt.
            CancelAutostart(preserveModes: true);
            SetWaypointFollowing(false);
            terrainFollow.SetEnabled(false);
            TsbAutoElevation.Checked = false;
            TsbAutoElevation.BackColor = Color.LightGray;
            autoLandState = AutoLand.Update(autoLandState, new(AutoLandAction.Begin));
            autopilot?.AutoThrottleOnOff(false);
            _isAutoThrottleActive = false;
            tsbAutoThrottle.BackColor = Color.LightGray;
            session = autoLandState.Session;
            autoLandCancellation = new();
            CancellationToken token = autoLandCancellation.Token;
            foreach (ToolStripItem item in AutoLandLockedItems)
            {
                autoLandPreviousEnabled[item] = item.Enabled;
                item.Enabled = false;
            }
            SetAutoLandButton(true);
            LogAutoLandStatus(AutoLandText("Loading"));
            if (simconnect is not { } connection)
            {
                LogAutoLandStatus(AutoLandText("Unavailable"));
                return;
            }
            autopilot?.DisablePlaneAutopilot();
            AttachAutoLandFacilities(connection);
            RegisterAutoLandDefinition(connection);
            StartAutoLandFlightTelemetry(connection);
            IReadOnlyList<AutoLandFacility> nearby = await AutoLandAirportSearch.FindAsync<AutoLandFacility>(
                (radius, cancellation) => RequestNearbyAutoLandAirportsAsync(connection, radius, cancellation), token);
            token.ThrowIfCancellationRequested();
            if (!AutoLandSessionActive(session))
                return;
            if (nearby.Count == 0)
            {
                DetachAutoLandFacilities();
                LogAutoLandStatus(AutoLandText("NoNearbyAirports"));
                return;
            }
            AutoLandPlan? plan = await ChooseAutoLandAirportAsync(connection, token);
            token.ThrowIfCancellationRequested();
            if (!AutoLandSessionActive(session))
                return;
            if (plan == null)
            {
                await CancelAutoLandAsync();
                return;
            }
            LogAutoLandPlan(plan);
            if (webView2.CoreWebView2 is not { } core)
            {
                LogAutoLandStatus(AutoLandText("MapUnavailable"));
                return;
            }
            if (!TryGetWaypointTelemetry(Flight_Data.PLANE_LATITUDE.Value, out double latitude) ||
                !TryGetWaypointTelemetry(Flight_Data.PLANE_LONGITUDE.Value, out double longitude) ||
                !TryGetWaypointTelemetry(Flight_Data.PLANE_HEADING_DEGREES_TRUE.Value, out double trueHeading))
            {
                Debug.WriteLine($"[Debug] {DateTime.Now:yyyy-MM-dd HH:mm:ss} - [AutoLand] Anflugmarkierung: Position oder wahrer Kurs fehlt.");
                return;
            }
            // Der vom Benutzer zum Nachfliegen freigegebene bestehende Kartenradius: 1 NM.
            const double entryRadiusMeters = 1852;
            AutoLandEntryPlan entry = AutoLandEntryPath.Create(new(new(latitude, longitude), trueHeading,
                plan.Points.Single(point => point.Label == "PA").Position, plan.CourseTrueDegrees, entryRadiusMeters));
            string arrows = JsonSerializer.Serialize(entry.Arrows.Select(arrow => new
            {
                lat = arrow.Position.LatitudeDegrees,
                lon = arrow.Position.LongitudeDegrees,
                heading = arrow.HeadingTrueDegrees
            }));
            string points = JsonSerializer.Serialize(plan.Points.Select(point => new
            {
                label = AutoLandText("MapPoint." + point.Label),
                lat = point.Position.LatitudeDegrees,
                lon = point.Position.LongitudeDegrees
            }));
            string result = await core.ExecuteScriptAsync($"window.setAutoLandMarkers({session}, {points}, {arrows});");
            token.ThrowIfCancellationRequested();
            if (!AutoLandSessionActive(session))
                return;
            if (result != "true")
            {
                LogAutoLandStatus(AutoLandText("MapUnavailable"));
                return;
            }
            autoLandState = AutoLand.Update(autoLandState, new(AutoLandAction.Mark, session, plan));
            DetachAutoLandFacilities();
            LogAutoLandStatus(string.Format(CultureInfo.CurrentCulture, AutoLandText("Marked"),
                plan.Airport.Icao, plan.SelectedDirection));
            StartAutoLandAlignment(entry, session);
        }
        catch (OperationCanceledException) when (!AutoLandSessionActive(session))
        {
            // Ausschließlich expliziter Benutzerabbruch beziehungsweise Formularende.
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException or TimeoutException)
        {
            if (AutoLandSessionActive(session))
                ReportAutoLandFailure(ex);
        }
    }

    private bool AutoLandSessionActive(long session) => !IsDisposed && !Disposing &&
        autoLandState.Phase != AutoLandPhase.Inactive && autoLandState.Session == session;

    private static void LogAutoLandStatus(string message) =>
        Debug.WriteLine($"[Debug] {DateTime.Now:yyyy-MM-dd HH:mm:ss} - [AutoLand] {message.Replace("[AutoLand] ", string.Empty, StringComparison.Ordinal)}");

    private bool TryReadAutoLandAlignment(out AutoLandAlignmentInput input)
    {
        input = null!;
        if (autoLandEntry is not { } entry)
            return false;
        DateTime now = DateTime.UtcNow;
        // Ein zusammenhängender Frame statt einer Mischung einzelner CHANGED-Zeitstempel.
        if (autoLandFlightSample is not { } telemetry || !telemetry.IsFreshAt(now) ||
            !TryGetWaypointTelemetry(Flight_Model.DESIGN_SPEED_VS1.Value, out double stall) || stall <= 0 ||
            !TryGetWaypointTelemetry(Flight_Model.DESIGN_SPEED_VC.Value, out double cruise) || cruise <= stall ||
            !TryGetWaypointTelemetry(Flight_Model.DESIGN_SPEED_CLIMB.Value, out double climb) || climb <= 0 ||
            climb > cruise || !double.IsFinite(autoLandAltitudeMslFeet) || autoLandState.Plan is not { } plan)
            return false;
        double speed = telemetry.IndicatedAirspeedKnots;
        double latitude = telemetry.LatitudeDegrees, longitude = telemetry.LongitudeDegrees;
        if (speed <= 0 || Math.Abs(latitude) > 90 || Math.Abs(longitude) > 180)
            return false;
        input = new(entry, new(latitude, longitude), telemetry.HeadingTrueDegrees,
            telemetry.GroundTrackTrueDegrees, speed, stall, cruise,
            autoLandAltitudeMslFeet - plan.Runway.AltitudeMeters / 0.3048, telemetry.SampleUtc);
        return true;
    }

    private void StartAutoLandAlignment(AutoLandEntryPlan entry, long session)
    {
        if (!AutoLandSessionActive(session) || autoLandState.Phase != AutoLandPhase.Marking)
            return;
        autoLandEntry = entry;
        autoLandAlignment = new();
        bool telemetryReady = TryReadAutoLandAlignment(out AutoLandAlignmentInput sample);
        AutoLandAlignmentStartOutput start = AutoLand.GetAlignmentStart(new(autoLandState, session,
            autopilot is { IsReady: true }, telemetryReady));
        if (!start.Start || autopilot == null)
        {
            if (start.Waiting && !autoLandTelemetryMissing)
                LogAutoLandStatus("Ausrichtung wartet auf bereiten Autopiloten und vollständige frische Flug-/Modelltelemetrie; Start wird automatisch nachgeholt.");
            autoLandTelemetryMissing = true;
            return;
        }
        autoLandAlignmentStartedUtc = DateTime.UtcNow;
        autoLandTelemetryMissing = false;
        autoLandApproachSpeedKnots = sample.CruiseSpeedKnots;
        TryGetWaypointTelemetry(Flight_Model.DESIGN_SPEED_CLIMB.Value, out autoLandClimbSpeedKnots);
        _targetAltitude = (int)Math.Round(autoLandFlightSample!.IndicatedAltitudeFeet +
            AutoLandAlignment.TargetAltitudeFeet - sample.AltitudeFeet);
        _targetHeading = RoundTargetHeading(autoLandFlightSample!.HeadingMagneticDegrees);
        autopilot.localTargetAltDeg = _targetAltitude;
        autopilot.LocalTargetHdgDeg = _targetHeading;
        autopilot.AutoThrottleOnOff(true);
        autopilot.DisablePlaneAutopilot();
        if (!_isLocalAutopilotActive)
        {
            autopilot.AutopilotLocalOnOff(true);
        }
        if (!_isALTActive) autopilot.AltOnOff(true);
        if (!_isHDGActive) autopilot.HdgOnOff(true);
        if (!_isLVLActive) autopilot.LvlOnOff(true);
        if (!_isATTActive) autopilot.AttOnOff(true);
        _isAutoThrottleActive = true;
        tsbAutoThrottle.BackColor = Color.LightGreen;
        _isLocalAutopilotActive = _isALTActive = _isHDGActive = _isLVLActive = _isATTActive = true;
        foreach (ToolStripItem item in new ToolStripItem[] { tsbAP, tsbALT, tsbHDG, TsbLVL, TsbATT })
            item.BackColor = Color.LightGreen;
        tstbAltitude.Text = _targetAltitude.ToString(CultureInfo.InvariantCulture);
        tstbHeading.Text = _targetHeading.ToString(CultureInfo.InvariantCulture);
        autoLandState = AutoLand.Update(autoLandState, new(AutoLandAction.Align, session));
        LogAutoLandStatus(FormattableString.Invariant($"Ausrichten: IAS={sample.IndicatedAirspeedKnots:F2}kt; AutoThrottle AN; ALT-Soll={_targetAltitude}ft indicated, 1500ft über Bahn; Radius=1852m; Kurve={entry.Kind}; Länge={entry.LengthMeters:F1}m"));
    }

    private void ApplyAutoLandAlignment()
    {
        if (autoLandDirectActive)
        {
            if (autoLandFlightSample is not { } current || !current.IsFreshAt(DateTime.UtcNow))
            {
                ResetAutoLandLandingTelemetry();
            }
            return;
        }
        // Die Dialogpause kann vorübergehend alte Messwerte hinterlassen.
        // Die markierte Sitzung behält ihren Pfad und startet erst mit frischen Daten.
        if (autoLandState.Phase == AutoLandPhase.Marking && autoLandEntry is { } pendingEntry)
            StartAutoLandAlignment(pendingEntry, autoLandState.Session);
        if (autoLandState.Phase != AutoLandPhase.Aligning || autoLandAlignment.PaReached ||
            autopilot == null || autoLandEntry == null)
            return;
        if (!TryReadAutoLandAlignment(out AutoLandAlignmentInput sample))
        {
            if (!autoLandTelemetryMissing)
                LogAutoLandStatus("Ausrichtung: Telemetrie fehlt/veraltet; keine neuen Kurven-Sollwerte.");
            autoLandTelemetryMissing = true;
            return;
        }
        if (autoLandAlignment.SampleUtc is DateTime previous && sample.SampleUtc <= previous)
            return;
        if (autoLandTelemetryMissing)
        {
            LogAutoLandStatus("Ausrichtung: Telemetrie wieder gültig.");
            autoLandAlignment = autoLandAlignment with { PreviousPosition = null, SampleUtc = null };
            autoLandTelemetryMissing = false;
        }
        AutoLandAlignmentState previousAlignment = autoLandAlignment;
        SetAutoLandAltitudeTarget((int)Math.Round(autoLandFlightSample!.IndicatedAltitudeFeet +
            AutoLandAlignment.TargetAltitudeFeet - sample.AltitudeFeet));
        AutoLandAlignmentOutput output = AutoLandAlignment.Update(autoLandAlignment, sample);
        autoLandAlignment = output.State;
        if (output.State.PaReached)
        {
            // Den letzten HDG-Sollwert erhalten; ab diesem Frame nur noch die direkte Achse führen.
            if (autoLandState.Plan is { } plan && autoLandFlightSample is { } telemetry &&
                plan.Points.FirstOrDefault(point => point.Label == "PA") is { } pa)
                WriteAutoLandPassageDebug(plan, pa, telemetry, FormattableString.Invariant($"Abstand={output.DistanceToPaMeters:F1}m"));
            StartAutoLandDirect(previousAlignment, sample.IndicatedAirspeedKnots);
            return;
        }
        ApplyAutoLandHeading(output.HeadingTrueDegrees, sample);
    }

    private void StartAutoLandDirect(AutoLandAlignmentState previousAlignment, double paEntrySpeedKnots)
    {
        if (autoLandDirectActive || autopilot == null)
            return;
        autoLandDirectActive = true;
        autoLandDirect = new(PreviousPosition: previousAlignment.PreviousPosition,
            SampleUtc: previousAlignment.SampleUtc);
        autoLandLanding = new(PaEntrySpeedKnots: paEntrySpeedKnots);
        autoLandLongitudinalOutput = null;
        autoLandLandingTelemetryMissing = autoLandOutsideStopReported = false;
        autoLandPastP4 = false;
        // Alle bisherigen Neutral-/Freigabebefehle müssen vor dem ersten direkten Achsenbefehl liegen.
        DisableAutoLandAutopilot();
        // Der Reise-AutoThrottle verwendet fest VC und darf die direkten Schubwerte nicht überschreiben.
        _isAutoThrottleActive = true;
        tsbAutoThrottle.BackColor = Color.LightGreen;
    }

    private bool ApplyAutoLandDirect(AutoLandFlightSample sample)
    {
        if (!autoLandDirectActive || autoLandState.Plan is not { } plan ||
            autoLandFlightConnection is not { } connection || !ReferenceEquals(connection, simconnect))
            return false;
        int previousPoint = autoLandDirect.NextPointIndex;
        AutoLandDirectOutput output = AutoLandDirectController.Update(autoLandDirect,
            new(plan, new(sample.LatitudeDegrees, sample.LongitudeDegrees), sample.HeadingTrueDegrees,
                sample.GroundTrackTrueDegrees, autoLandBankDegrees, sample.IndicatedAirspeedKnots,
                sample.SampleUtc, DateTime.UtcNow, ContinueAfterP5: true));
        autoLandDirect = output.State;
        for (int index = previousPoint; index < output.State.NextPointIndex; index++)
            WriteAutoLandPassageDebug(plan, plan.Points[index], sample, "");
        if (!output.HasCommand)
        {
            return output.Completed;
        }
        autoLandAxisConnection = connection;
        autoLandAxisOwned = true;
        try
        {
            connection.SetDataOnSimObject(AutoLandDefinitionId.AileronPosition,
                SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_DATA_SET_FLAG.DEFAULT, output.AileronPosition);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            ReleaseAutoLandAxis();
            throw;
        }
        return false;
    }

    private void ReleaseAutoLandAxis()
    {
        if (!autoLandAxisOwned)
            return;
        try
        {
            autoLandAxisConnection?.SetDataOnSimObject(AutoLandDefinitionId.AileronPosition,
                SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_DATA_SET_FLAG.DEFAULT, 0.0);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            Debug.WriteLine($"[Debug] {DateTime.Now:yyyy-MM-dd HH:mm:ss} - [AutoLand] Querruderfreigabe fehlgeschlagen: {ex.Message}");
        }
        finally
        {
            autoLandAxisOwned = false;
            autoLandAxisConnection = null;
        }
    }

    private void DisableAutoLandAutopilot()
    {
        autopilot?.AutoThrottleOnOff(false);
        autopilot?.HdgOnOff(false);
        autopilot?.LvlOnOff(false);
        autopilot?.AltOnOff(false);
        autopilot?.AttOnOff(false);
        autopilot?.AutopilotLocalOnOff(false);
        _isLocalAutopilotActive = _isALTActive = _isHDGActive = _isLVLActive = _isATTActive = _isAutoThrottleActive = false;
        foreach (ToolStripItem item in new ToolStripItem[] { tsbAP, tsbALT, TsbATT, tsbHDG, TsbLVL, tsbAutoThrottle })
            item.BackColor = Color.LightGray;
    }

    private void ApplyAutoLandHeading(double headingTrueDegrees, AutoLandAlignmentInput sample)
    {
        if (autopilot == null) return;
        double declination = sample.HeadingTrueDegrees - autoLandFlightSample!.HeadingMagneticDegrees;
        _targetHeading = RoundTargetHeading(headingTrueDegrees - declination);
        autopilot.LocalTargetHdgDeg = _targetHeading;
        double displayHeading = ((headingTrueDegrees - declination) % 360.0 + 360.0) % 360.0;
        displayHeading = Math.Round(displayHeading, 0, MidpointRounding.AwayFromZero) % 360.0;
        tstbHeading.Text = displayHeading.ToString("F0", CultureInfo.GetCultureInfo("de-DE"));
    }

    private void HoldAutoLandCurrentDirectionAndAltitude()
    {
        if (autopilot == null)
            return;
        if (TryGetWaypointTelemetry(Flight_Data.PLANE_HEADING_DEGREES_MAGNETIC.Value, out double heading))
        {
            _targetHeading = RoundTargetHeading(heading);
            autopilot.LocalTargetHdgDeg = _targetHeading;
            tstbHeading.Text = _targetHeading.ToString(CultureInfo.InvariantCulture);
        }
        if (!autoLandDirectActive && TryGetWaypointTelemetry(Flight_Data.INDICATED_ALTITUDE.Value, out double altitude) &&
            altitude >= int.MinValue && altitude <= int.MaxValue)
        {
            _targetAltitude = (int)Math.Round(altitude);
            autopilot.localTargetAltDeg = _targetAltitude;
            tstbAltitude.Text = _targetAltitude.ToString(CultureInfo.InvariantCulture);
        }
    }

    private uint NextAutoLandRequest()
    {
        if (autoLandSequence == uint.MaxValue)
            throw new InvalidOperationException(AutoLandText("Unavailable"));
        return ++autoLandSequence;
    }

    private void TrackAutoLandPacket(SimConnect connection, uint request)
        => autoLandSentPackets[connection.GetLastSentPacketID()] = request;

    private void AttachAutoLandFacilities(SimConnect connection)
    {
        autoLandConnection = connection;
        connection.OnRecvAirportList += ReceiveAutoLandAirportList;
        connection.OnRecvFacilityData += ReceiveAutoLandFacility;
        connection.OnRecvFacilityDataEnd += ReceiveAutoLandFacilityEnd;
        connection.OnRecvException += ReceiveAutoLandException;
    }

    private void StartAutoLandFlightTelemetry(SimConnect connection)
    {
        autoLandDirect = new();
        autoLandLanding = new();
        autoLandLastLandingLogSeconds = null;
        autoLandLastAirborneFrame = null;
        autoLandBankDegrees = double.NaN;
        autoLandFlightSample = null;
        autoLandAltitudeMslFeet = double.NaN;
        autoLandFlightConnection = connection;
        // Eine neue Request-ID trennt auch bereits gepufferte Frames früherer Sitzungen.
        autoLandFlightRequest = checked(autoLandFlightRequest + 1);
        // DEFAULT statt CHANGED: Auch unveränderte Werte benötigen einen frischen Frame-Zeitstempel.
        connection.RequestDataOnSimObject((AutoLandRequestId)autoLandFlightRequest, AutoLandDefinitionId.FlightSample,
            SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_PERIOD.SIM_FRAME,
            SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT, 0, 0, 0);
    }

    private bool ReceiveAutoLandFlightSample(SimConnect sender, SIMCONNECT_RECV_SIMOBJECT_DATA data)
    {
        if (data.dwDefineID != (uint)AutoLandDefinitionId.FlightSample) return false;
        if (data.dwRequestID != autoLandFlightRequest ||
            !ReferenceEquals(sender, autoLandFlightConnection) || !ReferenceEquals(sender, simconnect) ||
            autoLandState.Phase == AutoLandPhase.Inactive) return true;
        long session = autoLandState.Session;
        AutoLandFlightData frame = (AutoLandFlightData)data.dwData[0];
        autoLandFlightSample = new(DateTime.UtcNow, frame.Latitude, frame.Longitude, frame.HeadingTrue,
            frame.HeadingMagnetic, frame.GroundTrack, frame.IndicatedAirspeed, frame.IndicatedAltitude);
        autoLandAltitudeMslFeet = frame.AltitudeMsl;
        autoLandBankDegrees = frame.BankDegrees;
        autoLandLongitudinalFrame = frame;
        ApplyAutoLandAlignment();
        if (!autoLandDirectActive || !AutoLandSessionActive(session))
            return true;
        ApplyAutoLandLanding(sender, frame, autoLandFlightSample);
        return true;
    }

    private void ApplyAutoLandLanding(SimConnect connection, AutoLandFlightData frame, AutoLandFlightSample sample)
    {
        if (autoLandState.Plan is not { } plan || !sample.IsFreshAt(DateTime.UtcNow) ||
            Math.Abs(frame.Latitude) > 90 || Math.Abs(frame.Longitude) > 180 || frame.OnGround is not (0 or 1) ||
            !double.IsFinite(frame.EngineCount) || frame.EngineCount is < 1 or > 4 ||
            frame.EngineCount != Math.Truncate(frame.EngineCount))
        {
            ResetAutoLandLandingTelemetry();
            return;
        }
        double[] throttles = [frame.Throttle1, frame.Throttle2, frame.Throttle3, frame.Throttle4];
        AutoLandLandingState previous = autoLandLanding;
        AutoLandLandingOutput output = AutoLandLandingController.Update(previous,
            new(plan, new(frame.Latitude, frame.Longitude), frame.AltitudeMsl, frame.AltitudeAboveGroundFeet,
                frame.VerticalSpeedFeetPerSecond, frame.GroundSpeed, frame.IndicatedAirspeed, frame.ElevatorPosition,
                Array.AsReadOnly(throttles[..(int)frame.EngineCount]), autoLandApproachSpeedKnots,
                autoLandClimbSpeedKnots, frame.OnGround == 1, sample.SampleUtc, DateTime.UtcNow,
                frame.PitchDegrees, frame.SimulationSeconds, frame.TrueAirspeedKnots, frame.WeightPounds));
        autoLandLanding = output.State;
        if (!output.HasCommand || output.ThrottlePositionsPercent is not { } commands)
        {
            if (output.State.PreviousPitchNoseUpDegrees is null)
                ResetAutoLandLandingTelemetry();
            return;
        }
        autoLandLongitudinalOutput = output;
        if (autoLandLandingTelemetryMissing)
            LogAutoLandStatus("Längsführung: Telemetrie wieder gültig; Ableitungen neu gestartet, Stellzustand erhalten.");
        autoLandLandingTelemetryMissing = false;
        LogAutoLandFinalApproach(plan, frame, previous, output);
        if (previous.ElevatorCommand is null)
            LogAutoLandStatus(GetAutoLandLongitudinalDebug());
        if (output.State.ThrottleCut && !previous.ThrottleCut)
            LogAutoLandStatus("Schubabschaltung: 5ft AGL beziehungsweise bestätigtes Aufsetzen; Schub bleibt null.");
        if (output.State.TouchdownConfirmed && !previous.TouchdownConfirmed)
        {
            DisableAutoLandAutopilot();
            LogAutoLandStatus(FormattableString.Invariant($"Bodenkontakt bestätigt; AP AUS, maximale Bremsen"));
        }
        if (output.StoppedOutsideRunway && !autoLandOutsideStopReported)
        {
            autoLandOutsideStopReported = true;
            LogAutoLandStatus("Stillstand außerhalb der Bahnfläche: keine erfolgreiche Landung; Schub bleibt null, Bremsen bleiben aktiv, kein automatischer Phasenabbruch.");
        }
        // Auch nach dem Cut schreibt nur AutoLand: kein konkurrierender Reisegeschwindigkeitsregler.
        for (int engine = 0; engine < commands.Count; engine++)
            connection.SetDataOnSimObject((AutoLandDefinitionId)((uint)AutoLandDefinitionId.Throttle1 + engine),
                SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_DATA_SET_FLAG.DEFAULT, commands[engine]);
        autoLandElevatorConnection = connection;
        connection.SetDataOnSimObject(AutoLandDefinitionId.ElevatorPosition,
            SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_DATA_SET_FLAG.DEFAULT, output.ElevatorPosition);
        SendAutoLandBrakes(connection, output.FullBrakes);
        if (output.FlapsIndex is int flapsIndex)
        {
            AutoLandEventId eventId = flapsIndex switch
            {
                0 => AutoLandEventId.FlapsRetract,
                1 => AutoLandEventId.FlapsStageOne,
                2 => AutoLandEventId.FlapsStageTwo,
                3 => AutoLandEventId.FlapsFull,
                _ => throw new InvalidOperationException($"Ungültige AutoLand-Klappenstufe: {flapsIndex}.")
            };
            connection.TransmitClientEvent(SimConnect.SIMCONNECT_OBJECT_ID_USER,
                eventId,
                0, (AutoLandRequestId)0, SIMCONNECT_EVENT_FLAG.GROUPID_IS_PRIORITY);
        }
        if (output.RequestGearDown)
        {
            connection.TransmitClientEvent(SimConnect.SIMCONNECT_OBJECT_ID_USER, AutoLandEventId.GearDown,
                0, (AutoLandRequestId)0, SIMCONNECT_EVENT_FLAG.GROUPID_IS_PRIORITY);
            LogAutoLandStatus("Fahrwerk bei P3 ausgefahren");
        }
        if (output.State.TouchdownConfirmed)
        {
            ReleaseAutoLandAxis();
            AutoLandRolloutOutput rollout = AutoLandRolloutController.Update(autoLandRollout,
                new(plan, new(frame.Latitude, frame.Longitude), frame.HeadingTrue,
                    frame.GroundSpeed, frame.RudderPosition, frame.OnGround == 1, sample.SampleUtc, DateTime.UtcNow));
            autoLandRollout = rollout.State;
            if (rollout.HasCommand)
            {
                autoLandRudderConnection = connection;
                connection.SetDataOnSimObject(AutoLandDefinitionId.RudderPosition,
                    SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_DATA_SET_FLAG.DEFAULT, rollout.RudderPosition);
            }
        }
        else
        {
            ApplyAutoLandDirect(sample);
            autoLandPastP4 |= autoLandDirect.NextPointIndex >= 5;
        }
        if (output.Completed)
        {
            double along = AutoLandDirectController.GetOffsetsMeters(plan.Points[4].Position,
                plan.CourseTrueDegrees, new(frame.Latitude, frame.Longitude)).Along;
            LogAutoLandStatus(FormattableString.Invariant($"Landung beendet: Stillstand bei {along:F1}m ({100 * along / plan.Runway.LengthMeters:F1}% Bahnlänge); Klappen eingefahren, Regler AUS"));
            StopAutoLand();
        }
    }

    private void LogAutoLandFinalApproach(AutoLandPlan plan, AutoLandFlightData frame,
        AutoLandLandingState previous, AutoLandLandingOutput output)
    {
        if (!output.State.PassedP1 || previous.TouchdownConfirmed)
            return;

        bool firstGroundSample = frame.OnGround == 1 && previous.GroundContactSamples == 0;
        bool contactConfirmed = output.State.TouchdownConfirmed && !previous.TouchdownConfirmed;
        bool throttleCut = output.State.ThrottleCut && !previous.ThrottleCut;
        bool logDue = autoLandLastLandingLogSeconds is not double last ||
            frame.SimulationSeconds < last || frame.SimulationSeconds - last >= 0.1;
        if (logDue || firstGroundSample || contactConfirmed || throttleCut)
        {
            //LogAutoLandStatus(FormattableString.Invariant($"Längsführung ab P1: Simzeit={frame.SimulationSeconds:F3}s; Bahnposition={Along:F2}m ({100 * Along / plan.Runway.LengthMeters:F2}%); Aufsetzziel={plan.Runway.LengthMeters * 0.2:F2}m; Quer={Cross:F2}m; AGL={frame.AltitudeAboveGroundFeet:F2}ft; Höhe-über-Bahn={height:F2}ft; Höhe-Soll={output.Curve?.HeightAboveRunwayFeet:F2}ft; Höhenfehler(Ist-Soll)={height - output.Curve?.HeightAboveRunwayFeet:F2}ft; IAS-Ist={frame.IndicatedAirspeed:F2}kt; VY={autoLandClimbSpeedKnots:F2}kt; GS={frame.GroundSpeed:F2}m/s; VS-Ist={frame.VerticalSpeedFeetPerSecond * 60:F1}ft/min; VS-Soll={output.TargetVerticalSpeedFeetPerSecond * 60:F1}ft/min; Bodenkontakt-Ist={frame.OnGround == 1}; Bodenkontakt-bestätigt={output.State.TouchdownConfirmed}; Schubcut={output.State.ThrottleCut}; ") + GetAutoLandLongitudinalDebug(frame, previous, output));
            autoLandLastLandingLogSeconds = frame.SimulationSeconds;
        }
        if (firstGroundSample)
        {
            if (autoLandLastAirborneFrame is { } airborne)
                LogAutoLandStatus(FormattableString.Invariant($"Erster gemeldeter Bodenkontakt: letzter gültiger Luft-Frame Simzeit={airborne.SimulationSeconds:F3}s; Abstand-zum-Bodenframe={frame.SimulationSeconds - airborne.SimulationSeconds:F3}s; VS-Luft={airborne.VerticalSpeedFeetPerSecond * 60:F1}ft/min (positiv=Steigen); IAS-Luft={airborne.IndicatedAirspeed:F2}kt; AGL-Luft={airborne.AltitudeAboveGroundFeet:F2}ft; Pitch-Luft(Nose-up)={-airborne.PitchDegrees:F2}deg"));
            else
                LogAutoLandStatus("Erster gemeldeter Bodenkontakt: kein gültiger vorheriger Luft-Frame ab P1 verfügbar; Aufsetzsinkrate nicht bestimmbar.");
        }
        if (frame.OnGround == 0)
            autoLandLastAirborneFrame = frame;
    }

    private static void SendAutoLandBrakes(SimConnect connection, bool fullBrakes)
    {
        uint value = AutostartControlOutput.EncodeBrake(fullBrakes, !fullBrakes, false);
        connection.TransmitClientEvent(SimConnect.SIMCONNECT_OBJECT_ID_USER, AutoLandEventId.LeftBrake,
            value, (AutoLandRequestId)0, SIMCONNECT_EVENT_FLAG.GROUPID_IS_PRIORITY);
        connection.TransmitClientEvent(SimConnect.SIMCONNECT_OBJECT_ID_USER, AutoLandEventId.RightBrake,
            value, (AutoLandRequestId)0, SIMCONNECT_EVENT_FLAG.GROUPID_IS_PRIORITY);
    }

    private void ResetAutoLandLandingTelemetry()
    {
        autoLandLanding = autoLandLanding with
        {
            PreviousVerticalSpeedFeetPerSecond = null, PreviousSpeedKnots = null,
            PreviousPitchNoseUpDegrees = null, GroundContactSamples = 0,
            RunwayContactSamples = 0, StandstillSamples = 0, ThrottleElapsedSeconds = 0
        };
        if (!autoLandLandingTelemetryMissing)
            LogAutoLandStatus("Längsführung: Telemetrie fehlt/ungültig; keine neuen Stellbefehle, letzte Stellungen und Trimmung bleiben erhalten.");
        autoLandLandingTelemetryMissing = true;
    }

    private void ReleaseAutoLandElevator()
    {
        try
        {
            autoLandElevatorConnection?.SetDataOnSimObject(AutoLandDefinitionId.ElevatorPosition,
                SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_DATA_SET_FLAG.DEFAULT, 0.0);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        { LogAutoLandStatus($"Höhenruderfreigabe: {ex.Message}"); }
        finally { autoLandElevatorConnection = null; }
    }

    private void SetAutoLandAltitudeTarget(int altitudeFeet)
    {
        if (autopilot == null)
            return;
        if (_targetAltitude != altitudeFeet)
            _targetAltitude = altitudeFeet;
        if (autopilot.localTargetAltDeg != altitudeFeet)
            autopilot.localTargetAltDeg = altitudeFeet;
        string text = altitudeFeet.ToString(CultureInfo.InvariantCulture);
        if (tstbAltitude.Text != text)
            tstbAltitude.Text = text;
    }

    private void WriteAutoLandPassageDebug(AutoLandPlan plan, AutoLandPoint point,
        AutoLandFlightSample sample, string details)
    {
        double horizontal = AutoLandDirectController.GetOffsetsMeters(point.Position, plan.CourseTrueDegrees,
            new(sample.LatitudeDegrees, sample.LongitudeDegrees)).Cross;
        string altitudeMsl = double.IsFinite(autoLandAltitudeMslFeet)
            ? autoLandAltitudeMslFeet.ToString("F1", CultureInfo.InvariantCulture) : "n/v";
        details += FormattableString.Invariant($" ALT-Soll={_targetAltitude}ft; AP={_isLocalAutopilotActive}; HDG={_isHDGActive}; LVL={_isLVLActive}; ALT={_isALTActive}; ATT={_isATTActive}; AT={_isAutoThrottleActive}.");
        details += " " + GetAutoLandLongitudinalDebug();
        Debug.WriteLine(FormattableString.Invariant($"[Debug] {DateTime.Now:yyyy-MM-dd HH:mm:ss} - [AutoLand] {point.Label} passiert: HDG-wahr={sample.HeadingTrueDegrees:F2}deg; HDG-mag={sample.HeadingMagneticDegrees:F2}deg; Track-wahr={sample.GroundTrackTrueDegrees:F2}deg; ALT={sample.IndicatedAltitudeFeet:F1}ft; ALT-MSL={altitudeMsl}ft; IAS={sample.IndicatedAirspeedKnots:F2}kt; Horizontal={horizontal:+0.000;-0.000;0.000}m; Planmarker-MSL(nur Referenz)={point.AltitudeFeet:F1}ft; Bahnkurs={plan.CourseTrueDegrees:F3}deg wahr; {details}"));
    }

    private static string GetAutoLandLongitudinalDebug(AutoLandFlightData frame,
        AutoLandLandingState previous, AutoLandLandingOutput output)
    {
        AutoLandLandingState state = output.State;
        double dt = previous.SimulationSeconds is { } time ? frame.SimulationSeconds - time : 0;
        bool continuous = dt > 0 && dt <= 2 && previous.PreviousVerticalSpeedFeetPerSecond.HasValue &&
            previous.PreviousSpeedKnots.HasValue && previous.PreviousPitchNoseUpDegrees.HasValue;
        double pitchError = output.TargetPitchNoseUpDegrees + frame.PitchDegrees;
        double desiredRate = state.PreviousDesiredPitchRate.GetValueOrDefault();
        double a = state.PitchDynamics?[0] ?? 0;
        double b = state.PitchDynamics?[1] ?? 0;
        double c = state.PitchDynamics?[2] ?? 0;
        double varianceA = state.PitchDynamicsCovariance?[0] ?? 1e6;
        bool identified = a > 0 && b >= 0 && varianceA < 0.01 * a * a;
        // Nur Diagnose: dieselben unveränderten Formeln wie im AutoLandLandingController,
        // damit die protokollierten Anteile zum aktuellen Frame passen, ohne die Regelung zu beeinflussen.
        double gain = 0.5 * (identified ? (0.04 + b / a) / 1 : 0.04);
        double proportional = gain * pitchError;
        double damping = -0.01 * (state.PitchRateDegreesPerSecond - desiredRate);
        double feedforward = identified ? (b * desiredRate + state.DesiredPitchAcceleration) / a : 0;
        double feedback = proportional + damping + feedforward;
        string dynamics = string.Join(",", state.FlightPathDynamics?.Select(value =>
            value.ToString("G9", CultureInfo.InvariantCulture)) ?? []);

        return FormattableString.Invariant($"dt={dt:F6}s; kontinuierlich={continuous}; P3-passiert={state.PassedP3}; TAS={frame.TrueAirspeedKnots:F3}kt; Pitch-Ist(Nose-up)={-frame.PitchDegrees:F4}deg; Pitch-Soll(Nose-up)={output.TargetPitchNoseUpDegrees:F4}deg; Pitchfehler(Soll-Ist)={pitchError:F4}deg; Pitchrate-roh={state.RawPitchRateDegreesPerSecond:F4}deg/s; Pitchrate-gefiltert={state.PitchRateDegreesPerSecond:F4}deg/s; Pitchrate-Soll={desiredRate:F4}deg/s; Pitchbeschleunigung-Soll={state.DesiredPitchAcceleration:F4}deg/s²; Kurvenrate={state.CurvePathRateDegreesPerSecond:F4}deg/s; Zielpfadrate={state.TargetPathRateDegreesPerSecond:F4}deg/s; Offset-Ist={state.PreviousMeasuredOffsetDegrees:F4}deg; Offset-gefiltert={state.PitchPathOffsetDegrees:F4}deg; Offsetrate={state.OffsetRateDegreesPerSecond:F4}deg/s; Pitch-RLS-A={a:G9}; Pitch-RLS-B={b:G9}; Pitch-RLS-C={c:G9}; Pitch-RLS-VarA={varianceA:G9}; Pitch-RLS-identifiziert={identified}; Pfad-RLS=[{dynamics}]; Pfad-RLS-VarVerzug={state.FlightPathCovariance?[15]:G9}; Pitchgain={gain:G9}; P-Anteil(rekonstruiert)={proportional:F6}; D-Anteil(rekonstruiert)={damping:F6}; Vorsteuerung(rekonstruiert)={feedforward:F6}; Rückführung(rekonstruiert)={feedback:F6}; Elevator-Bias={state.ElevatorBias:F6}; Biasänderung={state.ElevatorBias - previous.ElevatorBias:F6}; Elevator-Summe(rekonstruiert)={state.ElevatorBias + feedback:F6}; Elevator-Ist={frame.ElevatorPosition:F6}; Elevator-Befehl-vorher={previous.ElevatorCommand:F6}; Elevator-Befehl={output.ElevatorPosition:F6}; Elevator-Befehlsänderung={output.ElevatorPosition - previous.ElevatorCommand:F6}; IAS-Soll={output.Curve?.TargetSpeedKnots:F2}kt; Klappenstufe-angefordert={state.RequestedFlapsStage}; Fahrwerk-angefordert={state.GearDownRequested}; Schub1-Ist={frame.Throttle1:F2}%; Schub-Befehl=[{string.Join(",", output.ThrottlePositionsPercent?.Select(value => value.ToString("F2", CultureInfo.InvariantCulture)) ?? [])}]%; ");
    }

    private string GetAutoLandLongitudinalDebug()
    {
        if (autoLandLongitudinalFrame is not { } frame)
            return string.Empty;
        string measured = FormattableString.Invariant($"VS-Ist={frame.VerticalSpeedFeetPerSecond:F2}ft/s; Pitch-Ist(Nose-up)={-frame.PitchDegrees:F2}deg; Elevator-Ist={frame.ElevatorPosition:F4}; Schub1-Ist={frame.Throttle1:F2}%; Simzeit={frame.SimulationSeconds:F3}s;");
        return autoLandLongitudinalOutput is { } output && output.State.SampleUtc == autoLandFlightSample?.SampleUtc
            ? measured + FormattableString.Invariant($" VS-Soll={output.TargetVerticalSpeedFeetPerSecond:F2}ft/s; Pitch-Soll(Nose-up)={output.TargetPitchNoseUpDegrees:F2}deg; Pitchrate={output.PitchRateDegreesPerSecond:F2}deg/s; Elevator-Befehl={output.ElevatorPosition:F4}; IAS-Soll={output.Curve?.TargetSpeedKnots:F2}kt; Schub-Befehl=[{string.Join(",", output.ThrottlePositionsPercent?.Select(value => value.ToString("F2", CultureInfo.InvariantCulture)) ?? [])}]%;")
            : measured + " Längs-Soll/Befehl für diesen Frame noch nicht verfügbar.";
    }

    private void StopAutoLandFlightTelemetry()
    {
        SimConnect? connection = autoLandFlightConnection;
        autoLandFlightConnection = null;
        autoLandFlightSample = null;
        autoLandLongitudinalFrame = null;
        autoLandLongitudinalOutput = null;
        autoLandAltitudeMslFeet = double.NaN;
        autoLandBankDegrees = double.NaN;
        try
        {
            if (connection != null && ReferenceEquals(connection, simconnect))
                connection.RequestDataOnSimObject((AutoLandRequestId)autoLandFlightRequest, AutoLandDefinitionId.FlightSample,
                    SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_PERIOD.NEVER,
                    SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT, 0, 0, 0);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            Debug.WriteLine($"[Debug] {DateTime.Now:yyyy-MM-dd HH:mm:ss} - [AutoLand] Flugtelemetrie abmelden: {ex.Message}");
        }
    }

    private void RegisterAutoLandDefinition(SimConnect connection)
    {
        if (ReferenceEquals(connection, autoLandDefinitionConnection))
        {
            if (!autoLandDefinitionRegistered)
                throw new InvalidOperationException(AutoLandText("Unavailable"));
            return;
        }
        autoLandDefinitionConnection = connection;
        autoLandDefinitionRegistered = false;
        connection.RegisterFacilityDataDefineStruct<AirportFields>(SIMCONNECT_FACILITY_DATA_TYPE.AIRPORT);
        connection.RegisterFacilityDataDefineStruct<AirportRunwayFields>(SIMCONNECT_FACILITY_DATA_TYPE.RUNWAY);
        string[] fields =
        [
            "OPEN AIRPORT", "NAME", "LATITUDE", "LONGITUDE", "ALTITUDE", "MAGVAR", "N_RUNWAYS",
            "OPEN RUNWAY", "LATITUDE", "LONGITUDE", "ALTITUDE", "HEADING", "LENGTH", "WIDTH",
            "PRIMARY_NUMBER", "PRIMARY_DESIGNATOR", "SECONDARY_NUMBER", "SECONDARY_DESIGNATOR",
            "SURFACE", "CLOSE RUNWAY", "CLOSE AIRPORT"
        ];
        foreach (string field in fields)
        {
            connection.AddToFacilityDefinition(AutoLandDefinitionId.Airport, field);
            TrackAutoLandPacket(connection, 0);
        }
        (string Name, string Unit)[] flightFields =
        [
            ("PLANE LATITUDE", "degrees"), ("PLANE LONGITUDE", "degrees"),
            ("PLANE HEADING DEGREES TRUE", "degrees"), ("PLANE HEADING DEGREES MAGNETIC", "degrees"),
            ("GPS GROUND TRUE TRACK", "degrees"), ("AIRSPEED INDICATED", "knots"),
            ("INDICATED ALTITUDE", "feet"), ("PLANE ALT ABOVE GROUND", "feet"),
            ("PLANE ALTITUDE", "feet"), ("PLANE BANK DEGREES", "degrees"),
            ("GROUND VELOCITY", "meters per second"), ("SIM ON GROUND", "bool"),
            ("RUDDER POSITION", "percent over 100"), ("VERTICAL SPEED", "feet per second"),
            ("ELEVATOR POSITION", "percent over 100"), ("NUMBER OF ENGINES", "number"),
            ("GENERAL ENG THROTTLE LEVER POSITION:1", "percent"),
            ("GENERAL ENG THROTTLE LEVER POSITION:2", "percent"),
            ("GENERAL ENG THROTTLE LEVER POSITION:3", "percent"),
            ("GENERAL ENG THROTTLE LEVER POSITION:4", "percent"),
            ("PLANE PITCH DEGREES", "degrees"), ("ABSOLUTE TIME", "seconds"), ("AIRSPEED TRUE", "knots"),
            ("TOTAL WEIGHT", "pounds")
        ];
        foreach (var field in flightFields)
            connection.AddToDataDefinition(AutoLandDefinitionId.FlightSample, field.Name, field.Unit,
                SIMCONNECT_DATATYPE.FLOAT64, 0, SimConnect.SIMCONNECT_UNUSED);
        connection.RegisterDataDefineStruct<AutoLandFlightData>(AutoLandDefinitionId.FlightSample);
        connection.AddToDataDefinition(AutoLandDefinitionId.AileronPosition, "AILERON POSITION", "percent over 100",
            SIMCONNECT_DATATYPE.FLOAT64, 0, SimConnect.SIMCONNECT_UNUSED);
        connection.RegisterDataDefineStruct<double>(AutoLandDefinitionId.AileronPosition);
        connection.AddToDataDefinition(AutoLandDefinitionId.RudderPosition, "RUDDER POSITION", "percent over 100",
            SIMCONNECT_DATATYPE.FLOAT64, 0, SimConnect.SIMCONNECT_UNUSED);
        connection.RegisterDataDefineStruct<double>(AutoLandDefinitionId.RudderPosition);
        connection.AddToDataDefinition(AutoLandDefinitionId.ElevatorPosition, "ELEVATOR POSITION", "percent over 100",
            SIMCONNECT_DATATYPE.FLOAT64, 0, SimConnect.SIMCONNECT_UNUSED);
        connection.RegisterDataDefineStruct<double>(AutoLandDefinitionId.ElevatorPosition);
        for (int engine = 0; engine < 4; engine++)
        {
            AutoLandDefinitionId definition = (AutoLandDefinitionId)((uint)AutoLandDefinitionId.Throttle1 + engine);
            connection.AddToDataDefinition(definition, $"GENERAL ENG THROTTLE LEVER POSITION:{engine + 1}", "percent",
                SIMCONNECT_DATATYPE.FLOAT64, 0, SimConnect.SIMCONNECT_UNUSED);
            connection.RegisterDataDefineStruct<double>(definition);
        }
        connection.MapClientEventToSimEvent(AutoLandEventId.LeftBrake, "AXIS_LEFT_BRAKE_SET");
        connection.MapClientEventToSimEvent(AutoLandEventId.RightBrake, "AXIS_RIGHT_BRAKE_SET");
        connection.MapClientEventToSimEvent(AutoLandEventId.FlapsStageOne, "FLAPS_1");
        connection.MapClientEventToSimEvent(AutoLandEventId.FlapsRetract, "FLAPS_UP");
        connection.MapClientEventToSimEvent(AutoLandEventId.GearDown, "GEAR_DOWN");
        connection.MapClientEventToSimEvent(AutoLandEventId.FlapsStageTwo, "FLAPS_2");
        connection.MapClientEventToSimEvent(AutoLandEventId.FlapsFull, "FLAPS_DOWN");
        autoLandDefinitionRegistered = true;
    }

    private bool AcceptAutoLandFacility(SimConnect sender) =>
        AutoLandSessionActive(autoLandState.Session) && autoLandState.Phase == AutoLandPhase.Planning &&
        ReferenceEquals(sender, autoLandConnection) && ReferenceEquals(sender, simconnect);

    private async Task<IReadOnlyList<AutoLandFacility>> RequestNearbyAutoLandAirportsAsync(
        SimConnect connection, double radiusNauticalMiles, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!ReferenceEquals(connection, simconnect) || !autoLandDefinitionRegistered)
            throw new InvalidOperationException(AutoLandText("Unavailable"));
        // Vor jedem Versuch die aktuelle Position lesen, während der Antwortpakete konstant halten.
        if (!TryGetWaypointTelemetry(Flight_Data.PLANE_LATITUDE.Value, out double latitude) ||
            !TryGetWaypointTelemetry(Flight_Data.PLANE_LONGITUDE.Value, out double longitude) ||
            !ValidAirportPosition(latitude, longitude, 0.0))
            throw new InvalidOperationException(AutoLandText("PositionUnavailable"));
        autoLandSearchOrigin = new(latitude, longitude);
        autoLandSearchRadiusNauticalMiles = radiusNauticalMiles;
        autoLandAirportList.Clear();
        autoLandListPackets.Clear();
        autoLandListPacketCount = null;
        autoLandListReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        uint request = autoLandListRequest = NextAutoLandRequest();
        LogAutoLandStatus(string.Format(CultureInfo.CurrentCulture, AutoLandText("SearchingNearby"), radiusNauticalMiles));
        try
        {
            // RequestFacilitiesList hat keinen Radiusparameter: bereits in den Listenpaketen filtern,
            // bevor UI-Zeilen oder Detailanfragen für weit entfernte Flughäfen entstehen.
            connection.RequestFacilitiesList(SIMCONNECT_FACILITY_LIST_TYPE.AIRPORT, (AutoLandRequestId)request);
            TrackAutoLandPacket(connection, request);
            await autoLandListReady.Task.WaitAsync(TimeSpan.FromSeconds(15), token);
            token.ThrowIfCancellationRequested();
            return [.. autoLandAirportList.Values];
        }
        finally
        {
            ForgetAutoLandPackets(request);
        }
    }

    private void ReceiveAutoLandAirportList(SimConnect sender, SIMCONNECT_RECV_AIRPORT_LIST data)
    {
        if (!AcceptAutoLandFacility(sender) || data.dwRequestID != autoLandListRequest ||
            autoLandListReady?.Task.IsCompleted != false)
            return;
        if (data.dwOutOf == 0 && data.dwArraySize == 0)
        {
            autoLandListReady.TrySetResult(true);
            return;
        }
        if (data.dwOutOf == 0 || data.dwEntryNumber >= data.dwOutOf ||
            (autoLandListPacketCount.HasValue && autoLandListPacketCount.Value != data.dwOutOf) ||
            data.rgData == null || data.dwArraySize != (uint)data.rgData.Length)
        {
            autoLandListReady.TrySetException(new InvalidOperationException(AutoLandText("Unavailable")));
            return;
        }
        autoLandListPacketCount = data.dwOutOf;
        if (!autoLandListPackets.Add(data.dwEntryNumber))
            return;
        foreach (object entry in data.rgData)
        {
            if (entry is SIMCONNECT_DATA_FACILITY_AIRPORT airport &&
                !string.IsNullOrWhiteSpace(airport.Ident) &&
                ValidAirportPosition(airport.Latitude, airport.Longitude, airport.Altitude) &&
                autoLandSearchOrigin is { } origin &&
                AutoLandAirportSearch.IsWithinRadius(origin, new(airport.Latitude, airport.Longitude),
                    autoLandSearchRadiusNauticalMiles))
            {
                string region = airport.Region ?? string.Empty;
                double distance = AutoLand.DistanceMeters(origin, new(airport.Latitude, airport.Longitude));
                autoLandAirportList.TryAdd(airport.Ident + ":" + region, new(airport.Ident, region)
                {
                    DistanceNauticalMiles = distance / AutoLandAirportSearch.MetersPerNauticalMile
                });
            }
        }
        if ((uint)autoLandListPackets.Count == data.dwOutOf)
        {
            autoLandListReady.TrySetResult(true);
        }
    }

    private async Task<AutoLandPlan?> ChooseAutoLandAirportAsync(SimConnect connection, CancellationToken token)
    {
        using Form dialog = new()
        {
            Text = AutoLandText("Choose"), StartPosition = FormStartPosition.CenterParent,
            MinimumSize = new Size(650, 340), Size = new Size(780, 470),
            MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false
        };
        autoLandDialog = dialog;
        TaskCompletionSource<AutoLandPlan?> selection = new(TaskCreationOptions.RunContinuationsAsynchronously);
        autoLandSelection = selection;
        using TableLayoutPanel layout = new()
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(8)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        using ListView choices = new()
        {
            View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false,
            Dock = DockStyle.Fill,
            AccessibleName = AutoLandText("Choose"), AccessibleDescription = AutoLandText("AirportListDescription"),
            TabIndex = 0
        };
        autoLandChoices = choices;
        choices.Columns.Add(AutoLandText("Icao"), 170);
        choices.Columns.Add(AutoLandText("Name"), 370);
        choices.Columns.Add(AutoLandText("Distance"), 130, HorizontalAlignment.Right);
        choices.Columns.Add(AutoLandText("LongestRunwayLength"), 140, HorizontalAlignment.Right);
        choices.SizeChanged += (_, _) =>
        {
            int availableWidth = Math.Max(0, choices.ClientSize.Width - SystemInformation.VerticalScrollBarWidth);
            choices.Columns[0].Width = availableWidth / 6;
            choices.Columns[2].Width = availableWidth / 5;
            choices.Columns[3].Width = availableWidth / 5;
            choices.Columns[1].Width = availableWidth - choices.Columns[0].Width - choices.Columns[2].Width - choices.Columns[3].Width;
        };
        using FlowLayoutPanel buttons = new()
        {
            AutoSize = true, FlowDirection = FlowDirection.RightToLeft,
            Anchor = AnchorStyles.Right, TabIndex = 1
        };
        using Button cancel = new()
        {
            Text = AutoLandText("Cancel"), AutoSize = true, TabIndex = 1,
            AccessibleName = AutoLandText("Cancel"), AccessibleDescription = AutoLandText("Abort")
        };
        using Button ok = new()
        {
            Text = AutoLandText("Ok"), AutoSize = true, TabIndex = 0, Enabled = false,
            AccessibleName = AutoLandText("Ok"), AccessibleDescription = AutoLandText("Choose")
        };
        choices.SelectedIndexChanged += (_, _) => ok.Enabled = choices.SelectedItems.Count == 1;
        cancel.Click += (_, _) => dialog.Close();
        ok.Click += (_, _) =>
        {
            try
            {
                if (choices.SelectedItems.Count != 1 ||
                    choices.SelectedItems[0].Tag is not AutoLandChoice choice)
                    return;
                if (choice.Airport.Runways.Count == 0)
                {
                    LogAutoLandStatus(AutoLandText("RunwayRequiredForPlan"));
                    return;
                }
                if (!TryGetWaypointTelemetry(Flight_Model.IS_GEAR_FLOATS.Value, out double floats) ||
                    floats is not (0.0 or 1.0) || autoLandWaterLanding != (floats == 1.0) ||
                    AutoLand.GetLandingCandidates([choice.Airport], floats == 1.0,
                        AutoLand.IsHelicopter(Flight_Model.CATEGORY.Value as string)).Count == 0 ||
                    !TryGetWaypointTelemetry(Flight_Data.PLANE_LATITUDE.Value, out double latitude) ||
                    !TryGetWaypointTelemetry(Flight_Data.PLANE_LONGITUDE.Value, out double longitude) ||
                    !TryGetWaypointTelemetry(Flight_Model.DESIGN_SPEED_CLIMB.Value, out double climb) ||
                    !TryGetWaypointTelemetry(Flight_Model.DESIGN_SPEED_VC.Value, out double cruise))
                {
                    LogAutoLandStatus(AutoLandText("SelectionRequired"));
                    return;
                }
                AutoLandPlan plan = AutoLand.CreatePlan(new(choice.Airport,
                    new(latitude, longitude), climb, cruise, floats == 1.0));
                selection.TrySetResult(plan);
                dialog.Close();
            }
            catch (ArgumentException ex)
            {
                Debug.WriteLine($"[Debug] {DateTime.Now:yyyy-MM-dd HH:mm:ss} - [AutoLand] Invalid planning data: {ex}");
            }
        };
        dialog.FormClosed += (_, _) => selection.TrySetResult(null);
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        layout.Controls.Add(choices, 0, 0);
        layout.Controls.Add(buttons, 0, 1);
        dialog.Controls.Add(layout);
        dialog.AcceptButton = ok;
        dialog.CancelButton = cancel;
        // Eigene IDs je Dialog: verspätetes Cleanup darf keine neue Sitzung beeinflussen.
        AutoLandRequestId pauseStateEvent = (AutoLandRequestId)NextAutoLandRequest();
        AutoLandRequestId pauseSetEvent = (AutoLandRequestId)NextAutoLandRequest();
        TaskCompletionSource<bool> pauseState = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> pauseApplied = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool pauseRequested = false;
        bool pauseReleased = false;
        bool pauseSubscribed = false;
        void receivePause(SimConnect sender, SIMCONNECT_RECV_EVENT data)
        {
            if (ReferenceEquals(sender, connection) && data.uEventID == (uint)pauseStateEvent)
            {
                pauseState.TrySetResult(data.dwData != 0);
                if (pauseRequested && data.dwData != 0)
                    pauseApplied.TrySetResult(true);
            }
        }
        void releasePause()
        {
            if (pauseReleased) return;
            pauseReleased = true;
            connection.OnRecvEvent -= receivePause;
            try
            {
                if (!ReferenceEquals(connection, simconnect))
                {
                    if (pauseRequested)
                        LogAutoLandStatus("Dialogpause: Verbindung verloren; Wiederherstellung nicht bestätigt.");
                    return;
                }
                // Nur eine von diesem Dialog gesetzte Pause aufheben; vorherige Pause bleibt bestehen.
                if (pauseRequested)
                {
                    connection.TransmitClientEvent(SimConnect.SIMCONNECT_OBJECT_ID_USER, pauseSetEvent,
                        0u, (AutoLandRequestId)0, SIMCONNECT_EVENT_FLAG.GROUPID_IS_PRIORITY);
                    LogAutoLandStatus("Dialogpause: vorherigen laufenden Zustand angefordert.");
                }
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException)
            {
                LogAutoLandStatus($"Dialogpause: Wiederherstellung fehlgeschlagen: {ex.Message}");
            }
            finally
            {
                if (pauseSubscribed && ReferenceEquals(connection, simconnect))
                {
                    try { connection.UnsubscribeFromSystemEvent(pauseStateEvent); }
                    catch (Exception ex) when (ex is COMException or InvalidOperationException)
                    { LogAutoLandStatus($"Dialogpause: Abmeldung fehlgeschlagen: {ex.Message}"); }
                }
            }
        }
        dialog.FormClosed += (_, _) => releasePause();
        try
        {
            connection.OnRecvEvent += receivePause;
            // Pause liefert bei der Anmeldung den aktuellen Zustand; niemals per Toggle raten.
            connection.SubscribeToSystemEvent(pauseStateEvent, "Pause");
            pauseSubscribed = true;
            bool wasPaused = await pauseState.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(connection, simconnect))
                throw new InvalidOperationException(AutoLandText("Unavailable"));
            if (!wasPaused)
            {
                connection.MapClientEventToSimEvent(pauseSetEvent, "PAUSE_SET");
                pauseRequested = true;
                connection.TransmitClientEvent(SimConnect.SIMCONNECT_OBJECT_ID_USER, pauseSetEvent,
                    1u, (AutoLandRequestId)0, SIMCONNECT_EVENT_FLAG.GROUPID_IS_PRIORITY);
                // Den Dialog erst nach bestätigter Pause zeigen, auch bei verzögerter SimConnect-Antwort.
                await pauseApplied.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            }
            token.ThrowIfCancellationRequested();
            LogAutoLandStatus($"Dialogpause: vorher pausiert={wasPaused}.");
            // Nicht modal: der AutoLand-Abbruchbutton des Hauptfensters bleibt bedienbar.
            dialog.Show(this);
            // Nie den gesamten Simulator-Katalog synchron an SimConnect senden.
            // Zwischen kleinen Abrufgruppen muss die Nachrichtenpumpe Antworten verarbeiten können.
            autoLandLoadQueue = new(autoLandAirportList.Values.OrderBy(facility => facility.DistanceNauticalMiles),
                maximumPending: 4, timeout: TimeSpan.FromSeconds(15));
            bool? previousHelicopter = null;
            while (!selection.Task.IsCompleted)
            {
                token.ThrowIfCancellationRequested();
                if (!ReferenceEquals(connection, simconnect) || !autoLandDefinitionRegistered)
                    throw new InvalidOperationException(AutoLandText("Unavailable"));
                foreach (AutoLandFacility expired in autoLandLoadQueue.Expire(DateTimeOffset.UtcNow))
                {
                    expired.Invalid = true;
                    FinishAutoLandRequest(expired.Request, expired);
                    LogAutoLandStatus($"Facility timeout: airport={expired.Icao}, request={expired.Request}");
                }
                bool? waterLanding = TryGetWaypointTelemetry(Flight_Model.IS_GEAR_FLOATS.Value, out double floats) &&
                    floats is 0.0 or 1.0 ? floats == 1.0 : null;
                bool helicopter = AutoLand.IsHelicopter(Flight_Model.CATEGORY.Value as string);
                if (autoLandWaterLanding != waterLanding || previousHelicopter != helicopter)
                {
                    autoLandWaterLanding = waterLanding;
                    previousHelicopter = helicopter;
                    RefreshAutoLandChoices();
                }
                foreach (AutoLandFacility facility in autoLandLoadQueue.TakeReady(DateTimeOffset.UtcNow))
                {
                    uint request = NextAutoLandRequest();
                    facility.Request = request;
                    autoLandFacilities.Add(request, facility);
                    try
                    {
                        connection.RequestFacilityData(AutoLandDefinitionId.Airport,
                            (AutoLandRequestId)request, facility.Icao, facility.Region);
                        TrackAutoLandPacket(connection, request);
                    }
                    catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException)
                    {
                        facility.Invalid = true;
                        FinishAutoLandRequest(request, facility);
                        ReportAutoLandFailure(ex);
                    }
                }
                await Task.WhenAny(selection.Task, Task.Delay(100, token));
            }
            return await selection.Task.WaitAsync(token);
        }
        finally
        {
            dialog.Close();
            releasePause();
            if (ReferenceEquals(autoLandDialog, dialog))
            {
                autoLandDialog = null;
                autoLandChoices = null;
                autoLandWaterLanding = null;
                autoLandSelection = null;
            }
        }
    }

    private void ReceiveAutoLandFacility(SimConnect sender, SIMCONNECT_RECV_FACILITY_DATA data)
    {
        if (!AcceptAutoLandFacility(sender) || !autoLandFacilities.TryGetValue(data.UserRequestId, out AutoLandFacility? facility) || facility.Complete)
            return;
        if (data.Data is not { Length: 1 })
        {
            facility.Invalid = true;
            LogAutoLandStatus($"Unexpected facility payload: airport={facility.Icao}, type={data.Type}, count={data.Data?.Length}");
            return;
        }
        if (data.Type == (uint)SIMCONNECT_FACILITY_DATA_TYPE.AIRPORT && data.Data[0] is AirportFields airport)
        {
            if (facility.Airport.HasValue || data.IsListItem != 0 || airport.RunwayCount < 0 ||
                !ValidAirportPosition(airport.Latitude, airport.Longitude, airport.AltitudeMeters))
                facility.Invalid = true;
            facility.Airport = airport;
            facility.AirportUniqueId = data.UniqueRequestId;
        }
        else if (data.Type == (uint)SIMCONNECT_FACILITY_DATA_TYPE.RUNWAY && data.Data[0] is AirportRunwayFields runway)
        {
            if (data.IsListItem != 1 || data.ItemIndex >= data.ListSize || !ValidAirportRunway(runway) ||
                !facility.Runways.TryAdd(data.ItemIndex, new(data.UniqueRequestId, data.ParentUniqueRequestId, data.ListSize, runway)))
                facility.Invalid = true;
        }
        else
        {
            facility.Invalid = true;
            LogAutoLandStatus($"Unexpected facility type: airport={facility.Icao}, type={data.Type}");
        }
    }

    private void ReceiveAutoLandFacilityEnd(SimConnect sender, SIMCONNECT_RECV_FACILITY_DATA_END data)
    {
        if (!AcceptAutoLandFacility(sender) || !autoLandFacilities.TryGetValue(data.RequestId, out AutoLandFacility? facility) || facility.Complete)
            return;
        if (facility.Airport is not { } airport || string.IsNullOrWhiteSpace(airport.Name) ||
            facility.Runways.Count != airport.RunwayCount ||
            facility.Runways.Any(pair => pair.Key >= (uint)airport.RunwayCount ||
                pair.Value.ListSize != (uint)airport.RunwayCount ||
                pair.Value.ParentId != facility.AirportUniqueId || pair.Value.UniqueId == facility.AirportUniqueId) ||
            facility.Runways.Values.Select(item => item.UniqueId).Distinct().Count() != facility.Runways.Count)
            facility.Invalid = true;
        FinishAutoLandRequest(data.RequestId, facility);
    }

    private void ForgetAutoLandPackets(uint request)
    {
        foreach (uint packet in autoLandSentPackets.Where(pair => pair.Value == request).Select(pair => pair.Key).ToArray())
            autoLandSentPackets.Remove(packet);
    }

    private void FinishAutoLandRequest(uint request, AutoLandFacility facility)
    {
        facility.Complete = true;
        autoLandFacilities.Remove(request);
        autoLandLoadQueue?.Complete(facility);
        ForgetAutoLandPackets(request);
        RefreshAutoLandChoices();
    }

    private void RefreshAutoLandChoices()
    {
        if (autoLandChoices is not { IsDisposed: false } choices)
            return;
        List<AutoLandChoice> candidates = [];
        if (autoLandWaterLanding is bool waterLanding)
        {
            foreach (AutoLandFacility facility in autoLandAirportList.Values)
            {
                if (!facility.Complete || facility.Invalid || facility.Airport is not { } airport)
                    continue;
                AutoLandRunway[] runways = [.. facility.Runways.OrderBy(pair => pair.Key).Select(pair =>
                {
                    AirportRunwayFields runway = pair.Value.Fields;
                    return new AutoLandRunway(AirportRunwayIdentifier(runway.PrimaryNumber, runway.PrimaryDesignator),
                        AirportRunwayIdentifier(runway.SecondaryNumber, runway.SecondaryDesignator),
                        new(runway.Latitude, runway.Longitude), runway.HeadingTrueDegrees,
                        runway.LengthMeters, runway.WidthMeters, runway.AltitudeMeters, runway.SurfaceType);
                })];
                AutoLandAirport candidate = new(facility.Icao, airport.Name,
                    new(airport.Latitude, airport.Longitude), Array.AsReadOnly(runways));
                foreach (AutoLandAirport usable in AutoLand.GetLandingCandidates([candidate], waterLanding,
                    AutoLand.IsHelicopter(Flight_Model.CATEGORY.Value as string)))
                    candidates.Add(new(facility, usable));
            }
        }
        AutoLandChoice[] orderedCandidates = [.. candidates.OrderBy(item => item.Facility.DistanceNauticalMiles)
            .ThenBy(item => item.Airport.Icao, StringComparer.Ordinal)];
        HashSet<AutoLandFacility> candidateFacilities = [.. orderedCandidates.Select(item => item.Facility)];
        Dictionary<AutoLandFacility, ListViewItem> existingRows = choices.Items.Cast<ListViewItem>()
            .ToDictionary(row => ((AutoLandChoice)row.Tag!).Facility);
        choices.BeginUpdate();
        try
        {
            // Bestehende Zeilen behalten Auswahl und Fokus auch während weiterer Antworten.
            foreach ((AutoLandFacility facility, ListViewItem row) in existingRows)
                if (!candidateFacilities.Contains(facility))
                    choices.Items.Remove(row);
            int index = 0;
            foreach (AutoLandChoice candidate in orderedCandidates)
            {
                if (existingRows.TryGetValue(candidate.Facility, out ListViewItem? row))
                    row.Tag = candidate;
                else
                {
                    row = new([candidate.Airport.Icao, candidate.Airport.Name,
                        candidate.Facility.DistanceNauticalMiles.ToString("N1", CultureInfo.CurrentCulture),
                        candidate.Airport.Runways.Count == 0 ? string.Empty :
                            candidate.Airport.Runways.Max(runway => runway.LengthMeters).ToString("N0", CultureInfo.CurrentCulture)]) { Tag = candidate };
                    choices.Items.Insert(index, row);
                }
                index++;
            }
        }
        finally
        {
            choices.EndUpdate();
        }
    }

    private void ReceiveAutoLandException(SimConnect sender, SIMCONNECT_RECV_EXCEPTION data)
    {
        if (!AcceptAutoLandFacility(sender) || !autoLandSentPackets.Remove(data.dwSendID, out uint request))
            return;
        if (request == 0)
        {
            autoLandDefinitionRegistered = false;
            autoLandListReady?.TrySetException(new InvalidOperationException(AutoLandText("Unavailable")));
        }
        if (request == autoLandListRequest)
            autoLandListReady?.TrySetException(new InvalidOperationException(AutoLandText("Unavailable")));
        if (autoLandFacilities.TryGetValue(request, out AutoLandFacility? facility))
        {
            facility.Invalid = true;
            FinishAutoLandRequest(request, facility);
        }
        LogAutoLandStatus(AutoLandText("Unavailable"));
        Debug.WriteLine($"[Debug] {DateTime.Now:yyyy-MM-dd HH:mm:ss} - [AutoLand] SimConnect error={data.dwException}, request={request}");
    }

    private void ReportAutoLandFailure(Exception exception)
    {
        string text = string.Format(CultureInfo.CurrentCulture, AutoLandText("Failure"), exception.Message);
        LogAutoLandStatus(text);
        Debug.WriteLine($"[Debug] {DateTime.Now:yyyy-MM-dd HH:mm:ss} - [AutoLand] {exception}");
    }

    private void DetachAutoLandFacilities()
    {
        if (autoLandConnection is { } connection)
        {
            connection.OnRecvAirportList -= ReceiveAutoLandAirportList;
            connection.OnRecvFacilityData -= ReceiveAutoLandFacility;
            connection.OnRecvFacilityDataEnd -= ReceiveAutoLandFacilityEnd;
            connection.OnRecvException -= ReceiveAutoLandException;
        }
        autoLandConnection = null;
        autoLandListRequest = 0;
        autoLandListPacketCount = null;
        autoLandListPackets.Clear();
        autoLandSentPackets.Clear();
        autoLandFacilities.Clear();
        autoLandAirportList.Clear();
        autoLandLoadQueue = null;
        autoLandSearchOrigin = null;
        autoLandSearchRadiusNauticalMiles = 0.0;
    }

    private void ReleaseAutoLandRudder()
    {
        try
        {
            autoLandRudderConnection?.SetDataOnSimObject(AutoLandDefinitionId.RudderPosition,
                SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_DATA_SET_FLAG.DEFAULT, 0.0);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        { LogAutoLandStatus($"Seitenruderfreigabe: {ex.Message}"); }
        finally { autoLandRudderConnection = null; autoLandRollout = new(); }
    }

    private long StopAutoLand()
    {
        long session = autoLandState.Session;
        ReleaseAutoLandRudder();
        ReleaseAutoLandAxis();
        ReleaseAutoLandElevator();
        if (autoLandState.Phase == AutoLandPhase.Aligning)
            DisableAutoLandAutopilot();
        autoLandPastP4 = false;
        autoLandLanding = new();
        autoLandDirectActive = false;
        autoLandDirect = new();
        StopAutoLandFlightTelemetry();
        if (autoLandState.Phase == AutoLandPhase.Aligning)
            LogAutoLandStatus(FormattableString.Invariant($"Ausrichtung Ende: Dauer={(DateTime.UtcNow - autoLandAlignmentStartedUtc).TotalSeconds:F1}s; Samples={autoLandAlignment.Samples}; maxKurvenabstand={autoLandAlignment.MaximumCrossTrackMeters:F1}m; maxALTFehler={autoLandAlignment.MaximumAltitudeErrorFeet:F1}ft; minPA={autoLandAlignment.MinimumDistanceToPaMeters:F1}m; maxBankbedarf={autoLandAlignment.MaximumRequiredBankDegrees:F1}deg"));
        autoLandEntry = null;
        autoLandTelemetryMissing = false;
        autoLandState = AutoLand.Update(autoLandState, new(AutoLandAction.Cancel, session));
        autoLandCancellation?.Cancel();
        autoLandCancellation?.Dispose();
        autoLandCancellation = null;
        autoLandSelection?.TrySetResult(null);
        autoLandDialog?.Close();
        DetachAutoLandFacilities();
        foreach ((ToolStripItem item, bool enabled) in autoLandPreviousEnabled)
            item.Enabled = enabled;
        autoLandPreviousEnabled.Clear();
        if (!IsDisposed && !Disposing)
            SetAutoLandButton(false);
        return session;
    }

    private async Task CancelAutoLandAsync()
    {
        if (!autoLandPastP4 && autoLandState.Phase == AutoLandPhase.Aligning)
            HoldAutoLandCurrentDirectionAndAltitude();
        long session = StopAutoLand();
        try
        {
            if (webView2.CoreWebView2 is { } core)
                await core.ExecuteScriptAsync($"window.clearAutoLandMarkers({session});");
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            Debug.WriteLine($"[Debug] {DateTime.Now:yyyy-MM-dd HH:mm:ss} - [AutoLand] Marker cleanup failed: {ex}");
            if (!IsDisposed && !Disposing && autoLandState.Session == session)
                LogAutoLandStatus(AutoLandText("CancelMapFailure"));
            return;
        }
        if (!IsDisposed && !Disposing && autoLandState.Session == session)
            LogAutoLandStatus(AutoLandText("Cancelled"));
    }

    private void AutoLandDisposed(object? sender, EventArgs e)
    {
        StopAutoLand();
        tsbAutoland.Click -= TsbAutoland_ClickAsync;
        Disposed -= AutoLandDisposed;
    }

    private void LogAutoLandPlan(AutoLandPlan plan)
    {
        static void log(string text) => Debug.WriteLine($"[Debug] {DateTime.Now:yyyy-MM-dd HH:mm:ss} - [AutoLand] {text}");
        log($"Airport={plan.Airport.Icao} {plan.Airport.Name}");
        log($"Runway={plan.Runway.PrimaryIdentifier}/{plan.Runway.SecondaryIdentifier}");
        log(FormattableString.Invariant($"AirportPosition={plan.Airport.Position.LatitudeDegrees:F7},{plan.Airport.Position.LongitudeDegrees:F7}; Suche={plan.SearchReference.LatitudeDegrees:F7},{plan.SearchReference.LongitudeDegrees:F7}"));
        log(FormattableString.Invariant($"Bahnmitte={plan.Runway.Center.LatitudeDegrees:F7},{plan.Runway.Center.LongitudeDegrees:F7}; Länge={plan.Runway.LengthMeters:F1}m; Breite={plan.Runway.WidthMeters:F1}m; Höhe={plan.Runway.AltitudeMeters:F2}m; Gegenkurs={(plan.CourseTrueDegrees + 180) % 360:F3}"));
        log(FormattableString.Invariant($"gewählt={plan.SelectedDirection}; Kurs={plan.CourseTrueDegrees:F3}"));
        foreach (AutoLandPoint point in plan.Points)
            log(FormattableString.Invariant($"{point.Label}: lat={point.Position.LatitudeDegrees:F7}, lon={point.Position.LongitudeDegrees:F7}; absolute={point.AltitudeFeet:F2}ft"));
    }
}
