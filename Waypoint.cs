using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace AutoFly;

/// <summary>Unveränderliche Momentaufnahme eines Wegpunkts; Nummern sind einsbasiert, IDs bleiben stabil.</summary>
/// <param name="Id">Innerhalb des Managers eindeutige, niemals wiederverwendete ID.</param>
/// <param name="Number">Aktuelle Position in der Route, beginnend bei eins.</param>
/// <param name="Latitude">Breitengrad in Grad.</param>
/// <param name="Longitude">Längengrad in Grad.</param>
/// <param name="AltitudeFeet">Optionale positive Sollhöhe in Fuß.</param>
/// <param name="ActivatedAt">UTC-Aktivierungszeit; mit Completed=false kennzeichnet sie das aktive Ziel.</param>
/// <param name="Completed">Der Wegpunkt wurde erreicht oder beim manuellen Zielen übersprungen.</param>
/// <param name="DistanceNauticalMiles">Entfernung vom Vorgänger; am Routenanfang ohne Wiederholung null Seemeilen.</param>
/// <param name="NextId">ID des Nachfolgers, bei Wiederholung einschließlich Ringschluss.</param>
/// <param name="HeadingDegrees">Wahrer Anfangskurs zum Nachfolger in [0,360), auf zwei Nachkommastellen gerundet.</param>
public record WaypointSnapshot(long Id, int Number, double Latitude, double Longitude,
    double? AltitudeFeet, DateTimeOffset? ActivatedAt, bool Completed, double DistanceNauticalMiles,
    long? NextId, double? HeadingDegrees)
{
    /// <summary>Optionaler fester Gelände-Abstand in Fuß; schließt eine absolute Höhenvorgabe aus.</summary>
    public double? HeightAboveGroundFeet { get; init; }

    /// <summary>Gespeicherte Bodenhöhe an diesen Koordinaten in Fuß über Meereshöhe.</summary>
    public double? GroundElevationFeet { get; init; }

    /// <summary>Feste Zielhöhe; ohne erforderliche Geländedaten bleibt sie unbekannt.</summary>
    public double? TargetAltitudeFeet => HeightAboveGroundFeet is double distance
        ? GroundElevationFeet is double ground && double.IsFinite(ground + distance) ? ground + distance : null
        : AltitudeFeet;
}

/// <summary>Magnetischer Sollkurs und optionale Sollhöhe einer Navigationsabfrage.</summary>
/// <param name="MagneticHeadingDegrees">Magnetischer Kurs in [0,360), auf zwei Nachkommastellen gerundet.</param>
/// <param name="AltitudeFeet">Optionale positive Sollhöhe in Fuß.</param>
public record WaypointNavigation(double MagneticHeadingDegrees, double? AltitudeFeet)
{
    /// <summary>Optionaler Gelände-Abstand des aktiven Ziels in Fuß.</summary>
    public double? HeightAboveGroundFeet { get; init; }

    /// <summary>Verwendeter, geometrisch begrenzter Vorhaltradius in Kilometern; bei Legacy-Abfragen null.</summary>
    public double? TransitionRadiusKilometers { get; init; }

    /// <summary>Gibt eine feste Zielhöhe unverändert zurück; nur ältere Abfragen ohne Zielhöhe verwenden den AGL-Fallback.</summary>
    public double? GetIndicatedAltitudeTarget(double indicatedAltitudeFeet, double currentHeightAboveGroundFeet)
    {
        if (!double.IsFinite(indicatedAltitudeFeet))
            throw new ArgumentOutOfRangeException(nameof(indicatedAltitudeFeet));
        if (!double.IsFinite(currentHeightAboveGroundFeet) || currentHeightAboveGroundFeet < 0.0)
            throw new ArgumentOutOfRangeException(nameof(currentHeightAboveGroundFeet));
        if (AltitudeFeet.HasValue)
            return AltitudeFeet;
        if (HeightAboveGroundFeet is not double distance)
            return AltitudeFeet;
        if (!double.IsFinite(distance) || distance <= 0.0)
            throw new InvalidOperationException(Resources_DE.GetString("Waypoint_InvalidGroundDistance"));
        double target = indicatedAltitudeFeet + (distance - currentHeightAboveGroundFeet);
        if (!double.IsFinite(target))
            throw new ArgumentOutOfRangeException(nameof(indicatedAltitudeFeet));

        return target;
    }
}

/// <summary>Verwaltet eine Route im aufrufenden Thread, unabhängig von WinForms, WebView2 und Simulator.</summary>
public sealed class WaypointManager
{
    private const double EarthRadiusKilometers = 6371.0;
    private const string Channel = "autoFly.waypoints";
    private readonly List<WaypointSnapshot> waypoints = [];
    private double? routeHeightAboveGroundFeet;
    private long lastId;
    private long revision;
    private bool repeating;
    private long navigationRevision = -1;
    private WaypointSnapshot[] navigationRoute = [];
    private long? navigationTargetId;
    private DateTimeOffset? navigationActivatedAt;
    private bool navigationRepeating;
    private (double Latitude, double Longitude)? initialLegStart;
    private (double Latitude, double Longitude, double Radius)? previousTransition;

    /// <summary>Wird synchron nach einer tatsächlichen Zustandsänderung ausgelöst.</summary>
    public event EventHandler? Changed;

    /// <summary>Monoton steigende Zustandsversion, auch über Clear hinweg.</summary>
    public long Revision => revision;

    /// <summary>Gibt an, ob die Route einschließlich Ringschluss wiederholt wird.</summary>
    public bool IsRepeating => repeating;

    /// <summary>Liefert eine vom Manager getrennte, schreibgeschützte Momentaufnahme der Route.</summary>
    public IReadOnlyList<WaypointSnapshot> GetWaypoints() => Array.AsReadOnly(waypoints.ToArray());

    /// <summary>Ersetzt die Route durch ein vollständig geprüftes gespeichertes JSON-Array; startet am ersten Ziel ohne Wiederholung.</summary>
    /// <param name="json">Array mit lat, long, optionalem alt (Zahl, false oder null) und optionalem heightAboveGroundFeet.</param>
    /// <exception cref="JsonException">Die Datei ist kein gültiges JSON.</exception>
    /// <exception cref="ArgumentException">Dateistruktur, Koordinaten oder Höhen sind ungültig; die bisherige Route bleibt erhalten.</exception>
    public void LoadWaypoints(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using JsonDocument document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new ArgumentException(null, nameof(json));
        List<WaypointSnapshot> loaded = [];
        long nextId = lastId;
        foreach (JsonElement point in document.RootElement.EnumerateArray())
        {
            if (point.ValueKind != JsonValueKind.Object)
                throw new ArgumentException(null, nameof(json));
            double latitude = ReadDouble(point, "lat");
            double longitude = ReadDouble(point, "long");
            ValidatePosition(latitude, longitude);
            double? altitude = point.TryGetProperty("alt", out JsonElement value) && value.ValueKind == JsonValueKind.False
                ? null : NormalizeElevation(ReadElevation(point, false, "alt"));
            double? distance = NormalizeElevation(ReadElevation(point, false, "heightAboveGroundFeet"));
            if (altitude.HasValue && distance.HasValue)
                throw new ArgumentException(null, nameof(json));
            double? ground = ReadElevation(point, false, "groundElevationFeet");
            if (ground.HasValue && (!distance.HasValue || !double.IsFinite(ground.Value + distance.Value)))
                throw new ArgumentException(null, nameof(json));
            loaded.Add(new WaypointSnapshot(checked(++nextId), loaded.Count + 1, latitude, longitude, altitude,
                loaded.Count == 0 ? DateTimeOffset.UtcNow : null, false, 0.0, null, null)
            {
                HeightAboveGroundFeet = distance,
                GroundElevationFeet = ground
            });
        }
        waypoints.Clear();
        waypoints.AddRange(loaded);
        lastId = nextId;
        routeHeightAboveGroundFeet = loaded.Count > 0 ? loaded[0].HeightAboveGroundFeet : null;
        repeating = false;
        Commit();
    }

    /// <summary>Hängt einen Wegpunkt an; der erste Wegpunkt wird sofort aktiviert.</summary>
    /// <param name="latitude">Breitengrad in Grad.</param>
    /// <param name="longitude">Längengrad in Grad.</param>
    /// <param name="altitudeFeet">Optionale absolute Sollhöhe, alternativ zum Gelände-Abstand.</param>
    /// <param name="heightAboveGroundFeet">Optionaler Gelände-Abstand; ohne Höhenangabe wird der beim ersten Wegpunkt erfasste Abstand übernommen.</param>
    /// <exception cref="ArgumentOutOfRangeException">Koordinaten oder Höhe sind ungültig.</exception>
    public WaypointSnapshot Add(double latitude, double longitude, double? altitudeFeet = null,
        double? heightAboveGroundFeet = null)
    {
        ValidatePosition(latitude, longitude);
        double? altitude = NormalizeElevation(altitudeFeet);
        double? distance = NormalizeElevation(heightAboveGroundFeet);
        if (altitude.HasValue && distance.HasValue)
            throw new ArgumentException(null, nameof(heightAboveGroundFeet));
        if (!altitudeFeet.HasValue && !heightAboveGroundFeet.HasValue && waypoints.Count > 0)
            distance = routeHeightAboveGroundFeet;
        long id = checked(lastId + 1);
        waypoints.Add(new WaypointSnapshot(id, waypoints.Count + 1, latitude, longitude, altitude,
            waypoints.Count == 0 ? DateTimeOffset.UtcNow : null, false, 0.0, null, null)
        {
            HeightAboveGroundFeet = distance
        });
        if (waypoints.Count == 1)
            routeHeightAboveGroundFeet = distance;
        lastId = id;
        Commit();

        return waypoints[FindIndex(id)];
    }

