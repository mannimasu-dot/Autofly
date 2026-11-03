using Microsoft.FlightSimulator.SimConnect;
using System.Diagnostics;
using System.Runtime.InteropServices;
using static AutoFly.AutoflyMain;

namespace AutoFly
{
    public class AutoPilot
    {
        public enum EVENT_IDS : uint
        {
            AUTOPILOT_OFF = 1,
            AILERON_SET = 2,
        }
        private enum DummyGroup : uint { Group0 = 0 }
        private enum WriteDefinitionId : uint { RollTrim = 10000, PitchTrim = 10001 }
        private SimConnect? simconnect;
        private bool trimDefinitionsRegistered;

        /// <summary>Gibt an, ob eine SimConnect-Verbindung und registrierte Trimdefinitionen vorliegen.</summary>
        public bool IsReady => simconnect != null && trimDefinitionsRegistered;

        private bool rollTrimInitialized;
        private bool pitchTrimInitialized;
        private double rollTrimPercent;
        private double pitchTrimRadians;
        private double? previousBankDegrees;
        private double? previousVerticalSpeed;
        private long lastControlSampleTimestamp;
        private double? previousIndicatedAirspeed;
        private bool autoThrottleEnabled;
        private readonly HeadingTurnController headingController = new();
        private bool headingAxisOwned;
        private DateTime? lastHeadingSample;
        private DateTime? lastHeadingBankSample;
        private double lastHeadingDegrees;
        private double lastHeadingBankDegrees;
        private string? headingAircraftTitle;
        private bool preserveHeadingControlsOnWarmup;

        // ----------------------- Felder für lokalen Autopilot-Status ----------------------
        private bool LokalAutopilotEnabled = false;
        private bool ALTControlEnabled = false;
        private bool HDGControlEnabled = false;
        private bool ATTControlEnabled = false;
        private bool LVLControlEnabled = false;
        // ------------------------ Felder für Höhen Trim-Regelung ------------------------
        public int localTargetAltDeg = 0;

        // ------------------------ Felder für Kursregelung ------------------------
        public int LocalTargetHdgDeg = 0;
        // ------------------------ Konstruktor ----------------------------------      
        public AutoPilot(SimConnect? simconnect = null)
        {
            SetSimConnect(simconnect);
        }

        // ------------------------ SimConnect-Setup -----------------------------
        public void SetSimConnect(SimConnect? sc)
        {
            headingAxisOwned = false;
            simconnect = sc;
            trimDefinitionsRegistered = false;
            ResetCruiseControlState();
            if (simconnect != null)
            {
                try
                {
                    //************** Autopilot im Flugzeug ****************************
                    simconnect.MapClientEventToSimEvent(EVENT_IDS.AUTOPILOT_OFF, "AUTOPILOT_OFF");
                    //************** Autopilot hier ***********************************
                    simconnect.MapClientEventToSimEvent(EVENT_IDS.AILERON_SET, "AILERON_SET");

                    simconnect.AddToDataDefinition(WriteDefinitionId.RollTrim, "AILERON TRIM PCT",
                        "percent over 100", SIMCONNECT_DATATYPE.FLOAT64, 0.0f, SimConnect.SIMCONNECT_UNUSED);
                    simconnect.RegisterDataDefineStruct<double>(WriteDefinitionId.RollTrim);
                    simconnect.AddToDataDefinition(WriteDefinitionId.PitchTrim, "ELEVATOR TRIM POSITION",
                        "radians", SIMCONNECT_DATATYPE.FLOAT64, 0.0f, SimConnect.SIMCONNECT_UNUSED);
                    simconnect.RegisterDataDefineStruct<double>(WriteDefinitionId.PitchTrim);
                    for (int engine = 1; engine <= 4; engine++)
                    {
                        Enum definition = (WriteDefinitionId)(10100 + engine);
                        simconnect.AddToDataDefinition(definition, $"GENERAL ENG THROTTLE LEVER POSITION:{engine}",
                            "percent", SIMCONNECT_DATATYPE.FLOAT64, 0.0f, SimConnect.SIMCONNECT_UNUSED);
                        simconnect.RegisterDataDefineStruct<double>(definition);
                    }
                    trimDefinitionsRegistered = true;

                    Debug.WriteLine($"[Debug] {DateTime.Now:yyyy-MM-dd HH:mm:ss} - [Autopilot] alle Events angemeldet");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Debug] {DateTime.Now:yyyy-MM-dd HH:mm:ss} - [Autopilot] Fehler beim Mapping der Events: {ex.Message}");
                }
            }
        }

        // Schaltet ausschließlich die lokale Reisegeschwindigkeits-Schubregelung frei.
        public void AutoThrottleOnOff(bool value)
        {
            if (autoThrottleEnabled != value)
                previousIndicatedAirspeed = null;
            autoThrottleEnabled = value;
        }

        // Berechnet die gestaffelten Schubänderungen in Prozentpunkten gemäß Tutorial.
        public static double[] CalculateThrottleChanges(double speedKnots, double speedChangeKnotsPerSecond,
            double cruiseSpeedKnots, double weightPounds, double altitudeFeet, double? targetAltitudeFeet = null)
        {
            if (!double.IsFinite(speedKnots) || speedKnots < 0.0)
                throw new ArgumentOutOfRangeException(nameof(speedKnots));
            if (!double.IsFinite(speedChangeKnotsPerSecond))
                throw new ArgumentOutOfRangeException(nameof(speedChangeKnotsPerSecond));
            if (!double.IsFinite(cruiseSpeedKnots) || cruiseSpeedKnots <= 0.0)
                throw new ArgumentOutOfRangeException(nameof(cruiseSpeedKnots));
            if (!double.IsFinite(weightPounds) || weightPounds <= 0.0)
                throw new ArgumentOutOfRangeException(nameof(weightPounds));
            if (!double.IsFinite(altitudeFeet))
                throw new ArgumentOutOfRangeException(nameof(altitudeFeet));
            if (targetAltitudeFeet.HasValue && !double.IsFinite(targetAltitudeFeet.Value))
                throw new ArgumentOutOfRangeException(nameof(targetAltitudeFeet));
            double diff = Math.Abs(speedKnots - cruiseSpeedKnots);
            double threshold = FlightMath.ConstrainMap(diff, 0.0, 10.0, 0.01, 0.2);
            double step = FlightMath.ConstrainMap(diff, 0.0, 50.0, 1.0, 5.0);
            step = FlightMath.ConstrainMap(weightPounds, 1000.0, 6000.0, step / 5.0, step);
            double altitudeDifference = targetAltitudeFeet.HasValue ? targetAltitudeFeet.Value - altitudeFeet : 0.0;
            double altitudeFactor = FlightMath.ConstrainMap(altitudeDifference, 0.0, 100.0, 0.0, 0.25);
            List<double> changes = [];
            if (cruiseSpeedKnots - speedKnots > 2.0)
            {
                if (speedChangeKnotsPerSecond <= threshold)
                    changes.Add(step);
                if (altitudeDifference > 50.0)
                    changes.Add(altitudeFactor * step);
                if (speedChangeKnotsPerSecond > threshold)
                    changes.Add(step / 4.0);
            }
            if (speedKnots - cruiseSpeedKnots > 2.0)
            {
                if (speedChangeKnotsPerSecond >= -3.0 * threshold)
                    changes.Add(-step);
                if (altitudeDifference < -50.0)
                    changes.Add(-altitudeFactor * step);
                if (speedChangeKnotsPerSecond < -3.0 * threshold)
                    changes.Add(step / 4.0);
            }
            return [.. changes];
        }

