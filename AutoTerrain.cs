using GeoTerrain.Storage;
using System.Globalization;
using System.Security;

namespace AutoFly;

/// <summary>Wartet bei Flugzeughöhe über der Gelände-Zielhöhe 30 Sekunden und führt den ALT-Sollwert über 60 Sekunden abwärts.</summary>
public sealed class TerrainFollow : IAsyncDisposable
{
    public const double HeightAboveGroundFeet = 2000.0;             // Abstand über dem Boden in Fuß, der als Zielhöhe verwendet wird.
    private const double DescentDelaySeconds = 30.0;                // Zeit in Sekunden, die vor einer Absenkung gewartet wird,
                                                                    // um kurzzeitige Geländeabsenkungen zu ignorieren.
    private const double DescentDurationSeconds = 60.0;             // Zeit in Sekunden, über die die Absenkung linear erfolgt,
                                                                    // um ein sanftes Absenken zu ermöglichen.
    private const string GeoPointsFileSearchPattern = "*.gtt";      // Dateisuche nach GeoPoints-Dateien im Programmverzeichnis;
                                                                    // die erste passende Datei wird ausgewählt.
    private static readonly bool geoPointsEnabled = true;           // Die erste passende Datei im Programmverzeichnis wird ausgewählt.
    private const double FileHeightAboveGroundFeet = 1000.0;        // Abstand über dem Boden in Fuß, der als Zielhöhe verwendet wird,
                                                                    // wenn eine GeoPoints-Datei verwendet wird.
    private const double FeetPerMeter = 1.0 / 0.3048;               // Umrechnung von Metern in Fuß.
    private readonly System.Threading.Lock stateGate = new();       // Sperre für den Zugriff auf den internen Zustand,
                                                                    // der von mehreren Threads gelesen und geschrieben wird.
    private readonly TimeProvider timeProvider;                     // Monotone Zeitquelle für die Absenkberechnung.
    private Task ioTask = Task.CompletedTask;                       // Aufgabe für asynchrone E/A-Operationen. 
    private bool readInProgress;                                    // Gibt an, ob eine Leseoperation im Gange ist,
                                                                    // um parallele Leseanforderungen zu verhindern.
    private bool disposed;                                          // Gibt an, ob die Instanz bereits verworfen wurde,
                                                                    // um weitere Operationen zu verhindern.
    private long version;                                           // Gibt die aktuelle Version der Höhenquelle an,
                                                                    // die bei Änderungen inkrementiert wird,
                                                                    // um veraltete Ergebnisse zu erkennen.
    private string? activeFilePath;                                 // Pfad der aktuell verwendeten GeoPoints-Datei,
                                                                    // die für die Höhenberechnung verwendet wird.
    private TransformedTerrainReader? reader;                       // Reader für die GeoPoints-Datei, der die Höhenabfrage durchführt.
    private string? readerPath;                                     // Pfad der aktuell geöffneten GeoPoints-Datei im Reader,
                                                                    // um zu erkennen, ob ein Reader neu geöffnet werden muss.
    private long readerVersion;                                     // Version der Höhenquelle, die beim Öffnen des Readers verwendet wurde,
                                                                    // um zu erkennen, ob der Reader veraltet ist.
    private bool fileSearchCompleted;                               // Gibt an, ob die Suche nach einer GeoPoints-Datei abgeschlossen ist,
                                                                    // um zu verhindern, dass die Suche erneut gestartet wird.
    private bool fileSearchFailed;                                  // Gibt an, ob die Suche nach einer GeoPoints-Datei fehlgeschlagen ist,
                                                                    // um zu verhindern, dass die Suche erneut gestartet wird.
    private double? targetGroundAltitudeFeet;                       // Zielhöhe über dem Boden in Fuß, die von der Höhenquelle berechnet wird.
    private long? descentRequestedTimestamp;                        // Zeitstempel der letzten Anforderung einer Absenkung,
                                                                    // um die Absenkberechnung zu steuern.
    private double lastDescentElapsedSeconds;                       // Zeit in Sekunden, die seit der letzten Absenkberechnung vergangen ist,
                                                                    // um die Absenkberechnung zu steuern.
    private double? requestedTargetAltitudeFeet;                    // Gelände-/Dateihöhe plus Bodenabstand, vor der zeitlichen Führung.
    public bool Enabled { get; private set; }                       // Schaltet die Höhenquelle ein oder aus.