    /// <summary>Verschiebt einen bestehenden Wegpunkt und berechnet die betroffenen Routenwerte neu.</summary>
    public void SetPosition(long id, double latitude, double longitude)
    {
        ValidatePosition(latitude, longitude);
        int index = FindIndex(id);
        WaypointSnapshot waypoint = waypoints[index];
        if (waypoint.Latitude == latitude && waypoint.Longitude == longitude)
            return;
        waypoints[index] = waypoint with { Latitude = latitude, Longitude = longitude, GroundElevationFeet = null };
        Commit();
    }

    /// <summary>Übernimmt eine Geländeantwort nur für einen noch vorhandenen Wegpunkt an den abgefragten Koordinaten.</summary>
    public bool TrySetGroundElevation(long id, double latitude, double longitude, double groundElevationFeet)
    {
        ValidatePosition(latitude, longitude);
        if (!double.IsFinite(groundElevationFeet))
            throw new ArgumentOutOfRangeException(nameof(groundElevationFeet));
        int index = waypoints.FindIndex(point => point.Id == id);
        if (index < 0)
            return false;
        WaypointSnapshot point = waypoints[index];
        if (point.Latitude != latitude || point.Longitude != longitude || !point.HeightAboveGroundFeet.HasValue)
            return false;
        if (!double.IsFinite(groundElevationFeet + point.HeightAboveGroundFeet.Value))
            throw new ArgumentOutOfRangeException(nameof(groundElevationFeet));
        if (point.GroundElevationFeet == groundElevationFeet)
            return true;
        waypoints[index] = point with { GroundElevationFeet = groundElevationFeet };
        Commit();
        return true;
    }

    /// <summary>Setzt die Höhe; null und endliche Werte kleiner/gleich null löschen die Höhenvorgabe.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Die Höhe ist NaN oder unendlich.</exception>
    public void SetElevation(long id, double? altitudeFeet)
    {
        double? altitude = NormalizeElevation(altitudeFeet);
        int index = FindIndex(id);
        if (waypoints[index].AltitudeFeet == altitude && waypoints[index].HeightAboveGroundFeet == null)
            return;
        waypoints[index] = waypoints[index] with
        {
            AltitudeFeet = altitude, HeightAboveGroundFeet = null, GroundElevationFeet = null
        };
        Commit();
    }

    /// <summary>Setzt den festen Gelände-Abstand und entfernt eine absolute Höhenvorgabe; null oder Werte kleiner/gleich null löschen die Vorgabe.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Der Abstand ist NaN oder unendlich, oder die ID ist unbekannt.</exception>
    public void SetHeightAboveGround(long id, double? heightAboveGroundFeet)
    {
        double? distance = NormalizeElevation(heightAboveGroundFeet);
        int index = FindIndex(id);
        if (waypoints[index].HeightAboveGroundFeet == distance && waypoints[index].AltitudeFeet == null)
            return;
        double? ground = distance.HasValue ? waypoints[index].GroundElevationFeet : null;
        if (ground.HasValue && !double.IsFinite(ground.Value + distance!.Value))
            throw new ArgumentOutOfRangeException(nameof(heightAboveGroundFeet));
        waypoints[index] = waypoints[index] with
        {
            HeightAboveGroundFeet = distance, AltitudeFeet = null, GroundElevationFeet = ground
        };
        if (index == 0 && !routeHeightAboveGroundFeet.HasValue)
            routeHeightAboveGroundFeet = distance;
        Commit();
    }

    /// <summary>Entfernt einen Wegpunkt; bei aktivem Ziel wird dessen Nachfolger aktiviert, ohne Wiederholung endet die Route am letzten Ziel.</summary>
    public void Remove(long id)
    {
        int index = FindIndex(id);
        bool wasActive = IsActive(waypoints[index]);
        waypoints.RemoveAt(index);
        if (waypoints.Count == 0)
            routeHeightAboveGroundFeet = null;
        if (wasActive && waypoints.Count > 0)
        {
            if (index < waypoints.Count)
                Activate(index);
            else if (repeating)
                ResetCore();
        }
        Commit();
    }

    /// <summary>Dupliziert Position und Höhe unmittelbar nach dem Original, mit neuer ID und ohne dessen Flugstatus.</summary>
    public WaypointSnapshot Duplicate(long id)
    {
        int index = FindIndex(id);
        long newId = checked(lastId + 1);
        WaypointSnapshot original = waypoints[index];
        waypoints.Insert(index + 1, original with
        {
            Id = newId, ActivatedAt = null, Completed = false
        });
        lastId = newId;
        Commit();

        return waypoints[FindIndex(newId)];
    }

    /// <summary>Aktiviert ein Ziel, markiert Vorgänger als erledigt und setzt den Flugstatus ab diesem Ziel zurück.</summary>
    public void Target(long id)
    {
        int index = FindIndex(id);
        for (int i = 0; i < waypoints.Count; i++)
            waypoints[i] = waypoints[i] with { Completed = i < index, ActivatedAt = null };
        Activate(index);
        Commit();
    }

    /// <summary>Setzt nur den Flugstatus zurück und aktiviert den ersten Wegpunkt; IDs, Höhen und Wiederholung bleiben erhalten.</summary>
    public void ResetWaypoints()
    {
        if (waypoints.Count == 0)
            return;
        ResetCore();
        Commit();
    }

    /// <summary>Entfernt alle Wegpunkte, behält aber ID-Zähler und Wiederholungseinstellung bei.</summary>
    public void Clear()
    {
        if (waypoints.Count == 0)
            return;
        waypoints.Clear();
        routeHeightAboveGroundFeet = null;
        Commit();
    }

    /// <summary>Schaltet die Wiederholung um; eine bereits beendete Route startet beim Einschalten wieder am Anfang.</summary>
    public bool ToggleRepeating()
    {
        repeating = !repeating;
        if (repeating && waypoints.Count > 0 && !waypoints.Any(IsActive))
            ResetCore();
        Commit();

        return repeating;
    }

    /// <summary>
    /// Legacy-Navigation: berechnet zunächst den Kurs zum bisherigen Ziel und schaltet erst danach bei strikt weniger als 1 km weiter.
    /// Der Gelände-Abstand stammt vom danach aktiven Ziel; absolute Höhen berücksichtigen dessen höheren Nachfolger.
    /// Ohne aktives Ziel wird keine Höhenvorgabe geliefert.
    /// Eine Einpunkt-Rundroute bleibt aktiv, ohne bei jeder Abfrage erneut umzuschalten.
    /// </summary>
    /// <param name="latitude">Aktueller Breitengrad in [-90,90].</param>
    /// <param name="longitude">Aktueller Längengrad in [-180,180].</param>
    /// <param name="declination">Endliche Missweisung: wahrer Kurs minus magnetischer Kurs, in Grad.</param>
    public WaypointNavigation? GetNavigation(double latitude, double longitude, double declination)
    {
        ValidatePosition(latitude, longitude);
        if (!double.IsFinite(declination))
            throw new ArgumentOutOfRangeException(nameof(declination));
        int index = waypoints.FindIndex(IsActive);
        if (index < 0)
            return null;
        WaypointSnapshot target = waypoints[index];
        double heading = NormalizeHeading(Math.Round(NormalizeHeading(
            Bearing(latitude, longitude, target.Latitude, target.Longitude) - NormalizeHeading(declination)), 2));
        if (!(repeating && waypoints.Count == 1) &&
            DistanceKilometers(latitude, longitude, target.Latitude, target.Longitude) < 1.0)
        {
            waypoints[index] = target with { Completed = true };
            if (index + 1 < waypoints.Count)
                Activate(index + 1);
            else if (repeating)
                ResetCore();
            Commit();
        }
        int activeIndex = waypoints.FindIndex(IsActive);
        double? altitude = null;
        double? heightAboveGround = null;
        if (activeIndex >= 0)
        {
            altitude = waypoints[activeIndex].TargetAltitudeFeet;
            heightAboveGround = waypoints[activeIndex].HeightAboveGroundFeet;
            int nextIndex = activeIndex + 1 < waypoints.Count ? activeIndex + 1 : repeating ? 0 : -1;
            if (!heightAboveGround.HasValue && nextIndex >= 0 && waypoints[nextIndex].AltitudeFeet is double nextAltitude &&
                (!altitude.HasValue || nextAltitude > altitude.Value))
                altitude = nextAltitude;
        }

        return new WaypointNavigation(heading, altitude) { HeightAboveGroundFeet = heightAboveGround };
    }

