using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.FlightSimulator.SimConnect;

namespace AutoFly;

internal static class ElevationSettings
{
    // true = Open-Meteo, false = Open-Elevation; gilt für beide Aufrufer.
    public static readonly bool UseOpenMeteo = true;
}

/// <summary>Fragt eine Bodenhöhe beim ausgewählten Anbieter ab; der Aufrufer verwaltet den HttpClient.</summary>
public sealed class ElevationClient(HttpClient httpClient)
{
    private readonly HttpClient httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));

    public async Task<double> GetElevationFeetAsync(double latitude, double longitude,
        CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(latitude) || latitude < -90.0 || latitude > 90.0)
            throw new ArgumentOutOfRangeException(nameof(latitude));
        if (!double.IsFinite(longitude) || longitude < -180.0 || longitude > 180.0)
            throw new ArgumentOutOfRangeException(nameof(longitude));
        bool useOpenMeteo = ElevationSettings.UseOpenMeteo;
        string provider = useOpenMeteo ? "Open-Meteo" : "Open-Elevation";
        string url = useOpenMeteo
            ? string.Create(CultureInfo.InvariantCulture,
                $"https://api.open-meteo.com/v1/elevation?latitude={latitude:R}&longitude={longitude:R}")
            : string.Create(CultureInfo.InvariantCulture,
                $"https://api.open-elevation.com/api/v1/lookup?locations={latitude:R},{longitude:R}");
        using HttpResponseMessage response = await httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using JsonDocument document = await JsonDocument.ParseAsync(stream,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException(Resources_DE.Format("Elevation_InvalidGroundHeight", provider));

        JsonElement elevation;
        if (useOpenMeteo)
        {
            if (!root.TryGetProperty("elevation", out JsonElement elevations) ||
                elevations.ValueKind != JsonValueKind.Array || elevations.GetArrayLength() != 1)
                throw new InvalidDataException(Resources_DE.Format("Elevation_InvalidGroundHeight", provider));
            elevation = elevations[0];
        }
        else
        {
            if (!root.TryGetProperty("results", out JsonElement results) ||
                results.ValueKind != JsonValueKind.Array || results.GetArrayLength() != 1 ||
                results[0].ValueKind != JsonValueKind.Object ||
                !results[0].TryGetProperty("elevation", out elevation))
                throw new InvalidDataException(Resources_DE.Format("Elevation_InvalidGroundHeight", provider));
        }

        if (elevation.ValueKind != JsonValueKind.Number ||
            !elevation.TryGetDouble(out double meters) || !double.IsFinite(meters))
            throw new InvalidDataException(Resources_DE.Format("Elevation_InvalidGroundHeight", provider));
        double feet = meters / 0.3048;
        if (!double.IsFinite(feet))
            throw new InvalidDataException(Resources_DE.GetString("Elevation_HeightConversionFailed"));
        return feet;
    }
}

/// <summary>Validiertes Versetzziel mit Geländehöhe-Zielgelände plus bisherigem Bodenabstand.</summary>
public sealed record AircraftRelocationTarget
{
    public double Latitude { get; }
    public double Longitude { get; }
    public double AltitudeFeet { get; }

    /// <summary>Berechnet die absolute Zielhöhe in Fuß ohne Ersatzwerte bei ungültigen Eingaben.</summary>
    public AircraftRelocationTarget(double latitude, double longitude, double terrainFeet, double heightAboveGroundFeet)
    {
        if (!double.IsFinite(latitude) || latitude < -90 || latitude > 90)
            throw new ArgumentOutOfRangeException(nameof(latitude));
        if (!double.IsFinite(longitude) || longitude < -180 || longitude > 180)
            throw new ArgumentOutOfRangeException(nameof(longitude));
        if (!double.IsFinite(terrainFeet))
            throw new ArgumentOutOfRangeException(nameof(terrainFeet));
        if (!double.IsFinite(heightAboveGroundFeet) || heightAboveGroundFeet <= 0)
            throw new ArgumentOutOfRangeException(nameof(heightAboveGroundFeet));
        double altitude = terrainFeet + heightAboveGroundFeet;
        if (!double.IsFinite(altitude))
            throw new ArgumentOutOfRangeException(nameof(heightAboveGroundFeet));
        Latitude = latitude;
        Longitude = longitude;
        AltitudeFeet = altitude;
    }
}