    /// <summary>Erstellt eine Geländeführung mit der monotonen Systemzeit.</summary>
    public TerrainFollow() : this(TimeProvider.System)
    {
    }

    /// <summary>Erstellt eine Geländeführung mit austauschbarer monotoner Zeitquelle.</summary>
    public TerrainFollow(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        this.timeProvider = timeProvider;
    }

    internal static bool GeoPointsEnabled => geoPointsEnabled;
    internal string? GeoPointsFilePath { get; private set; }
    internal bool UsesGeoPointsFile => geoPointsEnabled &&
        (!fileSearchCompleted || fileSearchFailed || GeoPointsFilePath is not null);
    internal long Version { get { lock (stateGate) return version; } }
    internal double? RequestedTargetAltitudeFeet { get { lock (stateGate) return requestedTargetAltitudeFeet; } }

    internal async Task FindGeoPointsFileAsync(CancellationToken cancellationToken)
    {
        if (!geoPointsEnabled)
            return;

        string? path = null;
        bool failed = false;
        try
        {
            path = await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                string? found = Directory.EnumerateFiles(AppContext.BaseDirectory, GeoPointsFileSearchPattern)
                    .FirstOrDefault();
                cancellationToken.ThrowIfCancellationRequested();

                return found;
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException) { failed = true; }
        catch (UnauthorizedAccessException) { failed = true; }
        catch (SecurityException) { failed = true; }

        lock (stateGate)
        {
            if (disposed || cancellationToken.IsCancellationRequested)
                return;
            if (GeoPointsFilePath != path || fileSearchFailed != failed || !fileSearchCompleted)
            {
                version++;
                ResetTarget();
                ioTask = ReleaseReaderAfterAsync(ioTask);
            }
            GeoPointsFilePath = path;
            activeFilePath = path;
            fileSearchFailed = failed;
            fileSearchCompleted = true;
        }
    }

    /// <summary>Schaltet die Höhenquelle um; die erste gültige Position liefert sofort eine Zielhöhe.</summary>
    public void SetEnabled(bool enabled)
    {
        lock (stateGate)
        {
            if (disposed)
                return;
            Enabled = enabled;
            version++;
            ResetTarget();
            ioTask = ReleaseReaderAfterAsync(ioTask);
        }
    }

    private void ResetTarget()
    {
        targetGroundAltitudeFeet = null;
        requestedTargetAltitudeFeet = null;
        descentRequestedTimestamp = null;
        lastDescentElapsedSeconds = 0.0;
    }

    internal void HoldFileTargetAltitude(bool invalidatePending = false)
    {
        lock (stateGate)
        {
            if (invalidatePending)
                version++;
            // Eine Datenlücke darf weder die Absenkung fortsetzen noch ihre Wartezeit verbrauchen.
            descentRequestedTimestamp = null;
            lastDescentElapsedSeconds = 0.0;
        }
    }