        // --------------- Methoden für Autopilot-Steuerung im Flugzeug-----------
        public void DisablePlaneAutopilot()
        {
            if (simconnect == null) return;
            simconnect.TransmitClientEvent(
                SimConnect.SIMCONNECT_OBJECT_ID_USER,
                EVENT_IDS.AUTOPILOT_OFF,
                0u,
                DummyGroup.Group0,
                SIMCONNECT_EVENT_FLAG.GROUPID_IS_PRIORITY);
            Debug.WriteLine($"[Debug] {DateTime.Now:yyyy-MM-dd HH:mm:ss} - [Autopilot] Flugzeug AUTOPILOT OFF angefordert");
        }

        // --------------- Methoden für Steuerung Flugzeug -----------------------

        // ---------------- Methoden für Kursregelung -----------------------
        public static class FlyLevel
        {
            // Waagerechte Tragflächen entsprechen einem Bankwinkel von null Grad.
            private const double DefaultTargetBank = 0.0;
            // Obergrenze für die Berechnung, keine allgemeine Sicherheitsgarantie.
            private const double DefaultMaxBank = 30.0;
            // Bezugswert der Rollratendämpfung in Grad pro Sekunde.
            private const double DefaultMaxRollRate = 3.0;
            private const double HeadingBumpFactor = 1.8;
            // Berechnet die neue Querrudertrimmung, ohne Steuerbefehle zu senden.
            // <param name="bankDegrees">Bankwinkel in Grad; rechts negativ, links positiv.</param>
            // <param name="speedKnots">Fluggeschwindigkeit in Knoten.</param>
            // <param name="rollRateDegreesPerSecond">Änderung des Bankwinkels in Grad pro Sekunde.</param>
            // <param name="currentTrimPercent">Aktuelle Querrudertrimmung in Prozent.</param>
            // <param name="currentHeadingDegrees">Aktueller magnetischer Kurs in Grad.</param>
            // <param name="targetHeadingDegrees">Magnetischer Sollkurs; null lässt die Zielbank bei null Grad.</param>
            // <returns>Querrudertrimmung zwischen -100 und 100 Prozent.</returns>
            public static double CalculateRollTrim(
                double bankDegrees,
                double speedKnots,
                double rollRateDegreesPerSecond,
                double currentTrimPercent,
                double currentHeadingDegrees = 0.0,
                double? targetHeadingDegrees = null,
                bool useStickInstead = false,
                double weightPounds = 1000.0,
                bool isAcrobatic = false)
            {
                if (!double.IsFinite(bankDegrees))
                    throw new ArgumentOutOfRangeException(nameof(bankDegrees));
                if (!double.IsFinite(speedKnots))
                    throw new ArgumentOutOfRangeException(nameof(speedKnots));
                if (!double.IsFinite(rollRateDegreesPerSecond))
                    throw new ArgumentOutOfRangeException(nameof(rollRateDegreesPerSecond));
                if (!double.IsFinite(currentTrimPercent))
                    throw new ArgumentOutOfRangeException(nameof(currentTrimPercent));
                if (!double.IsFinite(currentHeadingDegrees))
                    throw new ArgumentOutOfRangeException(nameof(currentHeadingDegrees));
                if (targetHeadingDegrees.HasValue && !double.IsFinite(targetHeadingDegrees.Value))
                    throw new ArgumentOutOfRangeException(nameof(targetHeadingDegrees));
                ArgumentOutOfRangeException.ThrowIfNegative(speedKnots);
                if (!double.IsFinite(weightPounds) || weightPounds <= 0.0)
                    throw new ArgumentOutOfRangeException(nameof(weightPounds));
                // Die numerische Schrittweite der Vorlage bleibt erhalten; sie ist kein Bankwinkel.
                double step = FlightMath.ConstrainMap(
                    speedKnots, 20.0, 150.0, FlightMath.ToRadians(0.1), FlightMath.ToRadians(5.0));
                if (useStickInstead)
                    step = FlightMath.ConstrainMap(weightPounds, 1000.0, 6000.0, 1.0, 3.0);
                // Bei geringer Geschwindigkeit wird die für die Berechnung verwendete Bank begrenzt.
                double maxBank = FlightMath.ConstrainMap(speedKnots, 50.0, 200.0, 10.0, DefaultMaxBank);
                double targetBank = DefaultTargetBank;
                double maxRollRate = DefaultMaxRollRate;
                double headingDiff = 0.0;
                if (targetHeadingDegrees.HasValue)
                {
                    headingDiff = FlightMath.GetCompassDiff(currentHeadingDegrees, targetHeadingDegrees.Value);
                    if (!isAcrobatic)
                        headingDiff /= 2.0;
                    targetBank = FlightMath.ConstrainMap(headingDiff, -30.0, 30.0, maxBank, -maxBank);
                    maxRollRate = FlightMath.ConstrainMap(Math.Abs(headingDiff), 0.0, 10.0, 0.0, DefaultMaxRollRate);
                }
                double diff = targetBank - bankDegrees;
                if (isAcrobatic)
                    step = FlightMath.ConstrainMap(Math.Abs(headingDiff), 0.0, 10.0, step / 5.0, step / 20.0);
                double bankCorrection = FlightMath.ConstrainMap(diff, -maxBank, maxBank, -step, step);
                double absoluteHeadingDiff = Math.Abs(headingDiff);
                // Nur den Bankanteil verstärken; Rollratendämpfung und Nahdämpfung folgen danach.
                if (targetHeadingDegrees.HasValue && absoluteHeadingDiff > 0.5 && absoluteHeadingDiff < 5.0)
                {
                    bankCorrection *= HeadingBumpFactor;
                    if (absoluteHeadingDiff < 2.0)
                    {
                        bankCorrection *= HeadingBumpFactor;
                        if (absoluteHeadingDiff < 1.0)
                            bankCorrection *= HeadingBumpFactor;
                    }
                }
                double update = -bankCorrection;
                // Die Rollrate dämpft die Korrektur, um ein Überschwingen zu verringern.
                update += FlightMath.ConstrainMap(
                    rollRateDegreesPerSecond, -4.0 * maxRollRate, 4.0 * maxRollRate,
                    -2.0 * step, 2.0 * step);
                if (Math.Abs(diff) < 2.0)
                    update /= 2.0;
                // Die Vorlage addiert normierte Trimmanteile; die öffentliche Schnittstelle nutzt Prozent.
                return Math.Clamp(currentTrimPercent + (useStickInstead ? update : update * 100.0), -100.0, 100.0);
            }
        }