public partial class AutoflyMain
{
    private enum RelocationDefinitionId : uint { Snapshot = 11000, Position = 11001 }
    private enum RelocationRequestId : uint { Snapshot = 11000 }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct RelocationSnapshot
    {
        public double Latitude;
        public double Longitude;
        public double Altitude;
        public double HeightAboveGround;
        public double Pitch;
        public double Bank;
        public double Heading;
        public double MagneticHeading;
        public double IndicatedAltitude;
        public double IndicatedAirspeed;
        public double VerticalSpeed;
        public double TurnRate;
        public double RollTrim;
        public double PitchTrim;
        public double Throttle1;
        public double Throttle2;
        public double Throttle3;
        public double Throttle4;
        public double AileronPosition;
        public int AutopilotMaster;
        public int OnGround;
    }

    private SimConnect? relocationConnection;
    private TaskCompletionSource<RelocationSnapshot>? relocationSnapshotCompletion;
    private uint? relocationWritePacket;
    private uint? relocationReadPacket;
    private readonly HashSet<uint> relocationDefinitionPackets = [];
    private uint relocationRequestSequence = 11000;
    private uint relocationExpectedRequest;
    private Exception? relocationError;
    private bool relocationInProgress;
    private bool relocationControlsSuspended;

    private void EnsureRelocationDefinitions(SimConnect connection)
    {
        if (ReferenceEquals(relocationConnection, connection))
            return;
        relocationDefinitionPackets.Clear();
        (string Name, string Unit)[] variables =
        [
            ("PLANE LATITUDE", "degrees"), ("PLANE LONGITUDE", "degrees"),
            ("PLANE ALTITUDE", "feet"), ("PLANE ALT ABOVE GROUND", "feet"),
            ("PLANE PITCH DEGREES", "degrees"), ("PLANE BANK DEGREES", "degrees"),
            ("PLANE HEADING DEGREES TRUE", "degrees"), ("PLANE HEADING DEGREES MAGNETIC", "degrees"),
            ("INDICATED ALTITUDE", "feet"), ("AIRSPEED INDICATED", "knots"),
            ("VERTICAL SPEED", "feet per minute"), ("TURN INDICATOR RATE", "degrees per second"),
            ("AILERON TRIM PCT", "percent"), ("ELEVATOR TRIM POSITION", "radians"),
            ("GENERAL ENG THROTTLE LEVER POSITION:1", "percent"),
            ("GENERAL ENG THROTTLE LEVER POSITION:2", "percent"),
            ("GENERAL ENG THROTTLE LEVER POSITION:3", "percent"),
            ("GENERAL ENG THROTTLE LEVER POSITION:4", "percent"),
            ("AILERON POSITION", "percent")
        ];
        foreach ((string name, string unit) in variables)
        {
            connection.AddToDataDefinition(RelocationDefinitionId.Snapshot, name, unit,
                SIMCONNECT_DATATYPE.FLOAT64, 0, SimConnect.SIMCONNECT_UNUSED);
            relocationDefinitionPackets.Add(connection.GetLastSentPacketID());
        }
        connection.AddToDataDefinition(RelocationDefinitionId.Snapshot, "AUTOPILOT MASTER", "bool",
            SIMCONNECT_DATATYPE.INT32, 0, SimConnect.SIMCONNECT_UNUSED);
        relocationDefinitionPackets.Add(connection.GetLastSentPacketID());
        connection.AddToDataDefinition(RelocationDefinitionId.Snapshot, "SIM ON GROUND", "bool",
            SIMCONNECT_DATATYPE.INT32, 0, SimConnect.SIMCONNECT_UNUSED);
        relocationDefinitionPackets.Add(connection.GetLastSentPacketID());
        connection.RegisterDataDefineStruct<RelocationSnapshot>(RelocationDefinitionId.Snapshot);
        // Bei strukturierten Datentypen muss der Einheitenzeiger NULL sein, nicht der Text "NULL".
        connection.AddToDataDefinition(RelocationDefinitionId.Position, "Initial Position", null,
            SIMCONNECT_DATATYPE.INITPOSITION, 0, SimConnect.SIMCONNECT_UNUSED);
        relocationDefinitionPackets.Add(connection.GetLastSentPacketID());
        connection.RegisterDataDefineStruct<SIMCONNECT_DATA_INITPOSITION>(RelocationDefinitionId.Position);
        relocationConnection = connection;
    }