    /// <summary>
    /// Liefert einen sphärisch vorausprojizierten magnetischen Sollkurs mit Cross-track-Korrektur und Fly-by-Übergängen.
    /// Das aktive Ziel ist das Legende. Der Vorhalt beträgt mindestens 20 Flugsekunden, soweit die Leglängen dies zulassen.
    /// Ungültige Eingaben ändern keinen Zustand; bei Stillstand werden keine Ziele abgeschlossen.
    /// </summary>
    /// <param name="latitude">Aktueller Breitengrad in [-90,90].</param>
    /// <param name="longitude">Aktueller Längengrad in [-180,180].</param>
    /// <param name="declination">Endliche Missweisung: wahrer Kurs minus magnetischer Kurs, in Grad.</param>
    /// <param name="speedKnots">Endliche, nicht negative angezeigte Fluggeschwindigkeit in Knoten.</param>
    /// <param name="stallSpeedKnots">Endliche positive Modellgeschwindigkeit VS1 in Knoten.</param>
    /// <param name="cruiseSpeedKnots">Endliche Modellgeschwindigkeit VC in Knoten, größer als VS1.</param>
    public WaypointNavigation? GetNavigation(double latitude, double longitude, double declination,
        double speedKnots, double stallSpeedKnots, double cruiseSpeedKnots)
    {
        ValidatePosition(latitude, longitude);
        if (!double.IsFinite(declination))
            throw new ArgumentOutOfRangeException(nameof(declination));
        if (!double.IsFinite(speedKnots) || speedKnots < 0.0)
            throw new ArgumentOutOfRangeException(nameof(speedKnots));
        if (!double.IsFinite(stallSpeedKnots) || stallSpeedKnots <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(stallSpeedKnots));
        if (!double.IsFinite(cruiseSpeedKnots) || cruiseSpeedKnots <= stallSpeedKnots)
            throw new ArgumentOutOfRangeException(nameof(cruiseSpeedKnots));
        int index = waypoints.FindIndex(IsActive);
        if (index < 0)
            return null;
        if (navigationRevision != revision)
        {
            bool sameGeometry = navigationRepeating == repeating && navigationTargetId == waypoints[index].Id &&
                navigationActivatedAt == waypoints[index].ActivatedAt && navigationRoute.Length == waypoints.Count;
            for (int i = 0; sameGeometry && i < waypoints.Count; i++)
                sameGeometry = navigationRoute[i].Id == waypoints[i].Id &&
                    navigationRoute[i].Latitude == waypoints[i].Latitude && navigationRoute[i].Longitude == waypoints[i].Longitude;
            // Neue Gelände- oder Höhenwerte ändern weder das Leg noch den ausfliegenden Kurvenradius.
            if (!sameGeometry)
            {
                initialLegStart = null;
                previousTransition = null;
            }
            navigationRoute = [.. waypoints];
            navigationTargetId = waypoints[index].Id;
            navigationActivatedAt = waypoints[index].ActivatedAt;
            navigationRepeating = repeating;
            navigationRevision = revision;
        }
        initialLegStart ??= (latitude, longitude);
        WaypointSnapshot target = waypoints[index];
        int previous = index > 0 ? index - 1 : repeating && waypoints.Count > 1 ? waypoints.Count - 1 : -1;
        int next = index + 1 < waypoints.Count ? index + 1 : repeating && waypoints.Count > 1 ? 0 : -1;
        (double startLatitude, double startLongitude) = previous >= 0
            ? (waypoints[previous].Latitude, waypoints[previous].Longitude) : initialLegStart.Value;
        double incomingLength = DistanceKilometers(startLatitude, startLongitude, target.Latitude, target.Longitude);
        double speedKilometersPerSecond = speedKnots * (1.852 / 3600.0);
        double turn = next < 0 || incomingLength < 1e-9 ? 0.0 : Math.Abs(SignedHeadingDifference(
            Bearing(target.Latitude, target.Longitude, startLatitude, startLongitude) + 180.0,
            Bearing(target.Latitude, target.Longitude, waypoints[next].Latitude, waypoints[next].Longitude)));
        double speedScale = 0.1 + 0.9 * Math.Clamp(
            (speedKnots - stallSpeedKnots) / (cruiseSpeedKnots - stallSpeedKnots), 0.0, 1.0);
        double radius = speedKilometersPerSecond * Math.Max(20.0, turn / 3.0 * speedScale);
        // Disjunkte Übergangsbereiche verhindern radiusbedingtes Überspringen dichter Ziele.
        radius = Math.Min(radius, incomingLength * 0.45);
        if (next >= 0)
            radius = Math.Min(radius, DistanceKilometers(target.Latitude, target.Longitude,
                waypoints[next].Latitude, waypoints[next].Longitude) * 0.45);
        double distance = DistanceKilometers(latitude, longitude, target.Latitude, target.Longitude);
        if (previousTransition is { Radius: > 0.0 } prior &&
            DistanceKilometers(latitude, longitude, prior.Latitude, prior.Longitude) < distance)
            radius = prior.Radius;

        (double Latitude, double Longitude) aim = ProjectAhead(startLatitude, startLongitude,
            target.Latitude, target.Longitude, latitude, longitude, radius);
        bool singleRepeating = repeating && waypoints.Count == 1;
        bool alreadyTransitionedHere = previousTransition is { } lastTransition &&
            latitude == lastTransition.Latitude && longitude == lastTransition.Longitude;
        bool transition = speedKnots > 0.0 && !singleRepeating && !alreadyTransitionedHere &&
            next >= 0 && distance <= radius;
        // Ein Endziel wird erst innerhalb von zwei Flugsekunden erreicht, nicht bereits am Vorhaltkreis.
        bool arrived = speedKnots > 0.0 && !singleRepeating && !alreadyTransitionedHere && next < 0 &&
            distance <= Math.Min(radius, speedKilometersPerSecond * 2.0);
        if (transition)
        {
            WaypointSnapshot successor = waypoints[next];
            aim = ProjectAhead(target.Latitude, target.Longitude, successor.Latitude, successor.Longitude,
                latitude, longitude, radius);
        }
        double aimDistance = DistanceKilometers(latitude, longitude, aim.Latitude, aim.Longitude);
        double trueHeading = aimDistance > 1e-9
            ? Bearing(latitude, longitude, aim.Latitude, aim.Longitude)
            : incomingLength > 1e-9
                ? NormalizeHeading(Bearing(target.Latitude, target.Longitude, startLatitude, startLongitude) + 180.0)
                : next >= 0 ? Bearing(target.Latitude, target.Longitude, waypoints[next].Latitude, waypoints[next].Longitude)
                : double.NaN;
        if (transition || arrived)
        {
            waypoints[index] = target with { Completed = true };
            if (next == 0)
                ResetCore();
            else if (next >= 0)
                Activate(next);
            long expectedRevision = checked(revision + 1);
            navigationTargetId = next >= 0 ? waypoints[next].Id : null;
            navigationActivatedAt = next >= 0 ? waypoints[next].ActivatedAt : null;
            Commit();
            navigationRevision = expectedRevision;
            previousTransition = (target.Latitude, target.Longitude, radius);
        }
        int activeIndex = waypoints.FindIndex(IsActive);
        if (activeIndex < 0 || !double.IsFinite(trueHeading))
            return null;
        WaypointSnapshot active = waypoints[activeIndex];
        double? altitude = active.TargetAltitudeFeet;
        int following = activeIndex + 1 < waypoints.Count ? activeIndex + 1 : repeating ? 0 : -1;
        if (!active.HeightAboveGroundFeet.HasValue && following >= 0 &&
            waypoints[following].AltitudeFeet is double nextAltitude && (!altitude.HasValue || nextAltitude > altitude.Value))
            altitude = nextAltitude;

        return new WaypointNavigation(NormalizeHeading(Math.Round(NormalizeHeading(
            trueHeading - NormalizeHeading(declination)), 2)), altitude)
        {
            HeightAboveGroundFeet = active.HeightAboveGroundFeet,
            TransitionRadiusKilometers = radius
        };
    }

    private static double SignedHeadingDifference(double first, double second) =>
        NormalizeHeading(second - first + 180.0) - 180.0;

    private static (double Latitude, double Longitude) ProjectAhead(double startLatitude, double startLongitude,
        double endLatitude, double endLongitude, double latitude, double longitude, double radius)
    {
        double length = DistanceKilometers(startLatitude, startLongitude, endLatitude, endLongitude);
        if (length < 1e-9)
            return (endLatitude, endLongitude);
        double course = Bearing(startLatitude, startLongitude, endLatitude, endLongitude);
        double angularDistance = DistanceKilometers(startLatitude, startLongitude, latitude, longitude) / EarthRadiusKilometers;
        double angle = SignedHeadingDifference(course, Bearing(startLatitude, startLongitude, latitude, longitude)) * Math.PI / 180.0;
        double crossTrack = Math.Asin(Math.Clamp(Math.Sin(angularDistance) * Math.Sin(angle), -1.0, 1.0));
        double alongTrack = Math.Atan2(Math.Sin(angularDistance) * Math.Cos(angle), Math.Cos(angularDistance));
        double angularRadius = radius / EarthRadiusKilometers;
        // Schnitt des Vorhaltkreises mit dem Großkreis; außerhalb des Kreises wird der Lotfuß angeflogen.
        double forward = Math.Abs(crossTrack) >= angularRadius ? 0.0 : Math.Atan2(Math.Sqrt(Math.Max(0.0,
            Math.Pow(Math.Sin(angularRadius), 2) - Math.Pow(Math.Sin(crossTrack), 2))), Math.Cos(angularRadius));
        double along = Math.Clamp((alongTrack + forward) * EarthRadiusKilometers, 0.0, length);

        return Destination(startLatitude, startLongitude, course, along);
    }

    internal static (double Latitude, double Longitude) Destination(double latitude, double longitude,
        double bearing, double distanceKilometers)
    {
        double phi = latitude * Math.PI / 180.0;
        double course = bearing * Math.PI / 180.0;
        double arc = distanceKilometers / EarthRadiusKilometers;
        double targetPhi = Math.Asin(Math.Clamp(Math.Sin(phi) * Math.Cos(arc) +
            Math.Cos(phi) * Math.Sin(arc) * Math.Cos(course), -1.0, 1.0));
        double delta = Math.Atan2(Math.Sin(course) * Math.Sin(arc) * Math.Cos(phi),
            Math.Cos(arc) - Math.Sin(phi) * Math.Sin(targetPhi));

        return (targetPhi * 180.0 / Math.PI, NormalizeHeading(longitude + delta * 180.0 / Math.PI + 180.0) - 180.0);
    }