    /// <summary>
    /// Liest außerhalb des Aufruferthreads das gültige Höhenmaximum 200 m entlang des wahren GPS-Tracks
    /// voraus sowie dort 30 m links und rechts. Liefert Meter direkt als Fuß plus 1000 ft, ohne Datum-
    /// oder Druckkorrektur. Bei fehlenden Daten, Lesefehlern, Zustandswechsel oder belegtem Reader
    /// bedeutet null: den zuletzt angewendeten ALT-Sollwert unverändert halten.
    /// Mit currentAltitudeFeet startet die Wartezeit bei Ist über Datei-Ziel; ohne Isthöhe bleibt die bisherige API kompatibel.
    /// </summary>
    public Task<double?> GetFileTargetAltitudeAsync(string path, double latitude, double longitude,
        double groundTrueTrack, double? currentAltitudeFeet = null, CancellationToken cancellationToken = default)
    {
        lock (stateGate)
        {
            if (disposed || !Enabled)
                return Task.FromResult<double?>(null);
            if (cancellationToken.IsCancellationRequested || string.IsNullOrWhiteSpace(path) || !ValidPosition(latitude, longitude) ||
                !double.IsFinite(groundTrueTrack) ||
                (currentAltitudeFeet.HasValue && !double.IsFinite(currentAltitudeFeet.Value)))
            {
                HoldFileTargetAltitude(invalidatePending: true);
                return Task.FromResult<double?>(null);
            }
            if (!string.Equals(activeFilePath, path, StringComparison.OrdinalIgnoreCase))
            {
                activeFilePath = path;
                version++;
                ResetTarget();
            }
            if (readInProgress)
                return Task.FromResult<double?>(null);
            readInProgress = true;
            Task<double?> operation = ReadFileTargetAltitudeAsync(ioTask, path, latitude, longitude,
                groundTrueTrack, version, currentAltitudeFeet, cancellationToken);
            ioTask = operation;

            return operation;
        }
    }

    private async Task<double?> ReadFileTargetAltitudeAsync(Task previous, string path, double latitude,
        double longitude, double track, long requestVersion, double? currentAltitudeFeet,
        CancellationToken cancellationToken)
    {
        try
        {
            await previous.ConfigureAwait(false);
            double? ground = await Task.Run(() =>
            {
                lock (stateGate)
                {
                    if (disposed || !Enabled || requestVersion != version || cancellationToken.IsCancellationRequested)
                        return (double?)null;
                }

                return QueryGroundAltitude(path, latitude, longitude, track, requestVersion);
            }).ConfigureAwait(false);
            lock (stateGate)
            {
                if (disposed || !Enabled || requestVersion != version)
                    return null;
                if (cancellationToken.IsCancellationRequested || ground is not double height)
                {
                    HoldFileTargetAltitude();
                    return null;
                }

                return UpdateTarget(height, FileHeightAboveGroundFeet, currentAltitudeFeet);
            }
        }
        finally
        {
            lock (stateGate) readInProgress = false;
        }
    }