    private async Task<RelocationSnapshot> ReadRelocationSnapshotAsync(SimConnect connection, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (relocationError != null)
            throw new InvalidOperationException(relocationError.Message, relocationError);
        if (!ReferenceEquals(simconnect, connection))
            throw new InvalidOperationException(WaypointText("RelocationDisconnected"));
        TaskCompletionSource<RelocationSnapshot> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        relocationSnapshotCompletion = completion;
        try
        {
            relocationExpectedRequest = checked(++relocationRequestSequence);
            connection.RequestDataOnSimObject((RelocationRequestId)relocationExpectedRequest, RelocationDefinitionId.Snapshot,
                SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_PERIOD.ONCE, SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT,
                0, 0, 0);
            relocationReadPacket = connection.GetLastSentPacketID();
            RelocationSnapshot snapshot = await completion.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            double[] values = [snapshot.Latitude, snapshot.Longitude, snapshot.Altitude, snapshot.HeightAboveGround,
                snapshot.Pitch, snapshot.Bank, snapshot.Heading, snapshot.MagneticHeading,
                snapshot.IndicatedAltitude, snapshot.IndicatedAirspeed, snapshot.VerticalSpeed, snapshot.TurnRate,
                snapshot.RollTrim, snapshot.PitchTrim, snapshot.Throttle1, snapshot.Throttle2,
                snapshot.Throttle3, snapshot.Throttle4, snapshot.AileronPosition];
            if (values.Any(value => !double.IsFinite(value)) || Math.Abs(snapshot.Latitude) > 90 ||
                Math.Abs(snapshot.Longitude) > 180 || snapshot.IndicatedAirspeed < 0 ||
                snapshot.IndicatedAirspeed > uint.MaxValue - 3.0)
                throw new InvalidOperationException(WaypointText("RelocationInvalidTelemetry"));
            return snapshot;
        }
        finally
        {
            relocationSnapshotCompletion = null;
            relocationReadPacket = null;
        }
    }

    private bool ReceiveRelocationSnapshot(SIMCONNECT_RECV_SIMOBJECT_DATA data)
    {
        if (data.dwDefineID != (uint)RelocationDefinitionId.Snapshot)
            return false;
        if (data.dwRequestID != relocationExpectedRequest)
            return true;
        if (data.dwData.Length > 0 && data.dwData[0] is RelocationSnapshot snapshot)
            relocationSnapshotCompletion?.TrySetResult(snapshot);
        else
            relocationSnapshotCompletion?.TrySetException(new InvalidDataException(WaypointText("RelocationInvalidTelemetry")));
        return true;
    }

    private void ReceiveRelocationException(SIMCONNECT_RECV_EXCEPTION data)
    {
        if (relocationInProgress && (relocationWritePacket == data.dwSendID || relocationReadPacket == data.dwSendID ||
            relocationDefinitionPackets.Contains(data.dwSendID)))
        {
            relocationError = new InvalidOperationException(
                Resources_DE.Format("Relocation_SimConnectError", (SIMCONNECT_EXCEPTION)data.dwException, data.dwSendID, data.dwIndex));
            relocationSnapshotCompletion?.TrySetException(relocationError);
        }
    }