    /// <summary>
    /// Verarbeitet ausschließlich JSON-Objekte mit version=1 und channel="autoFly.waypoints".
    /// Aktionen: add/move (lat,lng), list, setElevation (id,altitudeFeet), remove/duplicate/target (id),
    /// reset, clear und toggleRepeating. add akzeptiert optional altitudeFeet oder heightAboveGroundFeet; move benötigt id.
    /// setHeightAboveGround (id,heightAboveGroundFeet) setzt den Gelände-Abstand, null entfernt ihn.
    /// requestId ist optional (maximal 128 Zeichen) und wird von der Formularbrücke bestätigt.
    /// initialHeightAboveGroundFeet wird nur beim ersten add ohne explizite Höhenangabe verwendet.
    /// Ungültiges JSON wirft JsonException, ein ungültiges Protokoll oder unbekannte IDs ArgumentException.
    /// Alle Eingaben werden vor einer Änderung validiert; list verändert weder Status noch Revision.
    /// </summary>
    public IReadOnlyList<WaypointSnapshot> ProcessCommand(string json, double? initialHeightAboveGroundFeet = null)
    {
        ArgumentNullException.ThrowIfNull(json);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("version", out JsonElement version) ||
            version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out int versionNumber) || versionNumber != 1 ||
            ReadString(root, "channel") != Channel)
            throw new ArgumentException(null, nameof(json));
        if (root.TryGetProperty("requestId", out JsonElement requestId) &&
            (requestId.ValueKind != JsonValueKind.String || (requestId.GetString() ?? string.Empty).Length > 128))
            throw new ArgumentException(null, nameof(json));
        string action = ReadString(root, "action");
        switch (action)
        {
            case "add":
                double? initialDistance = waypoints.Count == 0 &&
                    !root.TryGetProperty("altitudeFeet", out _) && !root.TryGetProperty("heightAboveGroundFeet", out _)
                    ? initialHeightAboveGroundFeet : null;
                Add(ReadDouble(root, "lat"), ReadDouble(root, "lng"), ReadElevation(root, false),
                    ReadElevation(root, false, "heightAboveGroundFeet") ?? initialDistance);
                break;
            case "move":
                SetPosition(ReadId(root), ReadDouble(root, "lat"), ReadDouble(root, "lng"));
                break;
            case "list":
                break;
            case "setElevation":
                SetElevation(ReadId(root), ReadElevation(root, true));
                break;
            case "setHeightAboveGround":
                SetHeightAboveGround(ReadId(root), ReadElevation(root, true, "heightAboveGroundFeet"));
                break;
            case "remove":
                Remove(ReadId(root));
                break;
            case "duplicate":
                Duplicate(ReadId(root));
                break;
            case "target":
                Target(ReadId(root));
                break;
            case "reset":
                ResetWaypoints();
                break;
            case "clear":
                Clear();
                break;
            case "toggleRepeating":
                ToggleRepeating();
                break;
            default:
                throw new ArgumentException(null, nameof(json));
        }

        return GetWaypoints();
    }

    private int FindIndex(long id)
    {
        int index = waypoints.FindIndex(waypoint => waypoint.Id == id);
        if (index < 0)
            throw new ArgumentOutOfRangeException(nameof(id), id, null);

        return index;
    }

    private static bool IsActive(WaypointSnapshot waypoint) => waypoint.ActivatedAt.HasValue && !waypoint.Completed;

    private void Activate(int index) => waypoints[index] = waypoints[index] with
    {
        ActivatedAt = DateTimeOffset.UtcNow, Completed = false
    };

    private void ResetCore()
    {
        for (int i = 0; i < waypoints.Count; i++)
            waypoints[i] = waypoints[i] with { ActivatedAt = null, Completed = false };
        if (waypoints.Count > 0)
            Activate(0);
    }

    private void Commit()
    {
        Recompute();
        revision = checked(revision + 1);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Recompute()
    {
        for (int i = 0; i < waypoints.Count; i++)
        {
            WaypointSnapshot waypoint = waypoints[i];
            int previous = i > 0 ? i - 1 : repeating ? waypoints.Count - 1 : -1;
            int next = i + 1 < waypoints.Count ? i + 1 : repeating ? 0 : -1;
            double distance = previous < 0 ? 0.0 : DistanceKilometers(
                waypoints[previous].Latitude, waypoints[previous].Longitude, waypoint.Latitude, waypoint.Longitude) / 1.852;
            double? heading = next < 0 ? null : NormalizeHeading(Math.Round(Bearing(waypoint.Latitude,
                waypoint.Longitude, waypoints[next].Latitude, waypoints[next].Longitude), 2));
            waypoints[i] = waypoint with
            {
                Number = i + 1, DistanceNauticalMiles = distance,
                NextId = next < 0 ? null : waypoints[next].Id, HeadingDegrees = heading
            };
        }
    }

    private static void ValidatePosition(double latitude, double longitude)
    {
        if (!double.IsFinite(latitude) || latitude < -90.0 || latitude > 90.0)
            throw new ArgumentOutOfRangeException(nameof(latitude));
        if (!double.IsFinite(longitude) || longitude < -180.0 || longitude > 180.0)
            throw new ArgumentOutOfRangeException(nameof(longitude));
    }

    private static double? NormalizeElevation(double? altitudeFeet)
    {
        if (altitudeFeet.HasValue && !double.IsFinite(altitudeFeet.Value))
            throw new ArgumentOutOfRangeException(nameof(altitudeFeet));

        return altitudeFeet > 0.0 ? altitudeFeet : null;
    }

    private static double NormalizeHeading(double value) => (value % 360.0 + 360.0) % 360.0;

    private static double DistanceKilometers(double latitude, double longitude, double targetLatitude, double targetLongitude)
    {
        double phi = latitude * Math.PI / 180.0;
        double targetPhi = targetLatitude * Math.PI / 180.0;
        double halfLatitude = (targetPhi - phi) / 2.0;
        double halfLongitude = (targetLongitude - longitude) * Math.PI / 360.0;
        double a = Math.Clamp(Math.Pow(Math.Sin(halfLatitude), 2) +
            Math.Cos(phi) * Math.Cos(targetPhi) * Math.Pow(Math.Sin(halfLongitude), 2), 0.0, 1.0);

        return EarthRadiusKilometers * 2.0 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1.0 - a));
    }

    private static double Bearing(double latitude, double longitude, double targetLatitude, double targetLongitude)
    {
        double phi = latitude * Math.PI / 180.0;
        double targetPhi = targetLatitude * Math.PI / 180.0;
        double delta = (targetLongitude - longitude) * Math.PI / 180.0;
        double y = Math.Sin(delta) * Math.Cos(targetPhi);
        double x = Math.Cos(phi) * Math.Sin(targetPhi) - Math.Sin(phi) * Math.Cos(targetPhi) * Math.Cos(delta);

        return NormalizeHeading(Math.Atan2(y, x) * 180.0 / Math.PI);
    }

    private static string ReadString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.String)
            throw new ArgumentException(null, nameof(root));

        return value.GetString() ?? string.Empty;
    }

    private static double ReadDouble(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Number ||
            !value.TryGetDouble(out double number) || !double.IsFinite(number))
            throw new ArgumentException(null, nameof(root));

        return number;
    }

    private static long ReadId(JsonElement root)
    {
        if (!root.TryGetProperty("id", out JsonElement value))
            throw new ArgumentException(null, nameof(root));
        // Strings erhalten die volle long-Genauigkeit beim Austausch mit JavaScript.
        if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(),
            NumberStyles.None, CultureInfo.InvariantCulture, out long textId) && textId > 0)
            return textId;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long numberId) && numberId > 0)
            return numberId;

        throw new ArgumentException(null, nameof(root));
    }

    private static double? ReadElevation(JsonElement root, bool required, string name = "altitudeFeet")
    {
        if (!root.TryGetProperty(name, out JsonElement value))
        {
            if (required)
                throw new ArgumentException(null, nameof(root));

            return null;
        }
        if (value.ValueKind == JsonValueKind.Null)
            return null;

        return ReadDouble(root, name);
    }
}

public partial class AutoflyMain
{
    private const string WaypointSource = "http://autofly.local/map.html";
    private static readonly JsonSerializerOptions waypointExportOptions = new() { WriteIndented = true };
    private readonly WaypointManager waypointManager = new();
    private readonly HttpClient waypointElevationHttpClient = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly CancellationTokenSource waypointElevationCancellation = new();
    private readonly SemaphoreSlim waypointElevationGate = new(2);
    private readonly Dictionary<long, (double Latitude, double Longitude)> waypointElevationRequests = [];
    private bool waypointBridgeAttached;
    private bool waypointClientReady;
    private bool waypointClientInstalling;
    private bool waypointCommandInProgress;
    private ulong waypointNavigationId;

    private void InitializeWaypointControls()
    {
        tsbClear.Click += TsbClearWaypoints_Click;
        tsbReset.Click += TsbResetWaypoints_Click;
        tsbSave.Click += TsbSaveWaypoints_Click;
        TsbLoad.Click += TsbLoadWaypoints_Click;
    }

    private void SetWaypointFollowing(bool enabled)
    {
        enabled = enabled && !IsAutostartActive;
        _isWayPointFollow = enabled;
        TsbFollow.BackColor = enabled ? System.Drawing.Color.LightGreen : System.Drawing.Color.LightGray;
    }