    private double? QueryGroundAltitude(string path, double latitude, double longitude, double track, long requestVersion)
    {
        try
        {
            if (reader is null || readerVersion != requestVersion ||
                !string.Equals(readerPath, path, StringComparison.OrdinalIgnoreCase))
            {
                CloseReader();
                // Open prüft Datum, CRS und die tatsächliche Zell-/Höhenschrittweite der Metadaten.
                reader = TransformedTerrainReader.Open(path);
                readerPath = path;
                readerVersion = requestVersion;
            }
            double bearing = (track % 360.0 + 360.0) % 360.0;
            (double Latitude, double Longitude) center = WaypointManager.Destination(latitude, longitude, bearing, 0.2);
            (double Latitude, double Longitude) left = WaypointManager.Destination(center.Latitude, center.Longitude, bearing - 90.0, 0.03);
            (double Latitude, double Longitude) right = WaypointManager.Destination(center.Latitude, center.Longitude, bearing + 90.0, 0.03);
            double? maximum = null;
            foreach ((double lat, double lon) in new[] { center, left, right })
            {
                TransformedTerrainResult result = reader.QueryPoint(lat, lon);
                if (result.Status is not (TransformedCellStatus.Observed or TransformedCellStatus.Water or
                    TransformedCellStatus.Estimated or TransformedCellStatus.Partial) ||
                    result.HeightMeters is not double meters || !double.IsFinite(meters))
                    continue;
                maximum = maximum is double height ? Math.Max(height, meters) : meters;
            }
            if (maximum is double maximumMeters)
            {
                double feet = maximumMeters * FeetPerMeter;
                double target = feet + FileHeightAboveGroundFeet;
                if (double.IsFinite(target) && target >= int.MinValue && target <= int.MaxValue)
                    return feet;
            }
        }
        catch (InvalidDataException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (SecurityException) { }
        catch (ArgumentException) { }
        catch (NotSupportedException) { }
        // Bei vorhandener, aber unbrauchbarer Datei ist stilles Halten ausdrücklich vorgesehen.
        return null;
    }

    private void CloseReader()
    {
        try { reader?.Dispose(); }
        catch (IOException) { }
        finally
        {
            reader = null;
            readerPath = null;
        }
    }

    private async Task ReleaseReaderAfterAsync(Task previous)
    {
        try { await previous.ConfigureAwait(false); }
        finally { await Task.Run(CloseReader).ConfigureAwait(false); }
    }

    /// <summary>Invalidiert ausstehende Ergebnisse sofort und gibt den Reader nach dem letzten Lesevorgang außerhalb des UI-Threads frei.</summary>
    public ValueTask DisposeAsync()
    {
        lock (stateGate)
        {
            if (!disposed)
            {
                disposed = true;
                Enabled = false;
                version++;
                ResetTarget();
                ioTask = ReleaseReaderAfterAsync(ioTask);
            }

            return new ValueTask(ioTask);
        }
    }

    private static bool ValidPosition(double latitude, double longitude) =>
        double.IsFinite(latitude) && latitude >= -90.0 && latitude <= 90.0 &&
        double.IsFinite(longitude) && longitude >= -180.0 && longitude <= 180.0;

    /// <summary>Liefert den geführten ALT-Sollwert. Ohne Flugzeughöhe bleibt die bisherige geländebasierte API kompatibel.</summary>
    public bool TryGetTargetAltitude(double latitude, double longitude, double groundAltitudeFeet,
        out double targetAltitudeFeet, double? currentAltitudeFeet = null)
    {
        targetAltitudeFeet = 0.0;
        if (!Enabled || !double.IsFinite(latitude) || latitude < -90.0 || latitude > 90.0 ||
            !double.IsFinite(longitude) || longitude < -180.0 || longitude > 180.0 ||
            !double.IsFinite(groundAltitudeFeet) ||
            !double.IsFinite(groundAltitudeFeet + HeightAboveGroundFeet) ||
            (currentAltitudeFeet.HasValue && !double.IsFinite(currentAltitudeFeet.Value)))
            return false;

        lock (stateGate)
        {
            if (disposed || !Enabled)
                return false;
            targetAltitudeFeet = UpdateTarget(groundAltitudeFeet, HeightAboveGroundFeet, currentAltitudeFeet);
        }

        return true;
    }

    private double UpdateTarget(double groundAltitudeFeet, double clearanceFeet, double? currentAltitudeFeet)
    {
        long timestamp = timeProvider.GetTimestamp();
        double waitRemainingSeconds = DescentDelaySeconds;
        double descentRemainingSeconds = DescentDurationSeconds;
        double descentProgressPercent = 0.0;
        requestedTargetAltitudeFeet = groundAltitudeFeet + clearanceFeet;
        bool shouldDescend = false;
        if (currentAltitudeFeet is double aircraftAltitudeFeet)
        {
            if (aircraftAltitudeFeet <= requestedTargetAltitudeFeet.Value)
            {
                // Keine Absenkung nötig: höhere Zielhöhen unmittelbar freigeben.
                targetGroundAltitudeFeet = groundAltitudeFeet;
                descentRequestedTimestamp = null;
                lastDescentElapsedSeconds = 0.0;
            }
            else
            {
                shouldDescend = true;
                if (descentRequestedTimestamp is null)
                {
                    // Während der Wartezeit die Höhe beim Start der Anforderung halten.
                    targetGroundAltitudeFeet = aircraftAltitudeFeet - clearanceFeet;
                }
            }
        }
        else if (targetGroundAltitudeFeet is not double previousTarget)
        {
            targetGroundAltitudeFeet = groundAltitudeFeet;
        }
        else if (groundAltitudeFeet > previousTarget)
        {
            // Kompatibilität für bestehende Aufrufer ohne Flugzeughöhe.
            targetGroundAltitudeFeet = groundAltitudeFeet;
            descentRequestedTimestamp = null;
        }
        else
        {
            shouldDescend = groundAltitudeFeet < previousTarget || descentRequestedTimestamp is not null;
        }

        if (shouldDescend)
        {
            if (descentRequestedTimestamp is null)
            {
                descentRequestedTimestamp = timestamp;
                lastDescentElapsedSeconds = 0.0;
            }

            double elapsedSeconds = timeProvider.GetElapsedTime(descentRequestedTimestamp.Value, timestamp).TotalSeconds;
            double previousElapsedSeconds = Math.Max(DescentDelaySeconds, lastDescentElapsedSeconds);
            double remainingSeconds = DescentDelaySeconds + DescentDurationSeconds - previousElapsedSeconds;
            double progress = remainingSeconds <= 0.0
                ? 1.0
                : Math.Clamp((elapsedSeconds - previousElapsedSeconds) / remainingSeconds, 0.0, 1.0);
            double currentTarget = targetGroundAltitudeFeet!.Value;
            // Nur den seit dem letzten Messwert verstrichenen Anteil absenken.
            // Bei konstantem Gelände wird das Ziel nach 30 + 60 Sekunden linear erreicht.
            targetGroundAltitudeFeet = Math.Max(groundAltitudeFeet,
                (1.0 - progress) * currentTarget + progress * groundAltitudeFeet);
            lastDescentElapsedSeconds = elapsedSeconds;
            double descentSeconds = Math.Clamp(elapsedSeconds - DescentDelaySeconds, 0.0, DescentDurationSeconds);
            waitRemainingSeconds = Math.Clamp(DescentDelaySeconds - elapsedSeconds, 0.0, DescentDelaySeconds);
            descentRemainingSeconds = DescentDurationSeconds - descentSeconds;
            descentProgressPercent = descentSeconds / DescentDurationSeconds * 100.0;
            // Nach Ablauf bei weiterhin höherem Istwert keinen neuen Wartezyklus starten.
            if (progress >= 1.0 && currentAltitudeFeet is null)
                descentRequestedTimestamp = null;
        }

        System.Diagnostics.Debug.WriteLine($"[Debug] {DateTime.Now:yyyy-MM-dd HH:mm:ss} - [TerrainFollow Time ] Warten: {waitRemainingSeconds:F0}s Absenk: {descentRemainingSeconds:F0}s Fortschritt: {descentProgressPercent.ToString("F1", CultureInfo.GetCultureInfo("de-DE"))}%");
        return targetGroundAltitudeFeet!.Value + clearanceFeet;
    }
}

public partial class AutoflyMain
{
    private readonly TerrainFollow terrainFollow = new();
    private Task terrainFollowUpdateTask = Task.CompletedTask;
    private TerrainSnapshot? pendingTerrainSnapshot;
    private int terrainTelemetrySession;
    private bool terrainLatitudeReceived;
    private bool terrainLongitudeReceived;
    private bool terrainTrackReceived;
    private sealed record TerrainSnapshot(double Latitude, double Longitude, double Track, double AircraftAltitudeFeet, string Path,
        long Version, int Session);

