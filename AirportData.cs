using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.FlightSimulator.SimConnect;

namespace AutoFly;

public partial class AutoflyMain
{
    private enum AirportDefinitionId : uint { Airport = 13000 }
    private enum AirportRequestId : uint { First = 0xB0000000 }
    private enum AirportStage { Inactive, Position, AirportList, AirportData, Complete, Failed }
    private enum AirportRegistration { None, Pending, Registered, Failed }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
    private struct AirportFields
    {
        // SimConnect liefert UTF-8-Bytes, keine Zeichenfolge in der Windows-ANSI-Codepage.
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32, ArraySubType = UnmanagedType.U1)]
        public byte[] NameBytes;
        public double Latitude;
        public double Longitude;
        public double AltitudeMeters;
        public float MagVarDegrees;
        public int RunwayCount;

        public readonly string Name => SimConnectText.DecodeUtf8(NameBytes);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct AirportRunwayFields
    {
        public double Latitude;
        public double Longitude;
        public double AltitudeMeters;
        public float HeadingTrueDegrees;
        public float LengthMeters;
        public float WidthMeters;
        public int PrimaryNumber;
        public int PrimaryDesignator;
        public int SecondaryNumber;
        public int SecondaryDesignator;
        public int SurfaceType;
    }

    private sealed record AirportRunwayItem(uint UniqueId, uint ParentId, uint ListSize,
        AirportRunwayFields Fields);

    private SimConnect? airportConnection;
    private bool airportHandlersAttached;
    private AirportRegistration airportRegistration;
    private AirportStage airportStage;
    private uint airportSequence = (uint)AirportRequestId.First;
    private uint airportExpectedRequest;
    private readonly Dictionary<uint, bool> airportPackets = [];
    private readonly HashSet<uint> airportListPackets = [];
    private uint? airportListPacketCount;
    private readonly Dictionary<uint, AirportRunwayItem> airportRunways = [];
    private AirportFields? airportFields;
    private uint airportUniqueId;
    private bool airportDataInvalid;
    private string? airportIcao;
    private string? airportRegion;
    private double airportNearestDistance = double.PositiveInfinity;
    private double airportOriginLatitude;
    private double airportOriginLongitude;
    private DateTime airportPositionEpochUtc;
    private DateTime airportDeadlineUtc;
    private string? airportText;
    private string? airportAircraftTitle;
    private uint airportSessionId;
    private AutostartRunwayData airportRunwayData = new(string.Empty, 0, AutostartRunwayDataStatus.Pending, []);

    private bool AirportFacilitiesFinished => airportStage is
        AirportStage.Complete or AirportStage.Failed;

    private void ResetAirportFacilities()
    {
        CancelAirportFacilities();
        airportPositionEpochUtc = DateTime.UtcNow;
        airportDeadlineUtc = airportPositionEpochUtc.AddSeconds(15);
        airportStage = AirportStage.Position;
        SetAirportText("AirportInformationWaitingPosition");
    }

    private void CancelAirportFacilities(bool preserveRunwayData = false)
    {
        // Ohne ClearFacilityDefinition darf ein unvollständiger Aufbau nicht erneut angehängt werden.
        if (airportRegistration == AirportRegistration.Pending)
            airportRegistration = AirportRegistration.Failed;
        airportStage = AirportStage.Inactive;
        airportExpectedRequest = 0;
        foreach (uint packet in airportPackets.Where(pair => !pair.Value).Select(pair => pair.Key).ToArray())
            airportPackets.Remove(packet);
        airportListPackets.Clear();
        airportListPacketCount = null;
        airportRunways.Clear();
        airportAircraftTitle = null;
        airportSessionId = 0;
        if (!preserveRunwayData)
            airportRunwayData = new(string.Empty, 0, AutostartRunwayDataStatus.Pending, []);
        airportFields = null;
        airportDataInvalid = false;
        airportIcao = null;
        airportRegion = null;
        airportNearestDistance = double.PositiveInfinity;
        if (airportHandlersAttached && airportConnection is { } connection)
        {
            connection.OnRecvAirportList -= ReceiveAirportList;
            connection.OnRecvFacilityData -= ReceiveAirportData;
            connection.OnRecvFacilityDataEnd -= ReceiveAirportDataEnd;
        }
        airportHandlersAttached = false;
    }

    private void SetAirportText(string key, params object[] values)
    {
        string text = string.Format(CultureInfo.CurrentCulture, AutostartText(key), values);
        if (string.Equals(airportText, text, StringComparison.Ordinal))
            return;
        airportText = text;
        Debug.WriteLine($"[Debug] {DateTime.Now:yyyy-MM-dd HH:mm:ss} - [AirportFacilities] {airportText}");
    }

    private void FailAirportFacilities(string key, string? diagnostic = null)
    {
        if (airportRegistration == AirportRegistration.Pending)
            airportRegistration = AirportRegistration.Failed;
        if (airportStage == AirportStage.Failed)
            return;
        airportExpectedRequest = 0;
        airportStage = AirportStage.Failed;
        airportRunwayData = new(airportAircraftTitle ?? string.Empty, airportSessionId,
            key == "AirportInformationIncomplete" ? AutostartRunwayDataStatus.Invalid : AutostartRunwayDataStatus.Failed, []);
        SetAirportText(key);
        if (diagnostic != null)
            Debug.WriteLine($"[Debug] {DateTime.Now:yyyy-MM-dd HH:mm:ss} - [AirportFacilities] {diagnostic}");
    }

    private void UpdateAirportFacilities(DateTime nowUtc)
    {
        if (airportStage == AirportStage.Inactive || AirportFacilitiesFinished ||
            !IsAutostartActive || autostartErrorLatched || autostart.Phase != AutostartPhase.Readiness)
            return;
        if (simconnect == null || (airportConnection != null &&
            !ReferenceEquals(simconnect, airportConnection) &&
            airportStage != AirportStage.Position))
        {
            FailAirportFacilities("AirportInformationDisconnected");
            return;
        }
        if (nowUtc >= airportDeadlineUtc)
        {
            FailAirportFacilities("AirportInformationTimeout");
            return;
        }
        if (airportStage != AirportStage.Position)
            return;
        AutostartSnapshot snapshot = autostartSnapshot;
        if (autostartIntegrationError != null || !snapshot.Title.HasValue ||
            snapshot.SessionId == 0 || snapshot.SessionId != autostartExpectedRequest ||
            string.IsNullOrWhiteSpace(snapshot.Title.Value) ||
            !FreshAirportCoordinate(snapshot.LatitudeDegrees, nowUtc, 90) ||
            !FreshAirportCoordinate(snapshot.LongitudeDegrees, nowUtc, 180))
            return;

        airportOriginLatitude = snapshot.LatitudeDegrees.Value;
        airportOriginLongitude = snapshot.LongitudeDegrees.Value;
        try
        {
            AttachAirportHandlers(simconnect);
            airportAircraftTitle = snapshot.Title.Value;
            airportSessionId = snapshot.SessionId;
            airportRunwayData = new(snapshot.Title.Value ?? string.Empty, snapshot.SessionId, AutostartRunwayDataStatus.Pending, []);
            if (airportRegistration == AirportRegistration.Failed)
            {
                FailAirportFacilities("AirportInformationDefinitionFailed");
                return;
            }
            airportExpectedRequest = NextAirportRequest();
            airportStage = AirportStage.AirportList;
            airportDeadlineUtc = nowUtc.AddSeconds(15);
            SetAirportText("AirportInformationLoadingAirports");
            simconnect.RequestFacilitiesList(SIMCONNECT_FACILITY_LIST_TYPE.AIRPORT,
                (AirportRequestId)airportExpectedRequest);
            TrackAirportPacket(simconnect, false);
        }
        catch (COMException ex)
        {
            FailAirportFacilities("AirportInformationRequestFailed", ex.ToString());
        }
        catch (InvalidOperationException ex)
        {
            FailAirportFacilities("AirportInformationRequestFailed", ex.ToString());
        }
        catch (ArgumentException ex)
        {
            FailAirportFacilities("AirportInformationRequestFailed", ex.ToString());
        }
    }

    private bool FreshAirportCoordinate(AutostartValue<double> sample, DateTime nowUtc, double limit)
        => sample.HasValue && double.IsFinite(sample.Value) && Math.Abs(sample.Value) <= limit &&
            sample.LastUpdatedUtc.Kind == DateTimeKind.Utc &&
            sample.LastUpdatedUtc >= airportPositionEpochUtc && sample.LastUpdatedUtc <= nowUtc &&
            (nowUtc - sample.LastUpdatedUtc).TotalSeconds <= Autostart.MaximumTelemetryAgeSeconds;

    private uint NextAirportRequest()
    {
        if (airportSequence >= 0xBFFFFFFF)
            throw new InvalidOperationException();
        return ++airportSequence;
    }

    private void AttachAirportHandlers(SimConnect connection)
    {
        if (!ReferenceEquals(connection, airportConnection))
        {
            CancelAirportFacilities();
            airportConnection = connection;
            airportPackets.Clear();
            airportRegistration = AirportRegistration.None;
        }
        if (airportHandlersAttached)
            return;
        connection.OnRecvAirportList += ReceiveAirportList;
        connection.OnRecvFacilityData += ReceiveAirportData;
        connection.OnRecvFacilityDataEnd += ReceiveAirportDataEnd;
        airportHandlersAttached = true;
    }

    private void TrackAirportPacket(SimConnect connection, bool definition)
        => airportPackets[connection.GetLastSentPacketID()] = definition;

    private void RegisterAirportDefinition(SimConnect connection)
    {
        if (airportRegistration == AirportRegistration.Registered)
            return;
        if (airportRegistration != AirportRegistration.None)
            throw new InvalidOperationException();
        airportRegistration = AirportRegistration.Pending;
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
            connection.AddToFacilityDefinition(AirportDefinitionId.Airport, field);
            TrackAirportPacket(connection, true);
        }
        airportRegistration = AirportRegistration.Registered;
    }

    private bool AcceptAirportResponse(SimConnect sender, uint requestId, AirportStage stage)
    {
        if (!ReferenceEquals(sender, simconnect) || !ReferenceEquals(sender, airportConnection) ||
            !IsAutostartActive || autostart.Phase != AutostartPhase.Readiness || autostartErrorLatched ||
            airportSessionId == 0 || airportSessionId != autostartExpectedRequest ||
            !string.Equals(airportAircraftTitle, autostartSnapshot.Title.Value, StringComparison.Ordinal) ||
            airportStage != stage || requestId != airportExpectedRequest)
            return false;
        if (DateTime.UtcNow >= airportDeadlineUtc)
        {
            FailAirportFacilities("AirportInformationTimeout");
            return false;
        }
        return true;
    }

    private void ReceiveAirportList(SimConnect sender, SIMCONNECT_RECV_AIRPORT_LIST data)
    {
        if (!AcceptAirportResponse(sender, data.dwRequestID, AirportStage.AirportList))
            return;
        if (data.dwOutOf == 0 && data.dwEntryNumber == 0 && data.dwArraySize == 0 &&
            data.rgData is { Length: 0 } && airportListPackets.Count == 0)
        {
            FailAirportFacilities("AirportInformationNoAirports");
            return;
        }
        if (data.dwOutOf == 0 || data.dwEntryNumber >= data.dwOutOf ||
            (airportListPacketCount.HasValue && airportListPacketCount.Value != data.dwOutOf) ||
            data.rgData == null || data.dwArraySize != (uint)data.rgData.Length)
        {
            FailAirportFacilities("AirportInformationIncomplete");
            return;
        }
        airportListPacketCount = data.dwOutOf;
        if (!airportListPackets.Add(data.dwEntryNumber))
            return;
        foreach (object entry in data.rgData)
        {
            if (entry is not SIMCONNECT_DATA_FACILITY_AIRPORT airport ||
                string.IsNullOrWhiteSpace(airport.Ident) || airport.Region == null ||
                !ValidAirportPosition(airport.Latitude, airport.Longitude, airport.Altitude))
            {
                FailAirportFacilities("AirportInformationIncomplete");
                return;
            }
            double distance = AirportDistanceKm(airportOriginLatitude,
                airportOriginLongitude, airport.Latitude, airport.Longitude);
            if (distance < airportNearestDistance)
            {
                airportNearestDistance = distance;
                airportIcao = airport.Ident;
                airportRegion = airport.Region;
            }
        }
        if ((uint)airportListPackets.Count != data.dwOutOf)
            return;
        if (airportIcao == null)
        {
            FailAirportFacilities("AirportInformationNoAirports");
            return;
        }
        try
        {
            RegisterAirportDefinition(sender);
            airportExpectedRequest = NextAirportRequest();
            airportStage = AirportStage.AirportData;
            airportDeadlineUtc = DateTime.UtcNow.AddSeconds(15);
            SetAirportText("AirportInformationLoadingRunways", airportIcao);
            sender.RequestFacilityData(AirportDefinitionId.Airport,
                (AirportRequestId)airportExpectedRequest,
                airportIcao, airportRegion ?? string.Empty);
            TrackAirportPacket(sender, false);
        }
        catch (COMException ex)
        {
            FailAirportFacilities(airportRegistration == AirportRegistration.Pending
                ? "AirportInformationDefinitionFailed" : "AirportInformationRequestFailed", ex.ToString());
        }
        catch (InvalidOperationException ex)
        {
            FailAirportFacilities(airportRegistration is AirportRegistration.Pending or
                AirportRegistration.Failed ? "AirportInformationDefinitionFailed" :
                "AirportInformationRequestFailed", ex.ToString());
        }
        catch (ArgumentException ex)
        {
            FailAirportFacilities(airportRegistration == AirportRegistration.Pending
                ? "AirportInformationDefinitionFailed" : "AirportInformationRequestFailed", ex.ToString());
        }
    }

    private void ReceiveAirportData(SimConnect sender, SIMCONNECT_RECV_FACILITY_DATA data)
    {
        if (!AcceptAirportResponse(sender, data.UserRequestId, AirportStage.AirportData))
            return;
        // Daten erst beim END veröffentlichen: Bahnen können vor dem Flughafen eintreffen.
        if (data.Data == null || data.Data.Length != 1)
        {
            airportDataInvalid = true;
            return;
        }
        if (data.Type == (uint)SIMCONNECT_FACILITY_DATA_TYPE.AIRPORT &&
            data.Data[0] is AirportFields airport)
        {
            if (airportFields.HasValue || data.IsListItem != 0 ||
                !ValidAirportPosition(airport.Latitude, airport.Longitude, airport.AltitudeMeters) ||
                !float.IsFinite(airport.MagVarDegrees) || airport.RunwayCount < 0)
                airportDataInvalid = true;
            airportFields = airport;
            airportUniqueId = data.UniqueRequestId;
        }
        else if (data.Type == (uint)SIMCONNECT_FACILITY_DATA_TYPE.RUNWAY &&
            data.Data[0] is AirportRunwayFields runway)
        {
            if (data.IsListItem != 1 || data.ItemIndex >= data.ListSize ||
                !ValidAirportRunway(runway) ||
                !airportRunways.TryAdd(data.ItemIndex,
                    new(data.UniqueRequestId, data.ParentUniqueRequestId, data.ListSize, runway)))
                airportDataInvalid = true;
        }
        else
            airportDataInvalid = true;
    }

    private void ReceiveAirportDataEnd(SimConnect sender, SIMCONNECT_RECV_FACILITY_DATA_END data)
    {
        if (!AcceptAirportResponse(sender, data.RequestId, AirportStage.AirportData))
            return;
        if (airportDataInvalid || airportIcao is not { } icao ||
            airportFields is not { } airport ||
            airportRunways.Count != airport.RunwayCount ||
            airportRunways.Any(pair => pair.Key >= (uint)airport.RunwayCount ||
                pair.Value.ListSize != (uint)airport.RunwayCount ||
                pair.Value.ParentId != airportUniqueId ||
                pair.Value.UniqueId == airportUniqueId) ||
            airportRunways.Values.Select(item => item.UniqueId).Distinct().Count() != airport.RunwayCount)
        {
            FailAirportFacilities("AirportInformationIncomplete");
            return;
        }
        airportRegistration = AirportRegistration.Registered;
        airportExpectedRequest = 0;
        airportStage = airport.RunwayCount == 0 ? AirportStage.Failed : AirportStage.Complete;
        AutostartRunwayGeometry[] runways = [.. airportRunways.OrderBy(pair => pair.Key)
            .Select(pair => pair.Value.Fields)
            .Select(runway => new AutostartRunwayGeometry(runway.Latitude, runway.Longitude,
                runway.HeadingTrueDegrees, runway.LengthMeters, runway.WidthMeters))];
        airportRunwayData = new(airportAircraftTitle ?? string.Empty, airportSessionId,
            airport.RunwayCount == 0 ? AutostartRunwayDataStatus.Failed : AutostartRunwayDataStatus.Complete,
            Array.AsReadOnly(runways));
        List<string> lines =
        [
            string.Format(CultureInfo.CurrentCulture, AutostartText("AirportInformationAirportFormat"),
                icao, string.IsNullOrWhiteSpace(airport.Name) ? AutostartText("Unavailable") : airport.Name,
                airport.Latitude, airport.Longitude, airport.AltitudeMeters, airportNearestDistance),
            AutostartText("AirportInformationRunwaysHeading")
        ];
        foreach (AirportRunwayItem item in airportRunways.OrderBy(pair => pair.Key).Select(pair => pair.Value))
        {
            AirportRunwayFields runway = item.Fields;
            lines.Add(string.Format(CultureInfo.CurrentCulture, AutostartText("AirportInformationRunwayFormat"),
                AirportRunwayIdentifier(runway.PrimaryNumber, runway.PrimaryDesignator),
                AirportRunwayIdentifier(runway.SecondaryNumber, runway.SecondaryDesignator),
                runway.Latitude, runway.Longitude, runway.AltitudeMeters, runway.LengthMeters,
                runway.WidthMeters, runway.HeadingTrueDegrees % 360.0, (runway.HeadingTrueDegrees + 180.0) % 360.0));
        }
        if (airport.RunwayCount == 0)
            lines.Add(AutostartText("AirportInformationNoRunways"));
        lines.Add(AutostartText("AirportInformationRunwayValidation"));
        airportText = string.Join(Environment.NewLine, lines);
        Debug.WriteLine($"[Debug] {DateTime.Now:yyyy-MM-dd HH:mm:ss} - [AirportFacilities] {airportStage}: {airportIcao}, runways={airport.RunwayCount}");
    }

    private bool ReceiveAirportException(SimConnect sender, SIMCONNECT_RECV_EXCEPTION data)
    {
        if (!ReferenceEquals(sender, airportConnection) ||
            !ReferenceEquals(sender, simconnect) ||
            !airportPackets.TryGetValue(data.dwSendID, out bool definition))
            return false;
        if (definition)
        {
            bool alreadyFailed = airportRegistration == AirportRegistration.Failed;
            airportRegistration = AirportRegistration.Failed;
            if (!alreadyFailed)
                Debug.WriteLine($"[Debug] {DateTime.Now:yyyy-MM-dd HH:mm:ss} - [AirportFacilities] SimConnect exception={data.dwException}, SendID={data.dwSendID}, Index={data.dwIndex}");
        }
        else
            airportPackets.Remove(data.dwSendID);
        if (!IsAutostartActive || autostartErrorLatched)
            return true;
        if (definition && autostart.Phase == AutostartPhase.StartRoll)
        {
            airportRunwayData = airportRunwayData with { Status = AutostartRunwayDataStatus.Failed };
            SetAirportText("AirportInformationDefinitionFailed");
        }
        else if (autostart.Phase == AutostartPhase.Readiness &&
            (definition || airportSessionId == autostartExpectedRequest) &&
            airportStage != AirportStage.Inactive)
            FailAirportFacilities(definition ? "AirportInformationDefinitionFailed" : "AirportInformationRequestFailed",
                definition ? null : $"SimConnect exception={data.dwException}, SendID={data.dwSendID}, Index={data.dwIndex}");
        return true;
    }

    private static bool ValidAirportPosition(double latitude, double longitude, double altitude)
        => double.IsFinite(latitude) && Math.Abs(latitude) <= 90 &&
            double.IsFinite(longitude) && Math.Abs(longitude) <= 180 && double.IsFinite(altitude);

    private static bool ValidAirportRunway(AirportRunwayFields runway)
        => ValidAirportPosition(runway.Latitude, runway.Longitude, runway.AltitudeMeters) &&
            float.IsFinite(runway.HeadingTrueDegrees) && runway.HeadingTrueDegrees >= 0 && runway.HeadingTrueDegrees <= 360 &&
            float.IsFinite(runway.LengthMeters) && runway.LengthMeters > 0 &&
            float.IsFinite(runway.WidthMeters) && runway.WidthMeters > 0 &&
            runway.PrimaryNumber is >= 0 and <= 44 && runway.SecondaryNumber is >= 0 and <= 44 &&
            runway.PrimaryDesignator is >= 0 and <= 6 && runway.SecondaryDesignator is >= 0 and <= 6;

    private string AirportRunwayIdentifier(int number, int designator)
    {
        string identifier = number switch
        {
            >= 1 and <= 36 => number.ToString("D2", CultureInfo.CurrentCulture),
            37 => "N", 38 => "NE", 39 => "E", 40 => "SE", 41 => "S", 42 => "SW", 43 => "W", 44 => "NW",
            _ => AutostartText("Unavailable")
        };
        if (number == 0)
            return identifier;
        return identifier + (designator switch
        {
            1 => "L", 2 => "R", 3 => "C", 4 => "W", 5 => "A", 6 => "B", _ => string.Empty
        });
    }

    private static double AirportDistanceKm(double latitude1, double longitude1, double latitude2, double longitude2)
    {
        const double radians = Math.PI / 180;
        double a = Math.Pow(Math.Sin((latitude2 - latitude1) * radians / 2), 2) +
            Math.Cos(latitude1 * radians) * Math.Cos(latitude2 * radians) *
            Math.Pow(Math.Sin((longitude2 - longitude1) * radians / 2), 2);
        return 6371 * 2 * Math.Asin(Math.Sqrt(Math.Clamp(a, 0, 1)));
    }
}

internal static class SimConnectText
{
    internal static string DecodeUtf8(ReadOnlySpan<byte> buffer)
    {
        int terminator = buffer.IndexOf((byte)0);
        if (terminator >= 0)
            buffer = buffer[..terminator];
        return Encoding.UTF8.GetString(buffer);
    }
}