    private void TsbFollow_Click(object? sender, EventArgs e)
    {
        if (IsAutostartActive)
            return;
        if (_isWayPointFollow)
        {
            SetWaypointFollowing(false);
            return;
        }
        if (autopilot == null || simconnect == null || waypointManager.GetWaypoints().Count == 0)
            return;
        if (!TryGetWaypointTelemetry(Flight_Data.INDICATED_ALTITUDE.Value, out _))
            return;
        if (!terrainFollow.Enabled)
            GetCurrALT_Click(TsbFollow, e);
        if (!waypointManager.GetWaypoints().Any(point => point.ActivatedAt.HasValue && !point.Completed))
            waypointManager.ResetWaypoints();
        if (!_isLocalAutopilotActive)
        {
            autopilot.DisablePlaneAutopilot();
            autopilot.AutopilotLocalOnOff(true);
            _isLocalAutopilotActive = true;
            tsbAP.BackColor = System.Drawing.Color.LightGreen;
        }
        autopilot.LvlOnOff(true);
        autopilot.HdgOnOff(true);
        autopilot.AltOnOff(true);
        _isLVLActive = true;
        _isHDGActive = true;
        _isALTActive = true;
        TsbLVL.BackColor = System.Drawing.Color.LightGreen;
        tsbHDG.BackColor = System.Drawing.Color.LightGreen;
        tsbALT.BackColor = System.Drawing.Color.LightGreen;
        SetWaypointFollowing(true);
    }

    private static string WaypointText(string key) =>
        Resources_DE.GetString("Waypoint." + key);