    private async void TerrainFollow_ShownAsync(object? sender, EventArgs e)
    {
        TsbAutoElevation.Image = null;
        if (!TerrainFollow.GeoPointsEnabled)
            return;

        using CancellationTokenSource cancellation = new();
        void cancelSearch(object? sender, FormClosedEventArgs e) => cancellation.Cancel();
        FormClosed += cancelSearch;
        try
        {
            await terrainFollow.FindGeoPointsFileAsync(cancellation.Token);
            if (IsDisposed || Disposing || cancellation.IsCancellationRequested)
                return;

            TsbAutoElevation.Image = terrainFollow.GeoPointsFilePath is null
                ? null
                : (Image?)Resources_DE.ResourceManager.GetObject("Waypoint.gtt_file");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            System.Diagnostics.Debug.WriteLine($"[Debug] {DateTime.Now:yyyy-MM-dd HH:mm:ss} - [TerrainFollow] GTT-Dateisuche abgebrochen.");
        }
        finally
        {
            FormClosed -= cancelSearch;
        }
    }

    private void TsbAutoElevation_Click(object? sender, EventArgs e)
    {
        if (IsAutostartActive)
            return;
        if (terrainFollow.Enabled)
        {
            terrainFollow.SetEnabled(false);
            TsbAutoElevation.BackColor = System.Drawing.Color.LightGray;
            System.Diagnostics.Debug.WriteLine($"[Debug] {DateTime.Now:yyyy-MM-dd HH:mm:ss} - [TerrainFollow] Auto-Höhe AUS");
            ApplyWaypointNavigation();
            return;
        }
        if (simconnect == null || autopilot == null || relocationControlsSuspended ||
            (!terrainFollow.UsesGeoPointsFile && !TryGetTerrainTelemetry(out _, out _, out _)))
            return;

        SetWaypointFollowing(false);
        if (!_isHDGActive && TryGetWaypointTelemetry(
            Flight_Data.PLANE_HEADING_DEGREES_MAGNETIC.Value, out double heading))
        {
            _targetHeading = RoundTargetHeading(heading);
            autopilot.LocalTargetHdgDeg = _targetHeading;
            tstbHeading.Text = _targetHeading.ToString(CultureInfo.InvariantCulture);
        }
        if (!_isLocalAutopilotActive)
            TsbAP_Click(TsbAutoElevation, e);
        if (!_isLVLActive)
            TsbLVL_Click(TsbAutoElevation, e);
        if (!_isHDGActive)
            TsbHDG_Click(TsbAutoElevation, e);
        if (!_isALTActive)
            TsbALT_Click(TsbAutoElevation, e);

        terrainFollow.SetEnabled(true);
        TsbAutoElevation.BackColor = System.Drawing.Color.LightGreen;
        System.Diagnostics.Debug.WriteLine($"[Debug] {DateTime.Now:yyyy-MM-dd HH:mm:ss} - [TerrainFollow] Auto-Höhe EIN");
        ApplyTerrainFollow();
    }