        // --------------- Methoden für Höhenregelung -----------------------
        public static class AltitudeHold
        {
            // Ohne Sollhöhe bleibt das Ziel waagerechter Flug; mit Sollhöhe wird eine Soll-VS berechnet.
            private const double DefaultTargetVerticalSpeed = 0.0;
            // Obergrenze der VS-Abweichung für die Trimmkorrektur in ft/min.
            private const double DefaultMaxVerticalSpeed = 1000.0;
            // Bezugswert für die Dämpfung bei Änderungen der Vertikalgeschwindigkeit.
            private const double DefaultMaxVerticalSpeedChange = 200.0;
            private const double EmergencyCorrectionFactor = 4.0;

            // Berechnet die Trimmposition; optionale IAS und VY begrenzen positive Soll-VS bei geringer Fahrt.
            public static double CalculatePitchTrim(
                double verticalSpeedFeetPerMinute,
                double verticalSpeedChangeFeetPerMinute,
                double currentTrimRadians,
                double pitchTrimUpLimitRadians,
                double currentAltitudeFeet = 0.0,
                double? targetAltitudeFeet = null,
                double? indicatedAirspeedKnots = null,
                double? climbSpeedKnots = null)
            {
                if (!double.IsFinite(verticalSpeedFeetPerMinute))
                    throw new ArgumentOutOfRangeException(nameof(verticalSpeedFeetPerMinute));
                if (!double.IsFinite(verticalSpeedChangeFeetPerMinute))
                    throw new ArgumentOutOfRangeException(nameof(verticalSpeedChangeFeetPerMinute));
                if (!double.IsFinite(currentTrimRadians))
                    throw new ArgumentOutOfRangeException(nameof(currentTrimRadians));
                if (!double.IsFinite(pitchTrimUpLimitRadians))
                    throw new ArgumentOutOfRangeException(nameof(pitchTrimUpLimitRadians));
                if (!double.IsFinite(currentAltitudeFeet))
                    throw new ArgumentOutOfRangeException(nameof(currentAltitudeFeet));
                if (targetAltitudeFeet.HasValue && !double.IsFinite(targetAltitudeFeet.Value))
                    throw new ArgumentOutOfRangeException(nameof(targetAltitudeFeet));
                ArgumentOutOfRangeException.ThrowIfNegative(pitchTrimUpLimitRadians);
                if (indicatedAirspeedKnots.HasValue != climbSpeedKnots.HasValue)
                    throw new ArgumentException(null, nameof(climbSpeedKnots));
                if (indicatedAirspeedKnots.HasValue &&
                    (!double.IsFinite(indicatedAirspeedKnots.Value) || indicatedAirspeedKnots.Value < 0.0))
                    throw new ArgumentOutOfRangeException(nameof(indicatedAirspeedKnots));
                if (climbSpeedKnots.HasValue &&
                    (!double.IsFinite(climbSpeedKnots.Value) || climbSpeedKnots.Value <= 0.0))
                    throw new ArgumentOutOfRangeException(nameof(climbSpeedKnots));

                // Die Vorlage nutzt den numerischen Gradwert als Regelfaktor für die Radiant-Ausgabe.
                double trimLimit = pitchTrimUpLimitRadians == 0.0
                    ? 10.0
                    : pitchTrimUpLimitRadians * 180.0 / Math.PI;
                double trimStep = trimLimit / 10_000.0;
                double targetVerticalSpeed = targetAltitudeFeet.HasValue
                    ? FlightMath.ConstrainMap(targetAltitudeFeet.Value - currentAltitudeFeet,
                        -200.0, 200.0, -DefaultMaxVerticalSpeed, DefaultMaxVerticalSpeed)
                    : DefaultTargetVerticalSpeed;
                if (targetVerticalSpeed > 0.0 && indicatedAirspeedKnots.HasValue && climbSpeedKnots.HasValue)
                {
                    double threshold = climbSpeedKnots.Value + 10.0;
                    if (indicatedAirspeedKnots.Value < threshold)
                        targetVerticalSpeed = FlightMath.ConstrainMap(indicatedAirspeedKnots.Value,
                            climbSpeedKnots.Value, threshold, 0.0, targetVerticalSpeed / 2.0);
                }
                double diff = targetVerticalSpeed - verticalSpeedFeetPerMinute;

                // Die VS-Abweichung bestimmt die Korrektur, begrenzt auf einen Trimmschritt.
                double update = FlightMath.ConstrainMap(
                    diff, -DefaultMaxVerticalSpeed, DefaultMaxVerticalSpeed, -trimStep, trimStep);

                // Änderungen der Vertikalgeschwindigkeit wirken der Korrektur dämpfend entgegen.
                double maxVerticalSpeedChange = FlightMath.ConstrainMap(
                    Math.Abs(diff), 0.0, 100.0, 0.0, DefaultMaxVerticalSpeedChange);
                update -= FlightMath.ConstrainMap(
                    verticalSpeedChangeFeetPerMinute, -maxVerticalSpeedChange,
                    maxVerticalSpeedChange, -trimStep, trimStep);

                // Die Schwellen beziehen sich auf den VS-Regelfehler, nicht auf die Höhendifferenz.
                if (Math.Abs(diff) < 100.0)
                    update /= 2.0;
                if (Math.Abs(diff) < 20.0)
                    update /= 2.0;

                bool verticalSpeedEmergency = Math.Abs(verticalSpeedFeetPerMinute) > DefaultMaxVerticalSpeed;
                bool verticalSpeedChangeEmergency = Math.Abs(verticalSpeedChangeFeetPerMinute) > DefaultMaxVerticalSpeedChange;
                if (verticalSpeedEmergency || verticalSpeedChangeEmergency)
                {
                    // Der Notfalleingriff ersetzt die normale Sollwertkorrektur und ihre Nahdämpfung.
                    update = 0.0;
                    double emergencyStep = EmergencyCorrectionFactor * trimStep;
                    if (verticalSpeedEmergency)
                    {
                        double emergencyMaxVerticalSpeed = EmergencyCorrectionFactor * DefaultMaxVerticalSpeed;
                        update += FlightMath.ConstrainMap(verticalSpeedFeetPerMinute,
                            -emergencyMaxVerticalSpeed, emergencyMaxVerticalSpeed, emergencyStep, -emergencyStep);
                    }
                    if (verticalSpeedChangeEmergency)
                    {
                        double emergencyMaxVerticalSpeedChange = EmergencyCorrectionFactor * maxVerticalSpeedChange;
                        update += FlightMath.ConstrainMap(verticalSpeedChangeFeetPerMinute,
                            -emergencyMaxVerticalSpeedChange, emergencyMaxVerticalSpeedChange, emergencyStep, -emergencyStep);
                    }
                }

                return currentTrimRadians + update;
            }
        }