    private void TsbClearWaypoints_Click(object? sender, EventArgs e)
    {
        if (MessageBox.Show(this, Resources_DE.GetString("Waypoint_ConfirmClear"), WaypointText("WaypointDialogTitle"),
            MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;
        SetWaypointFollowing(false);
        waypointManager.Clear();
        if (waypointManager.IsRepeating)
            waypointManager.ToggleRepeating();
    }

    private void TsbResetWaypoints_Click(object? sender, EventArgs e) => waypointManager.ResetWaypoints();

    private async void TsbLoadWaypoints_Click(object? sender, EventArgs e)
    {
        if (!TsbLoad.Enabled)
            return;
        using OpenFileDialog dialog = new()
        {
            Title = WaypointText("WaypointLoadTitle"),
            Filter = WaypointText("WaypointLoadFilter"),
            DefaultExt = "json",
            CheckFileExists = true,
            Multiselect = false,
            RestoreDirectory = true
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;
        TsbLoad.Enabled = false;
        try
        {
            string json = await System.IO.File.ReadAllTextAsync(dialog.FileName);
            if (IsDisposed || Disposing)
                return;
            waypointManager.LoadWaypoints(json);
            SetWaypointFollowing(false);
        }
        catch (System.IO.IOException ex)
        {
            ReportWaypointLoadError(ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            ReportWaypointLoadError(ex);
        }
        catch (JsonException ex)
        {
            ReportWaypointLoadError(ex);
        }
        catch (ArgumentException ex)
        {
            ReportWaypointLoadError(ex);
        }
        finally
        {
            if (!IsDisposed && !Disposing)
                TsbLoad.Enabled = true;
        }
    }

    private void ReportWaypointLoadError(Exception error)
    {
        LogWaypointError(error);
        if (!IsDisposed && !Disposing)
            MessageBox.Show(this, WaypointText("WaypointLoadError"), WaypointText("WaypointLoadTitle"),
                MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    private async void TsbSaveWaypoints_Click(object? sender, EventArgs e)
    {
        using SaveFileDialog dialog = new()
        {
            Title = WaypointText("WaypointSaveTitle"),
            Filter = WaypointText("WaypointSaveFilter"),
            FileName = Resources_DE.GetString("Waypoint_DefaultFileName"),
            DefaultExt = "json",
            AddExtension = true,
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;
        string json = JsonSerializer.Serialize(waypointManager.GetWaypoints().Select(point => new
        {
            lat = point.Latitude,
            @long = point.Longitude,
            alt = point.AltitudeFeet.HasValue ? (object)point.AltitudeFeet.Value : false,
            heightAboveGroundFeet = point.HeightAboveGroundFeet,
            groundElevationFeet = point.GroundElevationFeet
        }), waypointExportOptions);
        try
        {
            await System.IO.File.WriteAllTextAsync(dialog.FileName, json);
        }
        catch (System.IO.IOException ex)
        {
            LogWaypointError(ex);
            MessageBox.Show(this, WaypointText("WaypointSaveError"), WaypointText("WaypointDialogTitle"),
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch (UnauthorizedAccessException ex)
        {
            LogWaypointError(ex);
            MessageBox.Show(this, WaypointText("WaypointSaveError"), WaypointText("WaypointDialogTitle"),
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // Nach EnsureCoreWebView2Async und vor Navigate aufrufen; erneute Aufrufe melden keine Handler doppelt an.
    private void InitializeWaypointNavigation()
    {
        if (waypointBridgeAttached)
            return;
        CoreWebView2 core = webView2.CoreWebView2 ?? throw new InvalidOperationException();
        core.NavigationStarting += WaypointNavigationStarting;
        core.NavigationCompleted += WaypointNavigationCompleted;
        core.WebMessageReceived += WaypointWebMessageReceived;
        waypointManager.Changed += WaypointManagerChanged;
        FormClosed += (_, _) =>
        {
            waypointElevationCancellation.Cancel();
            waypointElevationHttpClient.Dispose();
            waypointElevationCancellation.Dispose();
        };
        waypointBridgeAttached = true;
    }

    private void WaypointNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        waypointClientReady = false;
        waypointClientInstalling = false;
        _lastSentLat = null;
        _lastSentLon = null;
        waypointNavigationId = e.NavigationId;
    }

    private async void WaypointNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess || e.NavigationId != waypointNavigationId || IsDisposed || Disposing)
            return;
        try
        {
            CoreWebView2? core = webView2.CoreWebView2;
            if (core == null || !string.Equals(core.Source, WaypointSource, StringComparison.Ordinal))
                return;
            string labels = GetWaypointLabels();
            waypointClientInstalling = true;
            string installed = await core.ExecuteScriptAsync(ClientScript.Replace("__LABELS__", labels, StringComparison.Ordinal));
            if (IsDisposed || Disposing || e.NavigationId != waypointNavigationId ||
                !string.Equals(core.Source, WaypointSource, StringComparison.Ordinal))
                return;
            waypointClientInstalling = false;
            if (!string.Equals(installed, "true", StringComparison.Ordinal))
                throw new InvalidOperationException();
            waypointClientReady = true;
            await UpdateWaypointLanguageAsync();
            if (IsDisposed || Disposing || e.NavigationId != waypointNavigationId)
                return;
            // Auch im Stand senden: CHANGED liefert nach dem Laden ggf. keine neue Position.
            SendPlanePositionToMap();
            PushWaypointState();
        }
        catch (COMException ex)
        {
            if (e.NavigationId == waypointNavigationId)
            {
                waypointClientReady = false;
                waypointClientInstalling = false;
            }
            LogWaypointError(ex);
        }
        catch (InvalidOperationException ex)
        {
            if (e.NavigationId == waypointNavigationId)
            {
                waypointClientReady = false;
                waypointClientInstalling = false;
            }
            LogWaypointError(ex);
        }
    }

    private static string GetWaypointLabels() => JsonSerializer.Serialize(new
    {
        waypoint = WaypointText("WaypointLabel"),
        altitudeSuffix = WaypointText("WaypointAltitudeSuffix"),
        altitudeMissing = WaypointText("WaypointAltitudeMissing"),
        altitudeHelp = WaypointText("WaypointAltitudeHelp"),
        distanceSuffix = WaypointText("WaypointDistanceSuffix"),
        groundSource = WaypointText("WaypointGroundSource"),
        heightLabel = WaypointText("WaypointHeightAboveGroundLabel"),
        heightHelp = WaypointText("WaypointHeightHelp"),
        heightInvalid = WaypointText("WaypointHeightInvalid"),
        heightTransferFailed = WaypointText("WaypointHeightTransferFailed"),
        heightMissingTelemetry = WaypointText("WaypointHeightMissingTelemetry"),
        save = WaypointText("WaypointHeightSave"),
        cancel = WaypointText("WaypointHeightCancel"),
        remove = WaypointText("WaypointRemove"),
        removeFailed = WaypointText("WaypointRemoveFailed"),
        skipToHere = WaypointText("RelocationSkipToHere"),
        execute = WaypointText("RelocationExecute"),
        relocationHelp = WaypointText("RelocationHelp"),
        relocationBusy = WaypointText("RelocationBusy")
    });

    private async Task UpdateWaypointLanguageAsync()
    {
        if (!waypointClientReady || IsDisposed || Disposing)
            return;

        CoreWebView2? core = webView2.CoreWebView2;
        if (core == null || !string.Equals(core.Source, WaypointSource, StringComparison.Ordinal))
            return;

        await core.ExecuteScriptAsync($"window.setAutoFlyWaypointLabels({GetWaypointLabels()});");
    }

    private async void WaypointWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (IsDisposed || Disposing)
            return;
        string? requestId = null;
        try
        {
            CoreWebView2? core = webView2.CoreWebView2;
            if (core == null || !string.Equals(e.Source, WaypointSource, StringComparison.Ordinal) ||
                !string.Equals(core.Source, WaypointSource, StringComparison.Ordinal))
                return;
            string json = e.WebMessageAsJson;
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            // Andere Anwendungskanäle gehören nicht zu dieser Brücke.
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("channel", out JsonElement channel) ||
                channel.ValueKind != JsonValueKind.String || channel.GetString() != "autoFly.waypoints")
                return;
            if (root.TryGetProperty("requestId", out JsonElement request) &&
                request.ValueKind == JsonValueKind.String && request.GetString() is string token && token.Length <= 128)
                requestId = token;
            if (!waypointClientReady && !(waypointClientInstalling &&
                root.TryGetProperty("action", out JsonElement action) &&
                action.ValueKind == JsonValueKind.String && action.GetString() == "list"))
                return;
            if (root.TryGetProperty("action", out JsonElement command) && command.GetString() == "skipTo")
            {
                if (!root.TryGetProperty("lat", out JsonElement lat) || !lat.TryGetDouble(out double latitude) ||
                    !root.TryGetProperty("lng", out JsonElement lng) || !lng.TryGetDouble(out double longitude))
                    throw new ArgumentException(Resources_DE.GetString("Waypoint_InvalidMessageCoordinates"), nameof(e));
                string? error = IsAutostartActive ? AutostartText("RelocationBlocked") :
                    await RelocateAircraftAsync(latitude, longitude);
                if (!IsDisposed && !Disposing && ReferenceEquals(core, webView2.CoreWebView2) &&
                    string.Equals(core.Source, WaypointSource, StringComparison.Ordinal))
                    core.PostWebMessageAsJson(JsonSerializer.Serialize(new
                    {
                        version = 1, channel = "autoFly.waypoints", action = "relocationResult",
                        requestId, accepted = error == null, error
                    }));
                return;
            }
            waypointCommandInProgress = true;
            try
            {
                double? initialDistance = simconnect != null &&
                    TryGetWaypointTelemetry(Flight_Data.PLANE_ALT_ABOVE_GROUND.Value, out double currentDistance) &&
                    currentDistance > 0.0 ? currentDistance : null;
                waypointManager.ProcessCommand(json, initialDistance);
            }
            finally
            {
                waypointCommandInProgress = false;
            }
            waypointClientReady = true;
            PushWaypointState(requestId);
        }
        catch (JsonException ex)
        {
            LogWaypointError(ex);
            PushWaypointState(requestId, false);
        }
        catch (ArgumentException ex)
        {
            LogWaypointError(ex);
            PushWaypointState(requestId, false);
        }
        catch (COMException ex)
        {
            waypointClientReady = false;
            LogWaypointError(ex);
        }
        catch (InvalidOperationException ex)
        {
            waypointClientReady = false;
            LogWaypointError(ex);
        }
    }

    private void WaypointManagerChanged(object? sender, EventArgs e)
    {
        if (!waypointManager.GetWaypoints().Any(point => point.ActivatedAt.HasValue && !point.Completed))
            SetWaypointFollowing(false);
        if (!waypointCommandInProgress)
            PushWaypointState();
        QueueWaypointElevations();
    }

    private void QueueWaypointElevations()
    {
        if (IsDisposed || Disposing)
            return;
        IReadOnlyList<WaypointSnapshot> points = waypointManager.GetWaypoints();
        foreach (long id in waypointElevationRequests.Keys.Where(id => !points.Any(point => point.Id == id)).ToArray())
            waypointElevationRequests.Remove(id);
        foreach (WaypointSnapshot point in points)
        {
            if (!point.HeightAboveGroundFeet.HasValue || point.GroundElevationFeet.HasValue)
                continue;
            var position = (point.Latitude, point.Longitude);
            if (waypointElevationRequests.TryGetValue(point.Id, out var requested) && requested == position)
                continue;
            // Auch nach Fehlern nicht bei jedem Telemetrie-/Routenereignis erneut anfragen.
            waypointElevationRequests[point.Id] = position;
            _ = FetchWaypointElevationAsync(point, waypointElevationCancellation.Token);
        }
    }

    private async Task FetchWaypointElevationAsync(WaypointSnapshot point, CancellationToken cancellationToken)
    {
        bool entered = false;
        try
        {
            await waypointElevationGate.WaitAsync(cancellationToken);
            entered = true;
            if (!waypointManager.GetWaypoints().Any(current => current.Id == point.Id &&
                current.Latitude == point.Latitude && current.Longitude == point.Longitude &&
                current.HeightAboveGroundFeet.HasValue && !current.GroundElevationFeet.HasValue))
                return;
            ElevationClient client = new(waypointElevationHttpClient);
            double elevation = await client.GetElevationFeetAsync(point.Latitude, point.Longitude, cancellationToken);
            if (!IsDisposed && !Disposing && !cancellationToken.IsCancellationRequested)
                waypointManager.TrySetGroundElevation(point.Id, point.Latitude, point.Longitude, elevation);
        }
        catch (OperationCanceledException ex)
        {
            if (!cancellationToken.IsCancellationRequested)
                LogWaypointError(ex);
        }
        catch (HttpRequestException ex)
        {
            LogWaypointError(ex);
        }
        catch (JsonException ex)
        {
            LogWaypointError(ex);
        }
        catch (System.IO.InvalidDataException ex)
        {
            LogWaypointError(ex);
        }
        catch (System.IO.IOException ex)
        {
            LogWaypointError(ex);
        }
        catch (ArgumentException ex)
        {
            LogWaypointError(ex);
        }
        finally
        {
            if (entered)
                waypointElevationGate.Release();
        }
    }

    private void PushWaypointState(string? requestId = null, bool accepted = true)
    {
        if (!waypointClientReady || IsDisposed || Disposing)
            return;
        try
        {
            CoreWebView2? core = webView2.CoreWebView2;
            if (core == null || !string.Equals(core.Source, WaypointSource, StringComparison.Ordinal))
                return;
            string json = JsonSerializer.Serialize(new
            {
                version = 1,
                channel = "autoFly.waypoints",
                action = "state",
                revision = waypointManager.Revision.ToString(CultureInfo.InvariantCulture),
                requestId,
                accepted,
                repeating = waypointManager.IsRepeating,
                waypoints = waypointManager.GetWaypoints().Select(waypoint => new
                {
                    id = waypoint.Id.ToString(CultureInfo.InvariantCulture),
                    number = waypoint.Number,
                    latitude = waypoint.Latitude,
                    longitude = waypoint.Longitude,
                    altitudeFeet = waypoint.AltitudeFeet,
                    heightAboveGroundFeet = waypoint.HeightAboveGroundFeet,
                    groundElevationFeet = waypoint.GroundElevationFeet,
                    activatedAt = waypoint.ActivatedAt,
                    completed = waypoint.Completed,
                    distanceNauticalMiles = waypoint.DistanceNauticalMiles,
                    nextId = waypoint.NextId?.ToString(CultureInfo.InvariantCulture),
                    headingDegrees = waypoint.HeadingDegrees
                })
            });
            core.PostWebMessageAsJson(json);
        }
        catch (COMException ex)
        {
            waypointClientReady = false;
            LogWaypointError(ex);
        }
        catch (InvalidOperationException ex)
        {
            waypointClientReady = false;
            LogWaypointError(ex);
        }
    }

    // Unmittelbar vor autopilot.ProcessCurrentValues aufrufen; schaltet keine Regelungsfunktion ein.
    private void ApplyWaypointNavigation()
    {
        if (IsAutostartActive || !_isWayPointFollow || autopilot == null || !_isLocalAutopilotActive ||
            (!(_isLVLActive && _isHDGActive) && !_isALTActive))
            return;
        if (!TryGetWaypointTelemetry(Flight_Data.PLANE_LATITUDE.Value, out double latitude) ||
            !TryGetWaypointTelemetry(Flight_Data.PLANE_LONGITUDE.Value, out double longitude) ||
            !TryGetWaypointTelemetry(Flight_Data.PLANE_HEADING_DEGREES_TRUE.Value, out double trueHeading) ||
            !TryGetWaypointTelemetry(Flight_Data.PLANE_HEADING_DEGREES_MAGNETIC.Value, out double magneticHeading) ||
            !TryGetWaypointTelemetry(Flight_Data.AIRSPEED_INDICATED.Value, out double speedKnots) ||
            !TryGetWaypointTelemetry(Flight_Model.DESIGN_SPEED_VS1.Value, out double stallSpeedKnots) ||
            !TryGetWaypointTelemetry(Flight_Model.DESIGN_SPEED_VC.Value, out double cruiseSpeedKnots) ||
            speedKnots < 0.0 || stallSpeedKnots <= 0.0 || cruiseSpeedKnots <= stallSpeedKnots ||
            latitude < -90.0 || latitude > 90.0 || longitude < -180.0 || longitude > 180.0)
            return;
        double declination = (((trueHeading % 360.0 - magneticHeading % 360.0) % 360.0 + 540.0) % 360.0) - 180.0;
        WaypointNavigation? navigation = waypointManager.GetNavigation(latitude, longitude, declination,
            speedKnots, stallSpeedKnots, cruiseSpeedKnots);
        if (navigation == null)
            return;
        if (_isLVLActive && _isHDGActive)
        {
            _targetHeading = RoundTargetHeading(navigation.MagneticHeadingDegrees);
            autopilot.LocalTargetHdgDeg = _targetHeading;
            tstbHeading.Text = _targetHeading.ToString(CultureInfo.InvariantCulture);
        }
        if (_isALTActive && !terrainFollow.Enabled)
        {
            // Die Zielhöhe stammt vom Gelände am Wegpunkt, nicht vom Gelände unter dem Flugzeug.
            if (navigation.AltitudeFeet is double targetAltitude)
            {
                _targetAltitude = RoundTargetAltitude(targetAltitude);
                autopilot.localTargetAltDeg = _targetAltitude;
                tstbAltitude.Text = _targetAltitude.ToString(CultureInfo.InvariantCulture);
            }
        }
    }

    private static bool TryGetWaypointTelemetry(object? value, out double number)
    {
        number = value switch
        {
            double d => d,
            float f => f,
            decimal d => (double)d,
            byte b => b,
            sbyte b => b,
            short s => s,
            ushort s => s,
            int i => i,
            uint i => i,
            long l => l,
            ulong l => l,
            string text when double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) => parsed,
            _ => double.NaN
        };

        return double.IsFinite(number);
    }

    private static void LogWaypointError(Exception error) =>
        Debug.WriteLine($"[Debug] {DateTime.Now:yyyy-MM-dd HH:mm:ss} - [Waypoint] {error.GetType().Name}: {error.Message}");

    private const string ClientScript = """
        (() => {
          'use strict';
          if (window.__autoFlyWaypointsInstalled) return true;
          if (typeof map === 'undefined' || typeof L === 'undefined' || !window.chrome?.webview) return false;
          const labels = __LABELS__;
          const channel = 'autoFly.waypoints';
          const markers = new Map();
          const lines = new Map();
          const dragging = new Set();
          const pending = new Map();
          let lastRevision = -1n;
          let lastState = null;
          let sequence = 0n;
          let heightEditor = null;

          const waypointPane = map.createPane('autoFlyWaypoints');
          waypointPane.style.zIndex = '550';

          const style = document.createElement('style');
          style.textContent = `
            .leaflet-container.leaflet-grab, .leaflet-container.leaflet-grabbing, .af-waypoint-pin {
              cursor:url("data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' width='32' height='32' viewBox='0 0 32 32'%3E%3Cpath d='M16 3V29M3 16H29' fill='none' stroke='white' stroke-width='4'/%3E%3Cpath d='M16 3V29M3 16H29' fill='none' stroke='black' stroke-width='2'/%3E%3C/svg%3E") 16 16, crosshair !important;
            }
            .af-waypoint-icon { background:transparent; border:0; }
            .af-waypoint-pin { position:relative; width:40px; height:40px; color:white; }
            .af-waypoint-image { position:absolute; left:0; top:0; width:40px; height:40px;
              object-fit:contain; pointer-events:none; user-select:none; }
            .af-waypoint-pin.active .af-waypoint-image { filter:hue-rotate(145deg) brightness(2); }
            .af-waypoint-pin.completed .af-waypoint-image { filter:hue-rotate(-45deg); }
            .af-waypoint-number { position:absolute; left:4px; top:0px; width:32px;
              color:black; text-align:center; font:bold 13px/28px sans-serif; }
            .af-waypoint-distance, .af-waypoint-height { position:absolute; left:44px; top:8px; white-space:nowrap;
              color:#111; background:rgba(255,255,255,.3); border-radius:3px; padding:1px 4px;
              font:12px/18px sans-serif; pointer-events:none; }
            .af-waypoint-height { top:25px; }
            .af-waypoint-dialog { margin:auto; padding:20px; border:1px solid #777; border-radius:8px;
              width:min(360px, calc(100vw - 64px)); max-height:calc(100vh - 64px); overflow:auto;
              color:#111; background:white; font:14px/1.5 sans-serif; }
            .af-waypoint-dialog::backdrop { background:rgba(0,0,0,.45); }
            .af-waypoint-dialog h3 { margin:0 0 12px; }
            .af-waypoint-dialog label, .af-waypoint-dialog input { display:block; }
            .af-waypoint-dialog input { width:100%; box-sizing:border-box; padding:6px; margin:6px 0; }
            .af-waypoint-dialog p { white-space:normal; }
            .af-waypoint-dialog .af-height-error { color:#a40000; }
            .af-waypoint-dialog .af-height-buttons { display:flex; flex-wrap:wrap; justify-content:flex-end; gap:8px; }
            .af-waypoint-dialog .af-height-remove { margin-right:auto; }
            .af-waypoint-dialog button { padding:6px 12px; }
          `;
          document.head.appendChild(style);

          function send(action, values = {}) {
            window.chrome.webview.postMessage({ ...values, version:1, channel, action });
          }

          function closeHeightEditor() {
            if (!heightEditor) return;
            const { dialog } = heightEditor;
            heightEditor = null;
            dialog.close();
            dialog.remove();
          }

          function commitHeightEditor() {
            if (!heightEditor || heightEditor.requestId !== null) return;
            const { input, error } = heightEditor;
            input.setCustomValidity('');
            const empty = input.value.trim() === '';
            const distance = empty ? null : input.valueAsNumber;
            if (input.validity.badInput || (!empty && (!Number.isFinite(distance) || distance <= 0))) {
              input.setCustomValidity(labels.heightInvalid);
              error.textContent = labels.heightInvalid;
              input.reportValidity();
              return;
            }
            sendHeightEditorAction('setHeightAboveGround', { heightAboveGroundFeet:distance });
          }

          function sendHeightEditorAction(action, values = {}) {
            if (!heightEditor || heightEditor.requestId !== null) return;
            const requestId = `${action}-${++sequence}`;
            heightEditor.requestId = requestId;
            heightEditor.requestAction = action;
            heightEditor.input.disabled = true;
            heightEditor.save.disabled = true;
            heightEditor.removeButton.disabled = true;
            heightEditor.error.textContent = '';
            send(action, { ...values, id:heightEditor.id, requestId });
          }

          function showHeightEditor(id) {
            const waypoint = lastState?.waypoints.find(point => point.id === id);
            if (!waypoint || dragging.has(id) || pending.has(id)) return;
            closeHeightEditor();
            const dialog = document.createElement('dialog');
            dialog.className = 'af-waypoint-dialog';
            dialog.setAttribute('aria-labelledby', 'af-height-title');
            const form = document.createElement('form');
            form.noValidate = true;
            const title = document.createElement('h3');
            title.id = 'af-height-title';
            title.textContent = `${labels.waypoint} ${waypoint.number}`;
            const label = document.createElement('label');
            label.htmlFor = 'af-height-input';
            label.textContent = labels.heightLabel;
            const input = document.createElement('input');
            input.id = 'af-height-input';
            input.type = 'number';
            input.step = 'any';
            input.value = waypoint.heightAboveGroundFeet === null ? '' : String(waypoint.heightAboveGroundFeet);
            input.setAttribute('aria-describedby', 'af-height-help af-height-error');
            input.addEventListener('input', () => input.setCustomValidity(''));
            const help = document.createElement('p');
            help.id = 'af-height-help';
            help.textContent = waypoint.heightAboveGroundFeet === null && waypoint.altitudeFeet === null
              ? `${labels.heightMissingTelemetry} ${labels.heightHelp}` : labels.heightHelp;
            const error = document.createElement('p');
            error.id = 'af-height-error';
            error.className = 'af-height-error';
            error.setAttribute('role', 'alert');
            const buttons = document.createElement('div');
            buttons.className = 'af-height-buttons';
            const removeButton = document.createElement('button');
            removeButton.type = 'button';
            removeButton.className = 'af-height-remove';
            removeButton.textContent = labels.remove;
            removeButton.addEventListener('click', () => sendHeightEditorAction('remove'));
            const save = document.createElement('button');
            save.type = 'submit';
            save.textContent = labels.save;
            const cancel = document.createElement('button');
            cancel.type = 'button';
            cancel.textContent = labels.cancel;
            cancel.addEventListener('click', closeHeightEditor);
            buttons.append(removeButton, save, cancel);
            form.append(title, label, input, help, error, buttons);
            dialog.append(form);
            form.addEventListener('submit', event => {
              event.preventDefault();
              commitHeightEditor();
            });
            dialog.addEventListener('cancel', event => {
              event.preventDefault();
              closeHeightEditor();
            });
            dialog.addEventListener('keydown', event => event.stopPropagation());
            dialog.addEventListener('click', event => {
              event.stopPropagation();
              const rect = dialog.getBoundingClientRect();
              if (event.target === dialog && (event.clientX < rect.left || event.clientX > rect.right ||
                  event.clientY < rect.top || event.clientY > rect.bottom)) commitHeightEditor();
            });
            heightEditor = { id, dialog, input, error, save, removeButton, title, label, help, cancel,
              number:waypoint.number, missingTelemetry:waypoint.heightAboveGroundFeet === null && waypoint.altitudeFeet === null,
              requestId:null, requestAction:null };
            document.body.appendChild(dialog);
            dialog.showModal();
            input.focus();
            input.select();
          }

          function createMarker(waypoint) {
            const root = document.createElement('div');
            root.className = 'af-waypoint-pin';
            const image = document.createElement('img');
            image.className = 'af-waypoint-image';
            image.src = 'http://autofly.local/map_marker.png';
            image.alt = '';
            image.draggable = false;
            const number = document.createElement('span');
            number.className = 'af-waypoint-number';
            const distance = document.createElement('span');
            distance.className = 'af-waypoint-distance';
            const height = document.createElement('span');
            height.className = 'af-waypoint-height';
            root.append(image, number, distance, height);
            L.DomEvent.disableScrollPropagation(root);
            const marker = L.marker([waypoint.latitude, waypoint.longitude], {
              pane:'autoFlyWaypoints',
              draggable:true,
              bubblingMouseEvents:false,
              keyboard:true,
              icon:L.divIcon({ html:root, className:'af-waypoint-icon', iconSize:[40,40], iconAnchor:[20,40] })
            }).addTo(map);
            const entry = { marker, root, image, number, distance, height };
            marker.on('click dblclick contextmenu', event => {
              if (event.originalEvent) L.DomEvent.stopPropagation(event.originalEvent);
              if (event.type === 'click') showHeightEditor(waypoint.id);
            });
            marker.on('dragstart', () => {
              dragging.add(waypoint.id);
              pending.delete(waypoint.id);
            });
            marker.on('drag', renderLines);
            marker.on('dragend', () => {
              const point = marker.getLatLng();
              dragging.delete(waypoint.id);
              const requestId = `move-${++sequence}`;
              pending.set(waypoint.id, requestId);
              renderLines();
              send('move', { id:waypoint.id, lat:point.lat, lng:point.lng, requestId });
            });

            return entry;
          }

          function renderMarker(waypoint, entry) {
            if (dragging.has(waypoint.id) || pending.has(waypoint.id)) return;
            const point = entry.marker.getLatLng();
            if (point.lat !== waypoint.latitude || point.lng !== waypoint.longitude)
              entry.marker.setLatLng([waypoint.latitude, waypoint.longitude]);
            entry.root.classList.toggle('active', waypoint.activatedAt !== null && !waypoint.completed);
            entry.root.classList.toggle('completed', waypoint.completed);
            entry.number.textContent = String(waypoint.number);
            entry.distance.textContent = `${waypoint.distanceNauticalMiles.toFixed(2)} ${labels.distanceSuffix}`;
            renderWaypointHeight(waypoint, entry);
          }

          function renderWaypointHeight(waypoint, entry) {
            const altitude = waypoint.heightAboveGroundFeet !== null
              ? Number.isFinite(waypoint.groundElevationFeet) ? waypoint.groundElevationFeet + waypoint.heightAboveGroundFeet : null
              : waypoint.altitudeFeet;
            entry.height.textContent = Number.isFinite(altitude)
              ? `${altitude.toFixed(0)} ${labels.altitudeSuffix}` : labels.altitudeMissing;
            const source = waypoint.heightAboveGroundFeet !== null ? '\n' + labels.groundSource : '';
            const title = `${labels.waypoint} ${waypoint.number}: ${entry.height.textContent}\n${labels.altitudeHelp}${source}`;
            if (Number.isFinite(altitude)) {
              entry.root.setAttribute('data-altitude-feet', String(altitude));
            } else {
              entry.root.removeAttribute('data-altitude-feet');
            }
            entry.root.title = title;
            entry.root.setAttribute('aria-label', title);
            entry.image.alt = `${labels.waypoint} ${waypoint.number}`;
            const icon = entry.marker.getElement();
            if (icon) {
              icon.title = title;
              icon.setAttribute('aria-label', title);
              icon.setAttribute('aria-haspopup', 'dialog');
            }
          }

          function renderLines() {
            if (!lastState) return;
            const retained = new Set();
            for (const waypoint of lastState.waypoints) {
              if (waypoint.nextId === null || waypoint.nextId === waypoint.id) continue;
              const start = markers.get(waypoint.id);
              const end = markers.get(waypoint.nextId);
              if (!start || !end) continue;
              const key = `${waypoint.id}:${waypoint.nextId}`;
              retained.add(key);
              const points = [start.marker.getLatLng(), end.marker.getLatLng()];
              let line = lines.get(key);
              if (!line) {
                line = L.polyline(points, { color:'#2464bd', weight:2, interactive:false }).addTo(map);
                lines.set(key, line);
              } else {
                const old = line.getLatLngs();
                if (!old[0].equals(points[0], 0) || !old[1].equals(points[1], 0)) line.setLatLngs(points);
              }
            }
            for (const [key, line] of lines) {
              if (!retained.has(key)) {
                map.removeLayer(line);
                lines.delete(key);
              }
            }
          }

          function renderState() {
            if (!lastState) return;
            const retained = new Set(lastState.waypoints.map(waypoint => waypoint.id));
            if (heightEditor && !retained.has(heightEditor.id)) closeHeightEditor();
            for (const [id, entry] of markers) {
              if (!retained.has(id) && !dragging.has(id) && !pending.has(id)) {
                map.removeLayer(entry.marker);
                markers.delete(id);
              }
            }
            for (const waypoint of lastState.waypoints) {
              let entry = markers.get(waypoint.id);
              if (!entry) {
                entry = createMarker(waypoint);
                markers.set(waypoint.id, entry);
              }
              renderMarker(waypoint, entry);
            }
            renderLines();
          }

          window.chrome.webview.addEventListener('message', event => {
            const state = event.data;
            if (!state || state.version !== 1 || state.channel !== channel) return;
            if (state.action === 'relocationResult') {
              if (!relocationMenu || state.requestId !== relocationMenu.requestId) return;
              relocationMenu.busy = false;
              if (state.accepted) {
                map.closePopup(relocationMenu.popup);
                relocationPopup = null;
                relocationMenu = null;
                return;
              }
              relocationMenu.status.textContent = state.error;
              relocationMenu.status.style.color = '#a40000';
              relocationMenu.execute.disabled = false;
              relocationMenu.popup.update();
              return;
            }
            if (state.action !== 'state' ||
                typeof state.revision !== 'string' || !/^\d+$/.test(state.revision) || !Array.isArray(state.waypoints)) return;
            const revision = BigInt(state.revision);
            let acknowledged = false;
            if (typeof state.requestId === 'string') {
              for (const [id, requestId] of pending) {
                if (requestId === state.requestId) {
                  pending.delete(id);
                  acknowledged = true;
                }
              }
            }
            if (revision >= lastRevision) {
              lastRevision = revision;
              lastState = state;
              renderState();
            } else if (acknowledged) {
              // Eine verspätete Bestätigung darf nie einen neueren Vollzustand zurückrollen.
              renderState();
            }
            if (heightEditor && heightEditor.requestId !== null && heightEditor.requestId === state.requestId) {
              if (state.accepted) {
                closeHeightEditor();
              } else {
                heightEditor.requestId = null;
                heightEditor.input.disabled = false;
                heightEditor.save.disabled = false;
                heightEditor.removeButton.disabled = false;
                heightEditor.error.textContent = heightEditor.requestAction === 'remove'
                  ? labels.removeFailed : labels.heightTransferFailed;
                heightEditor.input.focus();
              }
            }
          });
          let relocationPopup = null;
          let relocationMenu = null;
          map.on('contextmenu', event => {
            if (heightEditor) return;
            L.DomEvent.preventDefault(event.originalEvent);
            if (relocationMenu?.busy) return;
            if (relocationPopup) map.closePopup(relocationPopup);
            const menu = document.createElement('div');
            menu.setAttribute('role', 'group');
            menu.setAttribute('aria-label', labels.skipToHere);
            menu.style.cssText = 'box-sizing:border-box;width:100%;padding:8px;font:16px/1.5 sans-serif;';
            const title = document.createElement('h3');
            title.style.cssText = 'margin:0 0 12px;font-size:19px;';
            title.textContent = labels.skipToHere;
            menu.appendChild(title);
            const coordinates = document.createElement('p');
            coordinates.textContent = `${event.latlng.lat.toFixed(6)}, ${event.latlng.lng.toFixed(6)}`;
            menu.appendChild(coordinates);
            const status = document.createElement('p');
            status.setAttribute('role', 'status');
            status.setAttribute('aria-live', 'polite');
            status.style.cssText = 'white-space:pre-line;overflow-wrap:anywhere;';
            status.textContent = labels.relocationHelp;
            menu.appendChild(status);
            const execute = document.createElement('button');
            execute.type = 'button';
            execute.textContent = labels.execute;
            execute.style.cssText = 'min-height:44px;min-width:140px;padding:10px 20px;font:inherit;cursor:pointer;';
            const entry = { menu, title, execute, status, requestId:null, busy:false, popup:null };
            execute.addEventListener('click', () => {
              execute.disabled = true;
              entry.busy = true;
              entry.requestId = `relocation-${++sequence}`;
              status.style.color = '';
              status.textContent = labels.relocationBusy;
              send('skipTo', { lat:event.latlng.lat, lng:event.latlng.lng, requestId:entry.requestId });
              entry.popup.update();
            });
            menu.appendChild(execute);
            L.DomEvent.disableClickPropagation(menu);
            L.DomEvent.disableScrollPropagation(menu);
            const width = Math.max(180, Math.min(360, map.getSize().x - 64));
            relocationPopup = L.popup({ minWidth:width, maxWidth:width, closeOnClick:false, autoClose:false })
              .setLatLng(event.latlng).setContent(menu).openOn(map);
            entry.popup = relocationPopup;
            relocationMenu = entry;
            execute.focus();
          });
          map.on('click', event => {
            if (relocationPopup) {
              if (relocationMenu?.busy) return;
              map.closePopup(relocationPopup);
              relocationPopup = null;
              return;
            }
            if (heightEditor) return;
            send('add', { lat:event.latlng.lat, lng:event.latlng.lng });
          });
          window.setAutoFlyWaypointLabels = newLabels => {
            const previousLabels = { ...labels };
            Object.assign(labels, newLabels);
            for (const waypoint of lastState?.waypoints ?? []) {
              const entry = markers.get(waypoint.id);
              if (entry) renderWaypointHeight(waypoint, entry);
            }
            if (heightEditor) {
              const editor = heightEditor;
              editor.title.textContent = `${labels.waypoint} ${editor.number}`;
              editor.label.textContent = labels.heightLabel;
              editor.help.textContent = editor.missingTelemetry
                ? `${labels.heightMissingTelemetry} ${labels.heightHelp}` : labels.heightHelp;
              editor.removeButton.textContent = labels.remove;
              editor.save.textContent = labels.save;
              editor.cancel.textContent = labels.cancel;
              for (const key of ['heightInvalid', 'heightTransferFailed', 'removeFailed']) {
                if (editor.error.textContent === previousLabels[key]) editor.error.textContent = labels[key];
              }
              if (editor.input.validity.customError) editor.input.setCustomValidity(labels.heightInvalid);
            }
            if (relocationMenu) {
              const menu = relocationMenu;
              menu.menu.setAttribute('aria-label', labels.skipToHere);
              menu.title.textContent = labels.skipToHere;
              menu.execute.textContent = labels.execute;
              if (menu.status.textContent === previousLabels.relocationHelp) menu.status.textContent = labels.relocationHelp;
              if (menu.status.textContent === previousLabels.relocationBusy) menu.status.textContent = labels.relocationBusy;
              menu.popup.update();
            }
            return true;
          };
          window.__autoFlyWaypointsInstalled = true;
          send('list');
          return true;
        })()
        """;
}