    private static bool TryGetTerrainTelemetry(out double latitude, out double longitude, out double groundAltitude)
    {
        bool latitudeValid = TryGetWaypointTelemetry(Flight_Data.PLANE_LATITUDE.Value, out latitude);
        bool longitudeValid = TryGetWaypointTelemetry(Flight_Data.PLANE_LONGITUDE.Value, out longitude);
        bool groundValid = TryGetWaypointTelemetry(Flight_Data.GROUND_ALTITUDE.Value, out groundAltitude);

        return latitudeValid && longitudeValid && groundValid &&
            latitude >= -90.0 && latitude <= 90.0 && longitude >= -180.0 && longitude <= 180.0;
    }

    private void ApplyTerrainFollow()
    {
        if (terrainFollow.UsesGeoPointsFile || IsAutostartActive || !terrainFollow.Enabled || autopilot == null || relocationControlsSuspended ||
            !TryGetTerrainTelemetry(out double latitude, out double longitude, out double groundAltitude) ||
            !TryGetWaypointTelemetry(Flight_Data.PLANE_ALTITUDE.Value, out double aircraftAltitudeFeet) ||
            !terrainFollow.TryGetTargetAltitude(latitude, longitude, groundAltitude, out double targetAltitude,
                currentAltitudeFeet: aircraftAltitudeFeet))
            return;

        _targetAltitude = RoundTargetAltitude(targetAltitude);
        autopilot.localTargetAltDeg = _targetAltitude;
        tstbAltitude.Text = _targetAltitude.ToString(CultureInfo.InvariantCulture);
        System.Diagnostics.Debug.WriteLine($"[Debug] {DateTime.Now:yyyy-MM-dd HH:mm:ss} - [TerrainFollow Inter] " +
            $"ALT Ist:{aircraftAltitudeFeet:F0} ALT Soll:{(groundAltitude + TerrainFollow.HeightAboveGroundFeet):F0} " +
            $"diff:{(groundAltitude + TerrainFollow.HeightAboveGroundFeet - aircraftAltitudeFeet):F0} ft");
    }