        public void AutopilotLocalOnOff(bool value)
        {
            if (LokalAutopilotEnabled != value)
                ResetCruiseControlState();
            LokalAutopilotEnabled = value;
            Debug.WriteLine($"[Debug] {DateTime.Now:yyyy-MM-dd HH:mm:ss} - [Autopilot] Lokal Autopilot:{value}");
        }

        /// <summary>Nach dem Einschalten von AP/HDG die Autostart-Stellungen ohne Neutralimpuls übernehmen.</summary>
        public void PrepareAutostartHeadingHandover(string aircraftTitle)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(aircraftTitle);
            if (!LokalAutopilotEnabled || !HDGControlEnabled || headingAxisOwned || lastHeadingSample.HasValue)
                throw new InvalidOperationException("Autostart-Übergabe erfordert einen frisch eingeschalteten AP/HDG.");
            headingAircraftTitle = aircraftTitle;
            preserveHeadingControlsOnWarmup = true;
        }

        public void AltOnOff(bool value)
        {
            bool wasPitchEnabled = ATTControlEnabled || ALTControlEnabled;
            ALTControlEnabled = value;
            if (wasPitchEnabled != (ATTControlEnabled || ALTControlEnabled))
            {
                pitchTrimInitialized = false;
                previousVerticalSpeed = null;
            }
            Debug.WriteLine($"[Debug] {DateTime.Now:yyyy-MM-dd HH:mm:ss} - [Autopilot] ALT Höhen-Regelung:{value}");
        }
        public void HdgOnOff(bool value)
        {
            if (HDGControlEnabled != value)
                ReleaseHeadingControl();
            HDGControlEnabled = value;
            Debug.WriteLine($"[Debug] {DateTime.Now:yyyy-MM-dd HH:mm:ss} - [Autopilot] HDG Kurs Regelung:{value}");
        }
        public void AttOnOff(bool value)
        {
            bool wasPitchEnabled = ATTControlEnabled || ALTControlEnabled;
            ATTControlEnabled = value;
            if (wasPitchEnabled != (ATTControlEnabled || ALTControlEnabled))
            {
                pitchTrimInitialized = false;
                previousVerticalSpeed = null;
            }
            Debug.WriteLine($"[Debug] {DateTime.Now:yyyy-MM-dd HH:mm:ss} - [Autopilot] ATT:{value}");
        }
        public void LvlOnOff(bool value)
        {
            if (LVLControlEnabled != value)
            {
                ReleaseHeadingControl();
                rollTrimInitialized = false;
                previousBankDegrees = null;
            }
            LVLControlEnabled = value;
            Debug.WriteLine($"[Debug] {DateTime.Now:yyyy-MM-dd HH:mm:ss} - [Autopilot] LVL:{value}");
        }

        public static class FlightMath
        {
            // Reines Mapping von einer Range [a,b] auf [c,d]
            public static double Map(double v, double a, double b, double c, double d)
            {
                double sourceInterval = b - a;
                if (sourceInterval == 0) return (c + d) / 2;
                return c + ((v - a) * (d - c)) / sourceInterval;
            }

            // Begrenzt einen Wert auf Min/Max
            public static double Constrain(double v, double min, double max)
            {
                if (min > max) return Constrain(v, max, min);
                return v > max ? max : (v < min ? min : v);
            }

            // Kombiniert Mapping und Begrenzung (Pomax' wichtigste Funktion)
            public static double ConstrainMap(double v, double a, double b, double c, double d)
            {
                return Constrain(Map(v, a, b, c, d), c, d);
            }

            // Berechnet die kürzeste Differenz zwischen zwei Kompasskursen (-180 bis +180 Grad)
            public static double GetCompassDiff(double current, double target)
            {
                if (!double.IsFinite(current))
                    throw new ArgumentOutOfRangeException(nameof(current));
                if (!double.IsFinite(target))
                    throw new ArgumentOutOfRangeException(nameof(target));
                current = ((current % 360.0) + 360.0) % 360.0;
                target = ((target % 360.0) + 360.0) % 360.0;

                return ((target - current + 540.0) % 360.0) - 180.0;
            }

            // Wandelt Grad in Radiant um (falls wir für Trigonometrie Math.Sin/Cos nutzen müssen)
            public static double ToRadians(double degrees) => (degrees / 180.0) * Math.PI;
        }

        internal void ResetAfterRelocation() => ResetCruiseControlState();

        private void ResetCruiseControlState()
        {
            ReleaseHeadingControl();
            rollTrimInitialized = false;
            pitchTrimInitialized = false;
            previousBankDegrees = null;
            previousVerticalSpeed = null;
            lastControlSampleTimestamp = 0;
            previousIndicatedAirspeed = null;
        }

        private void ReleaseHeadingControl()
        {
            if (headingAxisOwned && simconnect != null)
                simconnect.TransmitClientEvent(SimConnect.SIMCONNECT_OBJECT_ID_USER,
                    EVENT_IDS.AILERON_SET, 0u, DummyGroup.Group0, SIMCONNECT_EVENT_FLAG.GROUPID_IS_PRIORITY);
            headingAxisOwned = false;
            lastHeadingSample = null;
            lastHeadingBankSample = null;
            lastHeadingDegrees = 0.0;
            lastHeadingBankDegrees = 0.0;
            headingAircraftTitle = null;
            preserveHeadingControlsOnWarmup = false;
            headingController.Reset();
        }

        private static bool TryGetFreshValue(DataItem? item, DateTime now, out double value)
        {
            value = 0.0;
            return item != null && TryGetFiniteValue(item, out value) &&
                now >= item.LastUpdated && (now - item.LastUpdated).TotalSeconds <= 2.0;
        }

        private void ApplyHeadingControl(DataItem headingItem, DataItem speedItem, DataItem bankItem,
            DataItem? turnRateItem, DataItem? stallSpeedItem, DataItem? cruiseSpeedItem,
            DataItem? weightItem, DataItem? wingAreaItem, string? title)
        {
            DateTime now = DateTime.UtcNow;
            if (simconnect == null || string.IsNullOrWhiteSpace(title) || !double.IsFinite(LocalTargetHdgDeg) ||
                !TryGetFreshValue(headingItem, now, out double heading) ||
                !TryGetFreshValue(speedItem, now, out double speed) || speed < 0.0 ||
                !TryGetFreshValue(bankItem, now, out double bank) || Math.Abs(bank) >= 90.0 ||
                Math.Abs((headingItem.LastUpdated - bankItem.LastUpdated).TotalSeconds) > 0.1 ||
                 turnRateItem == null || !TryGetFreshValue(turnRateItem, now, out _) ||
                stallSpeedItem == null || !TryGetFiniteValue(stallSpeedItem, out double stallSpeed) || stallSpeed <= 0.0 ||
                cruiseSpeedItem == null || !TryGetFiniteValue(cruiseSpeedItem, out double cruiseSpeed) || cruiseSpeed <= stallSpeed ||
                weightItem == null || !TryGetFiniteValue(weightItem, out double weight) || weight <= 0.0 ||
                wingAreaItem == null || !TryGetFiniteValue(wingAreaItem, out double wingArea) || wingArea <= 0.0 ||
                !double.IsFinite(weight * 2.2046226218487757 / wingArea))
            {
                ReleaseHeadingControl();
                return;
            }
            if (headingAircraftTitle != title)
                ReleaseHeadingControl();
            // TURN INDICATOR RATE ist eine Instrumentenanzeige und folgt im Fluglog
            // nicht der momentanen Kursänderung. HDG und Bank mit eigenen Zeitstempeln ableiten.
            DateTime sample = headingItem.LastUpdated;
            DateTime bankSample = bankItem.LastUpdated;
            if (lastHeadingSample.HasValue && (sample <= lastHeadingSample.Value ||
                bankSample <= lastHeadingBankSample!.Value))
            {
                return;
            }
            double elapsed = lastHeadingSample.HasValue ? (sample - lastHeadingSample.Value).TotalSeconds : 0.5;
            double bankElapsed = lastHeadingBankSample.HasValue ? (bankSample - lastHeadingBankSample.Value).TotalSeconds : 0.5;
            if (elapsed > 2.0 || bankElapsed > 2.0)
            {
                ReleaseHeadingControl();
                elapsed = bankElapsed = 0.5;
            }
            // Sehr kurze Folge-Samples nicht differenzieren; Zeit und Änderung aufsummieren.
            if (lastHeadingSample.HasValue && (elapsed < 0.1 || bankElapsed < 0.1))
            {
                return;
            }
            bool warmup = !lastHeadingSample.HasValue;
            double turnRate = warmup ? 0.0 : FlightMath.GetCompassDiff(lastHeadingDegrees, heading) / elapsed;
            double headingRollRate = warmup ? 0.0 : (bank - lastHeadingBankDegrees) / bankElapsed;
            bool acrobatic = title.Contains("Pitts", StringComparison.OrdinalIgnoreCase) ||
                title.Contains("Gee Bee", StringComparison.OrdinalIgnoreCase);
            // Erster Snapshot nach Freigabe/Reset normalerweise neutral; bei Autostart Stellungen halten.
            bool preserveAutostartControls = warmup && preserveHeadingControlsOnWarmup;
            int command = warmup ? 0 : headingController.Calculate(heading, LocalTargetHdgDeg, turnRate, speed,
                stallSpeed, cruiseSpeed, weight, wingArea, acrobatic, elapsed, bank, headingRollRate);
            if (!headingAxisOwned)
            {
                // Nur bei der ausdrücklich vorbereiteten Autostart-Übergabe die Quertrimmung erhalten.
                if (!preserveAutostartControls)
                    simconnect.SetDataOnSimObject(WriteDefinitionId.RollTrim,
                        SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_DATA_SET_FLAG.DEFAULT, 0.0);
                headingAxisOwned = true;
            }
            if (!preserveAutostartControls)
                simconnect.TransmitClientEvent(SimConnect.SIMCONNECT_OBJECT_ID_USER,
                    EVENT_IDS.AILERON_SET, unchecked((uint)command), DummyGroup.Group0, SIMCONNECT_EVENT_FLAG.GROUPID_IS_PRIORITY);
            else
                Debug.WriteLine($"[Debug] {DateTime.Now:yyyy-MM-dd HH:mm:ss} - [Autopilot] Autostart-Übergabe: Quertrimmung und Querruderstellung unverändert");
            preserveHeadingControlsOnWarmup = false;
            headingAircraftTitle = title;
            lastHeadingSample = sample;
            lastHeadingBankSample = bankSample;
            lastHeadingDegrees = heading;
            lastHeadingBankDegrees = bank;
        }

        private static bool TryGetFiniteValue(DataItem item, out double value)
        {
            ArgumentNullException.ThrowIfNull(item);
            value = item.Value switch
            {
                double number => number,
                float number => number,
                int number => number,
                uint number => number,
                bool state => state ? 1.0 : 0.0,
                _ => double.NaN
            };

            return double.IsFinite(value);
        }

        // Verarbeitet den 500-ms-Snapshot und schreibt nur die freigegebenen Steuerachsen.
        public void ProcessCurrentValues(
            DataItem currentAltFeet,                                    // Höhe in Fuß
            DataItem currentHdgDeg,                                     // Magnetischer Kurs in Grad
            DataItem currentBankDeg,                                    // Bankwinkel in Grad
            DataItem currentAileronTrimPct,                             // Querrudertrimmung in Prozent
            DataItem verticalSpeed,                                     // Vertikalgeschwindigkeit in Fuß pro Minute
            DataItem AirSpeedIndicated,                                 // Indizierte Fluggeschwindigkeit in Knoten
            DataItem currentElevatorTrimPosition,                       // Höhenrudertrimmung in Radiant
            DataItem ElevatorTrimUpLimit,                               // Maximaler Höhenrudertrimm in Radiant
            DataItem ElevatorTrimDownLimit,                             // Minimaler Höhenrudertrimm in Radiant
            DataItem simulatorAutopilotMaster,                          // Autopilot Master Switch (0=off, 1=on)
            DataItem? designSpeedClimb = null,                          // Design Climb Speed in Knoten
            DataItem? aircraftTitle = null,                             // Flugzeugbezeichnung
            DataItem? totalWeightKg = null,                             // Gesamtgewicht in Kilogramm
            DataItem? currentAileronPosition = null,                    // Aktuelle Querruderposition in Radiant
            DataItem? designCruiseSpeed = null,                         // Design Cruise Speed in Knoten
            DataItem? engineCount = null,                               // Anzahl der Triebwerke
            IReadOnlyList<DataItem>? engineThrottlePositions = null,   // Triebwerksdrosselstellungen in Prozent
            DataItem? currentTurnRate = null,                          // Drehrate in Grad/Sekunde, rechts positiv
            DataItem? designStallSpeed = null,                         // VS1 in Knoten
            DataItem? wingAreaSquareFeet = null)                       // Flügelfläche in Quadratfuß
        {
            try
            {
                if (!LokalAutopilotEnabled || simconnect == null || !trimDefinitionsRegistered) return;
                if (!TryGetFiniteValue(simulatorAutopilotMaster, out double simulatorMaster) || simulatorMaster != 0.0)
                {
                    ResetCruiseControlState();
                    return;
                }

                long now = Stopwatch.GetTimestamp();
                double elapsedSeconds = lastControlSampleTimestamp == 0
                    ? 0.0
                    : Stopwatch.GetElapsedTime(lastControlSampleTimestamp, now).TotalSeconds;
                lastControlSampleTimestamp = now;
                bool bankValid = TryGetFiniteValue(currentBankDeg, out double bank);
                bool verticalSpeedValid = TryGetFiniteValue(verticalSpeed, out double vs);
                bool altitudeValid = TryGetFiniteValue(currentAltFeet, out double altitude);
                bool headingValid = TryGetFiniteValue(currentHdgDeg, out double heading);
                string? title = aircraftTitle?.Value as string;
                bool modelValid = aircraftTitle == null || !string.IsNullOrWhiteSpace(title);
                bool useStickInstead = title?.Contains("Turbo Arrow", StringComparison.OrdinalIgnoreCase) == true;
                bool isAcrobatic = title?.Contains("Pitts", StringComparison.OrdinalIgnoreCase) == true;
                double weightPounds = 1000.0;
                bool weightValid = totalWeightKg != null && TryGetFiniteValue(totalWeightKg, out double weightKg) && weightKg > 0.0;
                if (weightValid)
                    weightPounds = Convert.ToDouble(totalWeightKg!.Value) * 2.2046226218487757;
                bool rollPositionValid = !useStickInstead ||
                    (currentAileronPosition != null && TryGetFiniteValue(currentAileronPosition, out double stickPosition) &&
                        stickPosition >= -100.0 && stickPosition <= 100.0);
                bool speedValid = TryGetFiniteValue(AirSpeedIndicated, out double speed) && speed >= 0.0;
                double speedChange = speedValid && previousIndicatedAirspeed.HasValue && elapsedSeconds > 0.0
                    ? (speed - previousIndicatedAirspeed.Value) / elapsedSeconds : 0.0;
                previousIndicatedAirspeed = autoThrottleEnabled && speedValid ? speed : null;
                double climbSpeed = 0.0;
                bool climbSpeedValid = designSpeedClimb != null &&
                    TryGetFiniteValue(designSpeedClimb, out climbSpeed) && climbSpeed > 0.0;
                bool climbProtectionRequired = designSpeedClimb != null && ALTControlEnabled &&
                    altitudeValid && localTargetAltDeg > altitude;
                double rollRate = bankValid && previousBankDegrees.HasValue && elapsedSeconds > 0.0
                    ? (bank - previousBankDegrees.Value) / elapsedSeconds
                    : 0.0;
                double vsChange = verticalSpeedValid && previousVerticalSpeed.HasValue && elapsedSeconds > 0.0
                    ? (vs - previousVerticalSpeed.Value) / elapsedSeconds
                    : 0.0;
                previousBankDegrees = bankValid ? bank : null;
                previousVerticalSpeed = verticalSpeedValid ? vs : null;

                if (LVLControlEnabled && HDGControlEnabled)
                    ApplyHeadingControl(currentHdgDeg, AirSpeedIndicated, currentBankDeg, currentTurnRate,
                        designStallSpeed, designCruiseSpeed, totalWeightKg, wingAreaSquareFeet, title);
                else
                    ReleaseHeadingControl();

                if (LVLControlEnabled && !HDGControlEnabled && modelValid && (!useStickInstead || weightValid) && rollPositionValid &&
                    bankValid && double.IsFinite(rollRate) &&
                    (!HDGControlEnabled || (headingValid && double.IsFinite(LocalTargetHdgDeg))) &&
                    speedValid &&
                    TryGetFiniteValue(useStickInstead ? currentAileronPosition! : currentAileronTrimPct, out double measuredRollTrim) &&
                    measuredRollTrim >= -100.0 && measuredRollTrim <= 100.0)
                {
                    if (!rollTrimInitialized)
                    {
                        rollTrimPercent = measuredRollTrim;
                        rollTrimInitialized = true;
                    }
                    double nextRollTrim = FlyLevel.CalculateRollTrim(bank, speed, rollRate,
                        useStickInstead ? measuredRollTrim : rollTrimPercent,
                        headingValid ? heading : 0.0, HDGControlEnabled ? LocalTargetHdgDeg : null,
                        useStickInstead, weightPounds, isAcrobatic);
                    if (useStickInstead)
                    {
                        int aileronCommand = (int)(-16383.0 * nextRollTrim / 100.0);
                        simconnect.TransmitClientEvent(SimConnect.SIMCONNECT_OBJECT_ID_USER,
                            EVENT_IDS.AILERON_SET, unchecked((uint)aileronCommand), DummyGroup.Group0,
                            SIMCONNECT_EVENT_FLAG.GROUPID_IS_PRIORITY);
                    }
                    else
                    {
                        simconnect.SetDataOnSimObject(WriteDefinitionId.RollTrim,
                            SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_DATA_SET_FLAG.DEFAULT, nextRollTrim / 100.0);
                    }
                    rollTrimPercent = nextRollTrim;
                }
                else
                {
                    rollTrimInitialized = false;
                    previousBankDegrees = null;
                }

                if ((ATTControlEnabled || ALTControlEnabled) && verticalSpeedValid && double.IsFinite(vsChange) &&
                    (!ALTControlEnabled || (altitudeValid && double.IsFinite(localTargetAltDeg))) &&
                    (!climbProtectionRequired || (speedValid && climbSpeedValid)) &&
                    TryGetFiniteValue(currentElevatorTrimPosition, out double measuredPitchTrim) &&
                    TryGetFiniteValue(ElevatorTrimUpLimit, out double upLimit) && upLimit > 0.0 &&
                    TryGetFiniteValue(ElevatorTrimDownLimit, out double downLimit) && downLimit != 0.0)
                {
                    if (!pitchTrimInitialized)
                    {
                        pitchTrimRadians = measuredPitchTrim;
                        pitchTrimInitialized = true;
                    }
                    double nextPitchTrim = Math.Clamp(
                        AltitudeHold.CalculatePitchTrim(vs, vsChange, pitchTrimRadians, upLimit,
                            altitudeValid ? altitude : 0.0, ALTControlEnabled ? localTargetAltDeg : null,
                            climbProtectionRequired ? speed : null, climbProtectionRequired ? climbSpeed : null),
                        -Math.Abs(downLimit), upLimit);
                    simconnect.SetDataOnSimObject(WriteDefinitionId.PitchTrim,
                        SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_DATA_SET_FLAG.DEFAULT, nextPitchTrim);
                    pitchTrimRadians = nextPitchTrim;
                }
                else
                {
                    pitchTrimInitialized = false;
                    previousVerticalSpeed = null;
                }

                if (autoThrottleEnabled && speedValid && double.IsFinite(speedChange) && weightValid && altitudeValid &&
                    (!ALTControlEnabled || double.IsFinite(localTargetAltDeg)) &&
                    designCruiseSpeed != null && TryGetFiniteValue(designCruiseSpeed, out double cruiseSpeed) && cruiseSpeed > 0.0 &&
                    engineCount != null && TryGetFiniteValue(engineCount, out double engines) &&
                    engines >= 1.0 && engines <= 4.0 && engines == Math.Truncate(engines) &&
                    engineThrottlePositions != null && engineThrottlePositions.Count >= (int)engines)
                {
                    double[] throttles = new double[(int)engines];
                    bool throttlesValid = true;
                    for (int engine = 0; engine < throttles.Length; engine++)
                    {
                        if (!TryGetFiniteValue(engineThrottlePositions[engine], out throttles[engine]) ||
                            throttles[engine] < 0.0 || throttles[engine] > 100.0)
                            throttlesValid = false;
                    }
                    if (throttlesValid)
                    {
                        double[] changes = CalculateThrottleChanges(speed, speedChange, cruiseSpeed, weightPounds,
                            altitude, ALTControlEnabled ? localTargetAltDeg : null);
                        double minimumThrottle = 25.0;
                        for (int engine = 0; engine < throttles.Length; engine++)
                        {
                            double throttle = throttles[engine];
                            foreach (double change in changes)
                            {
                                if ((change < 0.0 && throttle > minimumThrottle) || (change > 0.0 && throttle < 100.0))
                                    throttle = Math.Clamp(throttle + change, minimumThrottle, 100.0);
                            }
                            if (throttle != throttles[engine])
                                simconnect.SetDataOnSimObject((WriteDefinitionId)(10101 + engine),
                                    SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_DATA_SET_FLAG.DEFAULT, throttle);
                        }
                    }
                }
                else
                {
                    previousIndicatedAirspeed = null;
                }
            }
            catch (COMException ex)
            {
                ResetCruiseControlState();
                Debug.WriteLine($"[Debug] {DateTime.Now:yyyy-MM-dd HH:mm:ss} - Exception in ProcessCurrentValues: {ex}");
            }
        }   
    }

    /// <summary>HDG-Regelung mit begrenzter Bank und Rollratendämpfung; unabhängig von SimConnect.</summary>
    public sealed class HeadingTurnController
    {
        private HeadingBankState bankState = new();
        private HeadingHoldState holdState = new();
        private double? previousHoldTarget;

        /// <summary>Aktuelle Grenze des Querruderbefehls in SimConnect-Achseinheiten.</summary>
        public double MaximumDeflection { get; private set; }

        /// <summary>Zuletzt berechnete Solldrehrate in Grad pro Sekunde, rechts positiv.</summary>
        public double TargetTurnRate { get; private set; }

        /// <summary>Sollbank in Grad, links positiv; auf +/-20 Grad begrenzt.</summary>
        public double TargetBankDegrees { get; private set; }

        /// <summary>Verwirft die flugzeugabhängige Anpassung bei Modus- oder Flugzeugwechsel.</summary>
        public void Reset()
        {
            bankState = new();
            holdState = new();
            previousHoldTarget = null;
            MaximumDeflection = 0.0;
            TargetTurnRate = 0.0;
            TargetBankDegrees = 0.0;
        }

        /// <summary>
        /// Berechnet AILERON_SET: negativer Befehl dreht rechts, positive Drehrate bedeutet rechts.
        /// Gewicht in kg, Flügelfläche in Quadratfuß, Geschwindigkeiten in Knoten, Kurse in Grad.
        /// turnRateDegreesPerSecond ist die zeitgestützte Kursänderung, nicht TURN INDICATOR RATE.
        /// Bank und Rollrate sind links positiv; ihre Standardwerte beschreiben waagerechten Flug.
        /// Die IAS dient nur zur konservativen Sollbank-Vorsteuerung, nicht als TAS-Messung.
        /// Ungültige Eingaben verändern den Zustand nicht.
        /// </summary>
        public int Calculate(double currentHeadingDegrees, double targetHeadingDegrees,
            double turnRateDegreesPerSecond, double speedKnots, double stallSpeedKnots,
            double cruiseSpeedKnots, double weightKg, double wingAreaSquareFeet,
            bool isAcrobatic, double elapsedSeconds = 0.5,
            double bankDegrees = 0.0, double rollRateDegreesPerSecond = 0.0)
        {
            RequireFinite(currentHeadingDegrees, nameof(currentHeadingDegrees));
            RequireFinite(targetHeadingDegrees, nameof(targetHeadingDegrees));
            RequireFinite(turnRateDegreesPerSecond, nameof(turnRateDegreesPerSecond));
            RequireFinite(speedKnots, nameof(speedKnots));
            ArgumentOutOfRangeException.ThrowIfNegative(speedKnots);
            RequirePositive(stallSpeedKnots, nameof(stallSpeedKnots));
            RequirePositive(cruiseSpeedKnots, nameof(cruiseSpeedKnots));
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(cruiseSpeedKnots, stallSpeedKnots);
            RequirePositive(weightKg, nameof(weightKg));
            RequirePositive(wingAreaSquareFeet, nameof(wingAreaSquareFeet));
            RequirePositive(elapsedSeconds, nameof(elapsedSeconds));
            ArgumentOutOfRangeException.ThrowIfGreaterThan(elapsedSeconds, 2.0);
            RequireFinite(bankDegrees, nameof(bankDegrees));
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(Math.Abs(bankDegrees), 90.0, nameof(bankDegrees));
            RequireFinite(rollRateDegreesPerSecond, nameof(rollRateDegreesPerSecond));
            double wingLoading = weightKg * 2.2046226218487757 / wingAreaSquareFeet;
            RequirePositive(wingLoading, nameof(weightKg));

            bool ultralight = wingLoading < 5.0;
            bool twitchy = isAcrobatic || ultralight;
            // Keine Aufschaukelung der Stellverstärkung bei langsamer/falsch gerichteter Anzeige.
            // Die frühere Grenze konnte dabei bis auf 8192 Achseinheiten anwachsen.
            MaximumDeflection = twitchy ? 300.0 : AutoPilot.FlightMath.ConstrainMap(
                wingLoading, 4.0, 20.0, 600.0, 1500.0);
            double speedFactor = AutoPilot.FlightMath.ConstrainMap(
                speedKnots, stallSpeedKnots, cruiseSpeedKnots, 0.5, 1.0);
            double headingDiff = AutoPilot.FlightMath.GetCompassDiff(currentHeadingDegrees,
                targetHeadingDegrees) * speedFactor;
            double maxTurnRate = ultralight ? 1.0 : 3.0;
            // 10 s statt 30 s: bewegliche Sollkurse nicht mit fast waagerechten Flügeln verfolgen.
            // Vorhalt aus echter HDG-Änderung bleibt erhalten; die Sollbank begrenzt zusätzlich.
            double anticipatedTurnRate = headingDiff / 10.0 - 0.5 * turnRateDegreesPerSecond;
            // Jede echte Solländerung verwirft den gelernten Kursvorhalt; Wrap 0/360 bleibt gleich.
            if (!previousHoldTarget.HasValue ||
                Math.Abs(AutoPilot.FlightMath.GetCompassDiff(previousHoldTarget.Value, targetHeadingDegrees)) > 1e-6)
                holdState = new();
            previousHoldTarget = targetHeadingDegrees;
            HeadingHoldOutput holdOutput = HeadingHoldController.Update(holdState,
                new(headingDiff, elapsedSeconds, anticipatedTurnRate, maxTurnRate));
            HeadingHoldState nextHoldState = holdOutput.State;
            TargetTurnRate = holdOutput.TurnRate;

            // Querruder steuert die Rollbewegung, nicht unmittelbar die Kursdrehrate.
            // Bankrückführung verhindert weiteres Einrollen bei bereits ausreichender Bank.
            double bankLimit = 20.0;
            TargetBankDegrees = Math.Clamp(-Math.Atan(speedKnots * 0.5144444444444445 *
                (TargetTurnRate * Math.PI / 180.0) / 9.80665) * 180.0 / Math.PI, -bankLimit, bankLimit);
            HeadingBankOutput bankOutput = HeadingBankController.Update(bankState,
                new(TargetBankDegrees, bankDegrees, rollRateDegreesPerSecond, elapsedSeconds,
                    MaximumBankDegrees: bankLimit));
            // Kein äußeres Auflaufen bei Bank-/Achsenbegrenzung. Gegenläufiger Abbau bleibt erlaubt.
            bool blocked = Math.Abs(bankOutput.NormalizedCommand) >= 1 ||
                Math.Abs(TargetBankDegrees) >= bankLimit || Math.Abs(bankDegrees) >= bankLimit;
            if (!blocked || (nextHoldState.IntegralRate - holdState.IntegralRate) * holdState.IntegralRate < 0)
                holdState = nextHoldState;
            bankState = bankOutput.State;
            int command = (int)(MaximumDeflection * bankOutput.NormalizedCommand);
            return command;
        }

        private static void RequireFinite(double value, string parameterName)
        {
            if (!double.IsFinite(value))
                throw new ArgumentOutOfRangeException(parameterName);
        }

        private static void RequirePositive(double value, string parameterName)
        {
            RequireFinite(value, parameterName);
            if (value <= 0.0)
                throw new ArgumentOutOfRangeException(parameterName);
        }
    }

    /// <summary>Äußerer Kursausgleich in Grad/s, unabhängig vom Bank-I-Anteil.</summary>
    public sealed record HeadingHoldState(double IntegralRate = 0);
    public sealed record HeadingHoldInput(double ErrorDegrees, double ElapsedSeconds,
        double BaseTurnRate, double MaximumTurnRate, bool CorrectionBlocked = false);
    public sealed record HeadingHoldOutput(HeadingHoldState State, double TurnRate);

    /// <summary>Reiner Restfehlerausgleich. Vorläufige lokale Auslegung, kein identifiziertes Optimum.</summary>
    public static class HeadingHoldController
    {
        public const double IntegralGain = 0.003; // 1/s²: zur P-Verstärkung 0.1/s ergibt sich Ti≈33,3 s.
        public const double MaximumIntegralRate = 0.15; // Grad/s, begrenzt den gespeicherten Vorhalt.
        public const double CaptureErrorDegrees = 2.0; // Nur nahe am Ziel lernen, nicht im großen Kurswechsel.

        public static HeadingHoldOutput Update(HeadingHoldState state, HeadingHoldInput input)
        {
            ArgumentNullException.ThrowIfNull(state);
            ArgumentNullException.ThrowIfNull(input);
            if (!double.IsFinite(state.IntegralRate) || Math.Abs(state.IntegralRate) > MaximumIntegralRate ||
                !double.IsFinite(input.ErrorDegrees) || !double.IsFinite(input.BaseTurnRate) ||
                !double.IsFinite(input.ElapsedSeconds) || input.ElapsedSeconds <= 0 || input.ElapsedSeconds > 2 ||
                !double.IsFinite(input.MaximumTurnRate) || input.MaximumTurnRate <= 0)
                throw new ArgumentOutOfRangeException(nameof(input));
            double delta = Math.Abs(input.ErrorDegrees) <= CaptureErrorDegrees
                ? IntegralGain * input.ErrorDegrees * input.ElapsedSeconds : 0;
            double integral = Math.Clamp(state.IntegralRate + delta, -MaximumIntegralRate, MaximumIntegralRate);
            double requested = input.BaseTurnRate + integral;
            bool unwinds = delta * state.IntegralRate < 0;
            if ((input.CorrectionBlocked && !unwinds) ||
                (requested > input.MaximumTurnRate && delta > 0) ||
                (requested < -input.MaximumTurnRate && delta < 0))
                integral = state.IntegralRate;
            return new(new(integral), Math.Clamp(input.BaseTurnRate + integral,
                -input.MaximumTurnRate, input.MaximumTurnRate));
        }
    }

    /// <summary>Persistenter, normierter Stellanteil; eine neue Instanz verwirft den Ausgleich.</summary>
    public sealed record HeadingBankState(double IntegralCommand = 0.0);

    /// <summary>Bank links positiv, Zeit in Sekunden; Gain ist eine vorläufige lokale Auslegung.</summary>
    public sealed record HeadingBankInput(double TargetBankDegrees, double BankDegrees,
        double RollRateDegreesPerSecond, double ElapsedSeconds, double IntegralGain = 0.005,
        double MaximumBankDegrees = 20.0);

    public sealed record HeadingBankOutput(HeadingBankState State, double NormalizedCommand);

    /// <summary>Reiner Bank-PI-/Rollratenschritt ohne Simulatorzugriff.</summary>
    public static class HeadingBankController
    {
        public static HeadingBankOutput Update(HeadingBankState state, HeadingBankInput input)
        {
            ArgumentNullException.ThrowIfNull(state);
            ArgumentNullException.ThrowIfNull(input);
            if (!double.IsFinite(state.IntegralCommand) || Math.Abs(state.IntegralCommand) > 1 ||
                !double.IsFinite(input.MaximumBankDegrees) || input.MaximumBankDegrees < 20 || input.MaximumBankDegrees > 25 ||
                !double.IsFinite(input.TargetBankDegrees) || Math.Abs(input.TargetBankDegrees) > input.MaximumBankDegrees ||
                !double.IsFinite(input.BankDegrees) || Math.Abs(input.BankDegrees) >= 90 ||
                !double.IsFinite(input.RollRateDegreesPerSecond) ||
                !double.IsFinite(input.ElapsedSeconds) || input.ElapsedSeconds <= 0 || input.ElapsedSeconds > 2 ||
                !double.IsFinite(input.IntegralGain) || input.IntegralGain < 0 || input.IntegralGain > 0.1)
                throw new ArgumentOutOfRangeException(nameof(input));

            double error = input.TargetBankDegrees - input.BankDegrees;
            double proportional = (error - 2 * input.RollRateDegreesPerSecond) / 10;
            double delta = input.IntegralGain * error * input.ElapsedSeconds;
            // Lokale Stellgrenzen entsprechen dem bestehenden normierten Achsenbereich.
            double integral = Math.Clamp(state.IntegralCommand + delta, -1, 1);
            double requested = proportional + integral;
            // Nicht weiter in eine aktive Begrenzung integrieren; Entsättigung bleibt möglich.
            bool pushesLimit = (requested > 1 && delta > 0) || (requested < -1 && delta < 0) ||
                (input.BankDegrees >= input.MaximumBankDegrees && requested > 0 && delta > 0) ||
                (input.BankDegrees <= -input.MaximumBankDegrees && requested < 0 && delta < 0);
            if (pushesLimit)
                integral = state.IntegralCommand;

            double command = Math.Clamp(proportional + integral, -1, 1);
            if (input.BankDegrees >= input.MaximumBankDegrees) command = Math.Min(command, 0);
            if (input.BankDegrees <= -input.MaximumBankDegrees) command = Math.Max(command, 0);
            return new(new(integral), command);
        }
    }
}