    private async Task<string?> RelocateAircraftAsync(double latitude, double longitude)
    {
        if (relocationInProgress)
            return WaypointText("RelocationBusy");
        relocationInProgress = true;
        relocationError = null;
        string? error = null;
        bool written = false;
        bool confirmed = false;
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(waypointElevationCancellation.Token);
        cancellation.CancelAfter(TimeSpan.FromSeconds(35));
        try
        {
            // Vor dem HTTP-Aufruf bereits Koordinaten validieren.
            _ = new AircraftRelocationTarget(latitude, longitude, 0, 1);
            SimConnect connection = simconnect ?? throw new InvalidOperationException(WaypointText("RelocationDisconnected"));
            EnsureRelocationDefinitions(connection);
            double terrain = await new ElevationClient(waypointElevationHttpClient)
                .GetElevationFeetAsync(latitude, longitude, cancellation.Token);
            RelocationSnapshot before = await ReadRelocationSnapshotAsync(connection, cancellation.Token);
            if (before.OnGround != 0 || before.HeightAboveGround <= 0)
                throw new InvalidOperationException(WaypointText("RelocationAirborneOnly"));
            AircraftRelocationTarget target = new(latitude, longitude, terrain, before.HeightAboveGround);
            double initialDistance = WaypointManagerDistance(before.Latitude, before.Longitude, latitude, longitude);
            double positionTolerance = Math.Clamp(initialDistance / 4, 1, 100);
            double altitudeTolerance = Math.Clamp(Math.Abs(before.Altitude - target.AltitudeFeet) / 4, 1, 50);
            if (initialDistance <= positionTolerance && Math.Abs(before.Altitude - target.AltitudeFeet) <= altitudeTolerance)
                throw new InvalidOperationException(WaypointText("RelocationAlreadyThere"));
            SIMCONNECT_DATA_INITPOSITION position = new()
            {
                Latitude = target.Latitude, Longitude = target.Longitude, Altitude = target.AltitudeFeet,
                Pitch = before.Pitch, Bank = before.Bank, Heading = before.Heading,
                // MSFS 2024 behandelt KEEP beim Schreiben hier als numerische Airspeed.
                // INITPOSITION erlaubt nur ganze Knoten: aktuellen Messwert auf den nächsten Knoten runden.
                OnGround = 0, Airspeed = checked((uint)Math.Round(before.IndicatedAirspeed))
            };
            relocationControlsSuspended = true;
            autopilot?.ResetAfterRelocation();
            connection.SetDataOnSimObject(RelocationDefinitionId.Position, SimConnect.SIMCONNECT_OBJECT_ID_USER,
                SIMCONNECT_DATA_SET_FLAG.DEFAULT, position);
            written = true;
            relocationWritePacket = connection.GetLastSentPacketID();
            RelocationSnapshot after;
            do
            {
                await Task.Delay(250, cancellation.Token);
                after = await ReadRelocationSnapshotAsync(connection, cancellation.Token);
            }
            while (after.OnGround != 0 ||
                WaypointManagerDistance(after.Latitude, after.Longitude, latitude, longitude) > positionTolerance ||
                Math.Abs(after.Altitude - target.AltitudeFeet) > altitudeTolerance ||
                Math.Abs(after.IndicatedAirspeed - before.IndicatedAirspeed) > 2 ||
                Math.Abs(after.Pitch - before.Pitch) > 2 || Math.Abs(after.Bank - before.Bank) > 2 ||
                Math.Abs(Math.IEEERemainder(after.Heading - before.Heading, 360)) > 2);
            if (relocationError != null)
                throw new InvalidOperationException(relocationError.Message, relocationError);
            confirmed = true;
            Flight_Data.PLANE_LATITUDE.Value = after.Latitude;
            Flight_Data.PLANE_LONGITUDE.Value = after.Longitude;
            Flight_Data.PLANE_ALT_ABOVE_GROUND.Value = after.HeightAboveGround;
            Flight_Data.INDICATED_ALTITUDE.Value = after.IndicatedAltitude;
            Flight_Data.PLANE_HEADING_DEGREES_TRUE.Value = after.Heading;
            Flight_Data.PLANE_HEADING_DEGREES_MAGNETIC.Value = after.MagneticHeading;
            Flight_Data.PLANE_BANK_DEGREES.Value = after.Bank;
            Flight_Data.AIRSPEED_INDICATED.Value = after.IndicatedAirspeed;
            Flight_Data.VERTICAL_SPEED.Value = after.VerticalSpeed;
            Flight_Data.TURN_INDICATOR_RATE.Value = after.TurnRate;
            Flight_Data.AILERON_TRIM_PCT.Value = after.RollTrim;
            Flight_Data.ELEVATOR_TRIM_POSITION.Value = after.PitchTrim;
            Flight_Data.GENERAL_ENG_THROTTLE_LEVER_POSITION_1.Value = after.Throttle1;
            Flight_Data.GENERAL_ENG_THROTTLE_LEVER_POSITION_2.Value = after.Throttle2;
            Flight_Data.GENERAL_ENG_THROTTLE_LEVER_POSITION_3.Value = after.Throttle3;
            Flight_Data.GENERAL_ENG_THROTTLE_LEVER_POSITION_4.Value = after.Throttle4;
            Flight_Data.AILERON_POSITION.Value = after.AileronPosition;
            Flight_Data.AUTOPILOT_MASTER.Value = after.AutopilotMaster;
            if (!_isWayPointFollow)
            {
                _targetAltitude = RoundTargetAltitude(after.IndicatedAltitude);
                autopilot?.localTargetAltDeg = _targetAltitude;
                tstbAltitude.Text = _targetAltitude.ToString("F0", CultureInfo.InvariantCulture);
            }
            _lastSentLat = null;
            _lastSentLon = null;
            if (webView2.CoreWebView2 is { } core)
                await core.ExecuteScriptAsync("window.clearTrail();");
            Debug.WriteLine($"[Debug] {DateTime.Now:yyyy-MM-dd HH:mm:ss} - [Relocation] {latitude:F6}, {longitude:F6}, ALT={target.AltitudeFeet:F1} ft, AGL={before.HeightAboveGround:F1} ft (Open-Elevation).");
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or JsonException or
            ArgumentException or InvalidOperationException or COMException or OperationCanceledException or TimeoutException)
        {
            Debug.WriteLine($"[Debug] {DateTime.Now:yyyy-MM-dd HH:mm:ss} - [Relocation] {ex}");
            error = ex is OperationCanceledException or TimeoutException
                ? WaypointText("RelocationTimeout") : ex.Message;
            if (written && !confirmed)
                error += Environment.NewLine + WaypointText("RelocationSafetyOff");
        }
        finally
        {
            relocationWritePacket = null;
            try
            {
                if (!IsDisposed && !Disposing && relocationControlsSuspended)
                {
                    if (written && !confirmed)
                    {
                        SetWaypointFollowing(false);
                        _isLocalAutopilotActive = false;
                        tsbAP.BackColor = System.Drawing.Color.LightGray;
                        autopilot?.AutopilotLocalOnOff(false);
                    }
                    autopilot?.ResetAfterRelocation();
                }
            }
            catch (COMException ex)
            {
                Debug.WriteLine($"[Debug] {DateTime.Now:yyyy-MM-dd HH:mm:ss} - [Relocation] Reset: {ex}");
                SetWaypointFollowing(false);
                _isLocalAutopilotActive = false;
                tsbAP.BackColor = System.Drawing.Color.LightGray;
                autopilot = null;
                error = WaypointText("RelocationSafetyOff");
            }
            finally
            {
                relocationControlsSuspended = false;
                relocationInProgress = false;
            }
        }
        // Erst nach der Sicherheitsbereinigung meldet die Webbrücke das Ergebnis.
        return error;
    }

    private static double WaypointManagerDistance(double latitude, double longitude, double targetLatitude, double targetLongitude)
    {
        double lat = (targetLatitude - latitude) * Math.PI / 180;
        double lon = (targetLongitude - longitude) * Math.PI / 180;
        double a = Math.Pow(Math.Sin(lat / 2), 2) + Math.Cos(latitude * Math.PI / 180) *
            Math.Cos(targetLatitude * Math.PI / 180) * Math.Pow(Math.Sin(lon / 2), 2);
        return 6371000 * 2 * Math.Asin(Math.Sqrt(Math.Clamp(a, 0, 1)));
    }
}