    private void ResetTerrainTelemetry()
    {
        terrainTelemetrySession++;
        terrainLatitudeReceived = false;
        terrainLongitudeReceived = false;
        terrainTrackReceived = false;
        pendingTerrainSnapshot = null;
        terrainFollow.SetEnabled(terrainFollow.Enabled);
    }

    private void ObserveTerrainTelemetry(DataItem item)
    {
        if (ReferenceEquals(item, Flight_Data.PLANE_LATITUDE))
            terrainLatitudeReceived = true;
        else if (ReferenceEquals(item, Flight_Data.PLANE_LONGITUDE))
            terrainLongitudeReceived = true;
        else if (ReferenceEquals(item, Flight_Data.GPS_GROUND_TRUE_TRACK))
            terrainTrackReceived = true;
    }

    private Task QueueFileTerrainFollowUpdate()
    {
        if (!terrainFollow.UsesGeoPointsFile)
            return Task.CompletedTask;

        if (!terrainFollow.Enabled || IsAutostartActive ||
            simconnect == null || autopilot == null || relocationControlsSuspended || IsDisposed || Disposing ||
            !terrainLatitudeReceived || !terrainLongitudeReceived || !terrainTrackReceived ||
            terrainFollow.GeoPointsFilePath is not string path ||
            !TryGetWaypointTelemetry(Flight_Data.PLANE_LATITUDE.Value, out double latitude) ||
            !TryGetWaypointTelemetry(Flight_Data.PLANE_LONGITUDE.Value, out double longitude) ||
            !TryGetWaypointTelemetry(Flight_Data.GPS_GROUND_TRUE_TRACK.Value, out double track) ||
            !TryGetWaypointTelemetry(Flight_Data.PLANE_ALTITUDE.Value, out double aircraftAltitudeFeet) ||
            latitude < -90.0 || latitude > 90.0 || longitude < -180.0 || longitude > 180.0)
        {
            pendingTerrainSnapshot = null;
            terrainFollow.HoldFileTargetAltitude(invalidatePending: true);
            return Task.CompletedTask;
        }

        pendingTerrainSnapshot = new(latitude, longitude, track, aircraftAltitudeFeet, path, terrainFollow.Version, terrainTelemetrySession);
        if (!terrainFollowUpdateTask.IsCompleted)
            return Task.CompletedTask;

        terrainFollowUpdateTask = ProcessFileTerrainFollowAsync();

        return terrainFollowUpdateTask;
    }

    private async Task ProcessFileTerrainFollowAsync()
    {
        while (pendingTerrainSnapshot is TerrainSnapshot snapshot)
        {
            pendingTerrainSnapshot = null;
            if (snapshot.Session != terrainTelemetrySession || snapshot.Version != terrainFollow.Version ||
                !terrainFollow.Enabled || IsAutostartActive || relocationControlsSuspended || IsDisposed || Disposing)
                continue;

            double? target = await terrainFollow.GetFileTargetAltitudeAsync(snapshot.Path,
                snapshot.Latitude, snapshot.Longitude, snapshot.Track,
                currentAltitudeFeet: snapshot.AircraftAltitudeFeet);
            if (target is not double targetAltitude || snapshot.Session != terrainTelemetrySession ||
                snapshot.Version != terrainFollow.Version || !terrainFollow.Enabled || IsAutostartActive ||
                relocationControlsSuspended || simconnect == null || autopilot == null || IsDisposed || Disposing ||
                terrainFollow.RequestedTargetAltitudeFeet is not double requestedAltitudeFeet)
                continue;

            _targetAltitude = RoundTargetAltitude(targetAltitude);
            autopilot.localTargetAltDeg = _targetAltitude;
            tstbAltitude.Text = _targetAltitude.ToString(CultureInfo.InvariantCulture);
            System.Diagnostics.Debug.WriteLine($"[Debug] {DateTime.Now:yyyy-MM-dd HH:mm:ss} - [TerrainFollow File ] " +
                $"ALT Ist:{snapshot.AircraftAltitudeFeet:F0} ALT Soll:{requestedAltitudeFeet:F0} " +
                $"diff:{requestedAltitudeFeet - snapshot.AircraftAltitudeFeet:F0} ft");
        }
    }
}
