using Microsoft.FlightSimulator.SimConnect;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Resources;
using System.Runtime.InteropServices;

namespace AutoFly
{
    public partial class AutoflyMain : Form
    {
        // Konstante für die Windows-Nachrichtenschleife (SimConnect-Kommunikation)
        private const int WM_USER_SIMCONNECT = 0x0402;
        private SimConnect? simconnect = null;
        private AutoPilot? autopilot;
        private readonly System.Windows.Forms.Timer autopilotTimer;
        private Task terrainFollowShutdownTask = Task.CompletedTask;
        private bool terrainFollowClosePending;
        private bool terrainFollowCloseReady;
        private readonly IReadOnlyList<DataItem> engineThrottlePositions = Array.AsReadOnly<DataItem>(
        [
            Flight_Data.GENERAL_ENG_THROTTLE_LEVER_POSITION_1,   //Triebwerk 1 Schubhebelposition (percent)
            Flight_Data.GENERAL_ENG_THROTTLE_LEVER_POSITION_2,   //Triebwerk 2 Schubhebelposition (percent)
            Flight_Data.GENERAL_ENG_THROTTLE_LEVER_POSITION_3,   //Triebwerk 3 Schubhebelposition (percent)
            Flight_Data.GENERAL_ENG_THROTTLE_LEVER_POSITION_4    //Triebwerk 4 Schubhebelposition (percent)
        ]);
        private enum SimConnectRequestId : uint
        {
            Dummy = 0
        }
        private enum SimConnectDefinitionId : uint
        {
            Dummy = 0
        }
        private bool _isPlaneCenterActive = true;
        private bool _isLocalAutopilotActive = false;
        private bool _isHDGActive = false;
        private bool _isALTActive = false;
        private bool _isATTActive = false;
        private bool _isLVLActive = false;
        private bool _isAutoThrottleActive = false;
        private bool _isWayPointFollow = false;
        private int _targetAltitude = 0;
        private int _targetHeading = 0;
        private double? _lastSentLat = null;
        private double? _lastSentLon = null;


        // String properties must be packed inside of a struct
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
        struct Struct1
        {
            // this is how you declare a fixed size string
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public System.String sValue;

            // other definitions can be added to this struct
            // ...
        };

        public class DataItem(uint requestId = 0, uint definitionId = 0, string unit = "", SIMCONNECT_DATATYPE dataType = SIMCONNECT_DATATYPE.FLOAT64, string name = "", object? value = null)
        {
            public uint RequestId { get; set; } = requestId;
            public uint DefinitionId { get; set; } = definitionId;
            public string Unit { get; set; } = unit;
            public string Name { get; set; } = name ?? string.Empty;
            public SIMCONNECT_DATATYPE DataType { get; set; } = dataType;
            public bool UpDated { get; set; } = false;
            public DateTime LastUpdated { get; set; } = DateTime.UtcNow;

            private object? _value = value;
            public object? Value
            {
                get => _value;
                set
                {
                    if (value == null) return;
                    // Status aktualisieren und neuen Wert schreiben
                    UpDated = true;
                    _value = value;
                    LastUpdated = DateTime.UtcNow;
                }
            }
        }

        private static class Flight_Model
        {
            public static readonly DataItem CATEGORY = new(1000, 100, "", SIMCONNECT_DATATYPE.STRING256, "CATEGORY", null);
            public static readonly DataItem DESIGN_CRUISE_ALT = new(1001, 101, "feet", SIMCONNECT_DATATYPE.FLOAT64, "DESIGN CRUISE ALT", null);
            public static readonly DataItem DESIGN_SPEED_CLIMB = new(1002, 102, "knots", SIMCONNECT_DATATYPE.FLOAT64, "DESIGN SPEED CLIMB", null);
            public static readonly DataItem DESIGN_SPEED_MIN_ROTATION = new(1003, 103, "knots", SIMCONNECT_DATATYPE.FLOAT64, "DESIGN SPEED MIN ROTATION", null);
            public static readonly DataItem DESIGN_SPEED_VC = new(1004, 104, "knots", SIMCONNECT_DATATYPE.FLOAT64, "DESIGN SPEED VC", null);
            public static readonly DataItem DESIGN_SPEED_VS0 = new(1005, 105, "knots", SIMCONNECT_DATATYPE.FLOAT64, "DESIGN SPEED VS0", null);
            public static readonly DataItem DESIGN_SPEED_VS1 = new(1006, 106, "knots", SIMCONNECT_DATATYPE.FLOAT64, "DESIGN SPEED VS1", null);
            public static readonly DataItem REFERENCE_SPEED_MAX_IAS = new(1025, 125, "knots", SIMCONNECT_DATATYPE.FLOAT64, "REFERENCE SPEED MAX IAS", null);
            public static readonly DataItem DESIGN_TAKEOFF_SPEED = new(1007, 107, "knots", SIMCONNECT_DATATYPE.FLOAT64, "DESIGN TAKEOFF SPEED", null);
            public static readonly DataItem ELEVATOR_TRIM_DOWN_LIMIT = new(1008, 108, "radians", SIMCONNECT_DATATYPE.FLOAT64, "ELEVATOR TRIM DOWN LIMIT", null);
            public static readonly DataItem ELEVATOR_TRIM_UP_LIMIT = new(1009, 109, "radians", SIMCONNECT_DATATYPE.FLOAT64, "ELEVATOR TRIM UP LIMIT", null);
            public static readonly DataItem ENGINE_TYPE = new(1010, 110, "", SIMCONNECT_DATATYPE.STRING256, "ENGINE TYPE", null);
            public static readonly DataItem INCIDENCE_ALPHA = new(1011, 111, "degrees", SIMCONNECT_DATATYPE.FLOAT64, "INCIDENCE ALPHA", null);
            public static readonly DataItem IS_GEAR_FLOATS = new(1012, 112, "bool", SIMCONNECT_DATATYPE.INT32, "IS GEAR FLOATS", null);
            public static readonly DataItem IS_GEAR_RETRACTABLE = new(1013, 113, "bool", SIMCONNECT_DATATYPE.INT32, "IS GEAR RETRACTABLE", null);
            public static readonly DataItem IS_TAIL_DRAGGER = new(1014, 114, "bool", SIMCONNECT_DATATYPE.INT32, "IS TAIL DRAGGER", null);
            public static readonly DataItem NUMBER_OF_ENGINES = new(1015, 115, "number", SIMCONNECT_DATATYPE.INT32, "NUMBER OF ENGINES", null);
            public static readonly DataItem PLANE_LATITUDE = new(1016, 116, "degrees", SIMCONNECT_DATATYPE.FLOAT64, "PLANE LATITUDE", null);
            public static readonly DataItem PLANE_LONGITUDE = new(1017, 117, "degrees", SIMCONNECT_DATATYPE.FLOAT64, "PLANE LONGITUDE", null);
            public static readonly DataItem STALL_ALPHA = new(1018, 118, "degrees", SIMCONNECT_DATATYPE.FLOAT64, "STALL ALPHA", null);
            public static readonly DataItem STATIC_CG_TO_GROUND = new(1019, 119, "feet", SIMCONNECT_DATATYPE.FLOAT64, "STATIC CG TO GROUND", null);
            public static readonly DataItem TITLE = new(1020, 120, "", SIMCONNECT_DATATYPE.STRING256, "TITLE", null);
            public static readonly DataItem TOTAL_WEIGHT = new(1021, 121, "kg", SIMCONNECT_DATATYPE.FLOAT64, "TOTAL WEIGHT", null);
            public static readonly DataItem TYPICAL_DESCENT_RATE = new(1022, 122, "feet per minute", SIMCONNECT_DATATYPE.FLOAT64, "TYPICAL DESCENT RATE", null);
            public static readonly DataItem WING_AREA = new(1023, 123, "square feet", SIMCONNECT_DATATYPE.FLOAT64, "WING AREA", null);
            public static readonly DataItem WING_SPAN = new(1024, 124, "feet", SIMCONNECT_DATATYPE.FLOAT64, "WING SPAN", null);
        }

        private static class Flight_Data
        {
            public static readonly DataItem AILERON_POSITION = new(2000, 200, "percent", SIMCONNECT_DATATYPE.FLOAT64, "AILERON POSITION", null);
            public static readonly DataItem AILERON_TRIM_PCT = new(2001, 201, "percent", SIMCONNECT_DATATYPE.FLOAT64, "AILERON TRIM PCT", null);
            public static readonly DataItem AIRSPEED_INDICATED = new(2002, 202, "knots", SIMCONNECT_DATATYPE.FLOAT64, "AIRSPEED INDICATED", null);
            public static readonly DataItem AIRSPEED_TRUE = new(2003, 203, "knots", SIMCONNECT_DATATYPE.FLOAT64, "AIRSPEED TRUE", null);
            public static readonly DataItem AUTOPILOT_HEADING_LOCK_DIR = new(2004, 204, "degrees", SIMCONNECT_DATATYPE.FLOAT64, "AUTOPILOT HEADING LOCK DIR", null);
            public static readonly DataItem AUTOPILOT_MASTER = new(2005, 205, "bool", SIMCONNECT_DATATYPE.INT32, "AUTOPILOT MASTER", null);
            public static readonly DataItem CAMERA_STATE = new(2006, 206, "position", SIMCONNECT_DATATYPE.INT32, "CAMERA STATE", null);
            public static readonly DataItem CAMERA_SUBSTATE = new(2007, 207, "position", SIMCONNECT_DATATYPE.INT32, "CAMERA SUBSTATE", null);
            public static readonly DataItem CRASH_FLAG = new(2008, 208, "bool", SIMCONNECT_DATATYPE.INT32, "CRASH FLAG", null);
            public static readonly DataItem CRASH_SEQUENCE = new(2009, 209, "bool", SIMCONNECT_DATATYPE.INT32, "CRASH SEQUENCE", null);
            public static readonly DataItem ELECTRICAL_AVIONICS_BUS_VOLTAGE = new(2010, 210, "volts", SIMCONNECT_DATATYPE.FLOAT64, "ELECTRICAL AVIONICS BUS VOLTAGE", null);
            public static readonly DataItem ELECTRICAL_TOTAL_LOAD_AMPS = new(2011, 211, "amperes", SIMCONNECT_DATATYPE.FLOAT64, "ELECTRICAL TOTAL LOAD AMPS", null);
            public static readonly DataItem ELEVATOR_POSITION = new(2012, 212, "percent", SIMCONNECT_DATATYPE.FLOAT64, "ELEVATOR POSITION", null);
            public static readonly DataItem ELEVATOR_TRIM_PCT = new(2013, 213, "percent", SIMCONNECT_DATATYPE.FLOAT64, "ELEVATOR TRIM PCT", null);
            public static readonly DataItem ELEVATOR_TRIM_POSITION = new(2014, 214, "radians", SIMCONNECT_DATATYPE.FLOAT64, "ELEVATOR TRIM POSITION", null);
            public static readonly DataItem ENG_COMBUSTION_1 = new(2015, 215, "bool", SIMCONNECT_DATATYPE.INT32, "ENG COMBUSTION:1", null);
            public static readonly DataItem ENG_COMBUSTION_2 = new(2016, 216, "bool", SIMCONNECT_DATATYPE.INT32, "ENG COMBUSTION:2", null);
            public static readonly DataItem ENG_COMBUSTION_3 = new(2017, 217, "bool", SIMCONNECT_DATATYPE.INT32, "ENG COMBUSTION:3", null);
            public static readonly DataItem ENG_COMBUSTION_4 = new(2018, 218, "bool", SIMCONNECT_DATATYPE.INT32, "ENG COMBUSTION:4", null);
            public static readonly DataItem GEAR_POSITION_1 = new(2019, 219, "bool", SIMCONNECT_DATATYPE.FLOAT64, "GEAR POSITION:1", null);
            public static readonly DataItem GEAR_SPEED_EXCEEDED = new(2020, 220, "bool", SIMCONNECT_DATATYPE.INT32, "GEAR SPEED EXCEEDED", null);
            public static readonly DataItem GROUND_ALTITUDE = new(2021, 221, "feet", SIMCONNECT_DATATYPE.FLOAT64, "GROUND ALTITUDE", null);
            public static readonly DataItem INDICATED_ALTITUDE = new(2022, 222, "feet", SIMCONNECT_DATATYPE.FLOAT64, "INDICATED ALTITUDE", null);
            public static readonly DataItem OVERSPEED_WARNING = new(2023, 223, "bool", SIMCONNECT_DATATYPE.INT32, "OVERSPEED WARNING", null);
            public static readonly DataItem PLANE_ALT_ABOVE_GROUND_MINUS_CG = new(2024, 224, "feet", SIMCONNECT_DATATYPE.FLOAT64, "PLANE ALT ABOVE GROUND MINUS CG", null);
            public static readonly DataItem PLANE_ALT_ABOVE_GROUND = new(2025, 225, "feet", SIMCONNECT_DATATYPE.FLOAT64, "PLANE ALT ABOVE GROUND", null);
            public static readonly DataItem PLANE_BANK_DEGREES = new(2026, 226, "degrees", SIMCONNECT_DATATYPE.FLOAT64, "PLANE BANK DEGREES", null);
            public static readonly DataItem PLANE_HEADING_DEGREES_MAGNETIC = new(2027, 227, "degrees", SIMCONNECT_DATATYPE.FLOAT64, "PLANE HEADING DEGREES MAGNETIC", null);
            public static readonly DataItem PLANE_HEADING_DEGREES_TRUE = new(2028, 228, "degrees", SIMCONNECT_DATATYPE.FLOAT64, "PLANE HEADING DEGREES TRUE", null);
            public static readonly DataItem PLANE_LATITUDE = new(2029, 229, "degrees", SIMCONNECT_DATATYPE.FLOAT64, "PLANE LATITUDE", null);
            public static readonly DataItem PLANE_LONGITUDE = new(2030, 230, "degrees", SIMCONNECT_DATATYPE.FLOAT64, "PLANE LONGITUDE", null);
            public static readonly DataItem PLANE_PITCH_DEGREES = new(2031, 231, "degrees", SIMCONNECT_DATATYPE.FLOAT64, "PLANE PITCH DEGREES", null);
            public static readonly DataItem RUDDER_POSITION = new(2032, 232, "percent", SIMCONNECT_DATATYPE.FLOAT64, "RUDDER POSITION", null);
            public static readonly DataItem RUDDER_TRIM_PCT = new(2033, 233, "percent", SIMCONNECT_DATATYPE.FLOAT64, "RUDDER TRIM PCT", null);
            public static readonly DataItem SIM_ON_GROUND = new(2034, 234, "bool", SIMCONNECT_DATATYPE.INT32, "SIM ON GROUND", null);
            public static readonly DataItem TURN_INDICATOR_RATE = new(2035, 235, "degrees per second", SIMCONNECT_DATATYPE.FLOAT64, "TURN INDICATOR RATE", null);
            public static readonly DataItem VERTICAL_SPEED = new(2036, 236, "feet per minute", SIMCONNECT_DATATYPE.FLOAT64, "VERTICAL SPEED", null);
            public static readonly DataItem AUTOPILOT_ALTITUDE_LOCK_VAR = new(2037, 237, "feet", SIMCONNECT_DATATYPE.FLOAT64, "AUTOPILOT ALTITUDE LOCK VAR", null);
            public static readonly DataItem PLANE_ALTITUDE = new(2038, 238, "feet", SIMCONNECT_DATATYPE.FLOAT64, "PLANE ALTITUDE", null);
            public static readonly DataItem GENERAL_ENG_THROTTLE_LEVER_POSITION_1 = new(2039, 239, "percent", SIMCONNECT_DATATYPE.FLOAT64, "GENERAL ENG THROTTLE LEVER POSITION:1", null);
            public static readonly DataItem GENERAL_ENG_THROTTLE_LEVER_POSITION_2 = new(2040, 240, "percent", SIMCONNECT_DATATYPE.FLOAT64, "GENERAL ENG THROTTLE LEVER POSITION:2", null);
            public static readonly DataItem GENERAL_ENG_THROTTLE_LEVER_POSITION_3 = new(2041, 241, "percent", SIMCONNECT_DATATYPE.FLOAT64, "GENERAL ENG THROTTLE LEVER POSITION:3", null);
            public static readonly DataItem GENERAL_ENG_THROTTLE_LEVER_POSITION_4 = new(2042, 242, "percent", SIMCONNECT_DATATYPE.FLOAT64, "GENERAL ENG THROTTLE LEVER POSITION:4", null);
            public static readonly DataItem GPS_GROUND_TRUE_TRACK = new(2043, 243, "degrees", SIMCONNECT_DATATYPE.FLOAT64, "GPS GROUND TRUE TRACK", null);

        }

        public AutoflyMain()
        {
            LoadLanguagePreference();
            InitializeComponent();
            tsbGerman.Click += TsbGerman_Click;
            tsbEnglish.Click += TsbEnglish_Click;
            tsbInfo.Click += TsbInfo_Click;
            InitializeAutostartControls();
            TsbAutoElevation.Image = null;
            Shown += TerrainFollow_ShownAsync;
            Disposed += TerrainFollow_DisposedAsync;
            InitializeWaypointControls();
            InitializeAutoLandControls();
            ApplyLanguage();
            components ??= new System.ComponentModel.Container();
            autopilotTimer = new System.Windows.Forms.Timer(components)
            {
                Interval = 500
            };
            autopilotTimer.Tick += AutopilotTimer_Tick;
            autostartTimer = new System.Windows.Forms.Timer(components)
            {
                Interval = 100
            };
            autostartTimer.Tick += AutostartTimer_Tick;
            ShowStatus(Resources_DE.GetString("MainForm_Starting"));
            InitializeAutopilotTargets();
            TryConnect();
        }

        private void InitializeAutopilotTargets()
        {
            if (double.TryParse(tstbHeading.Text, NumberStyles.Float | NumberStyles.AllowThousands,
                CultureInfo.InvariantCulture, out double heading) && double.IsFinite(heading))
                _targetHeading = RoundTargetHeading(heading);
            if (double.TryParse(tstbAltitude.Text, NumberStyles.Float | NumberStyles.AllowThousands,
                CultureInfo.InvariantCulture, out double altitude) && double.IsFinite(altitude) &&
                altitude >= int.MinValue && altitude <= int.MaxValue)
                _targetAltitude = RoundTargetAltitude(altitude);
            tstbHeading.Text = _targetHeading.ToString(CultureInfo.InvariantCulture);
            tstbAltitude.Text = _targetAltitude.ToString(CultureInfo.InvariantCulture);
        }

        private static int RoundTargetAltitude(double altitudeFeet) =>
            checked((int)Math.Round(altitudeFeet, MidpointRounding.AwayFromZero));

        private static int RoundTargetHeading(double headingDegrees) =>
            checked((int)Math.Round((headingDegrees % 360.0 + 360.0) % 360.0,
                MidpointRounding.AwayFromZero)) % 360;

        private async void TryConnect()
        {
            try
            {
                //************************************************************
                //********** Verbindung zu MSFS herstellen *******************
                //************************************************************
                ShowStatus(Resources_DE.GetString("MainForm_Connecting"));
                simconnect = new SimConnect("Autofly", this.Handle, WM_USER_SIMCONNECT, null, 0);
                simconnect.OnRecvOpen += new SimConnect.RecvOpenEventHandler(SimConnect_OnRecvOpen);
                simconnect.OnRecvQuit += new SimConnect.RecvQuitEventHandler(SimConnect_OnRecvQuit);
                simconnect.OnRecvException += new SimConnect.RecvExceptionEventHandler(SimConnect_OnRecvException);
                simconnect.OnRecvSimobjectData += new SimConnect.RecvSimobjectDataEventHandler(Simconnect_OnRecvSimobjectData);
                //Debug.WriteLine($"[Debug] {DateTime.Now:yyyy-MM-dd HH:mm:ss} - TryConnect: Event-Handler registriert");

                // Benutzer informieren
                ShowStatus(Resources_DE.GetString("MainForm_WaitingForConnection"));
            }
            catch (COMException)
            {
                // MSFS läuft nicht: Panel zeigt Hinweis und schließt sich sauber nach Beenden des MessageBox-Dialogs
                ShowStatus(Resources_DE.GetString("MainForm_SimulatorNotRunning"));
                MessageBox.Show(Resources_DE.GetString("MainForm_SimulatorNotRunning"), Resources_DE.GetString("MainForm_SimulatorNotFoundTitle"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                Environment.Exit(1); // sofort beenden
                return;
            }
            //*************************************************************
            //********** WebView2 initialisieren und Karte laden **********
            //*************************************************************
            try
            {
                // WebView2 initialisieren
                await webView2.EnsureCoreWebView2Async();
                InitializeWaypointNavigation();
                // Filter mit Slash/Wildcard setzen
                webView2.CoreWebView2.AddWebResourceRequestedFilter("http://autofly.local/*", Microsoft.Web.WebView2.Core.CoreWebView2WebResourceContext.All);
                // Event-Handler verknüpfen
                webView2.CoreWebView2.WebResourceRequested += WebView2_WebResourceRequested;
                // Korrekte URL aufrufen (Domain + Pfad)
                webView2.CoreWebView2.Navigate("http://autofly.local/map.html");
            }
            catch (Exception ex)
            {
                ShowStatus(Resources_DE.Format("MainForm_WebViewError", ex.Message));
            }
        }

        private void WebView2_WebResourceRequested(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2WebResourceRequestedEventArgs e)
        {
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                var names = asm.GetManifestResourceNames();
                //Debug.WriteLine($"[Debug] {DateTime.Now:yyyy-MM-dd HH:mm:ss} - Resources: " + string.Join(", ", names));

                string uri = e.Request?.Uri ?? string.Empty;
                if (string.IsNullOrEmpty(uri)) return;

                // Dateiname aus der URL extrahieren (z.B. "map.html", "leaflet.js", "plane.png")
                string requestedFile;
                try
                {
                    requestedFile = System.IO.Path.GetFileName(new Uri(uri).AbsolutePath);
                }
                catch
                {
                    requestedFile = System.IO.Path.GetFileName(uri);
                }

                if (string.IsNullOrEmpty(requestedFile)) return;

                // Manifest-Namen case-insensitiv suchen
                string? found = Array.Find(names, n => n.EndsWith("." + requestedFile, StringComparison.OrdinalIgnoreCase));
                if (found != null)
                {
                    var stream = asm.GetManifestResourceStream(found);
                    if (stream != null)
                    {
                        if (stream.CanSeek) stream.Position = 0;
                        string contentType = requestedFile.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ? "text/html; charset=utf-8"
                                          : requestedFile.EndsWith(".js", StringComparison.OrdinalIgnoreCase) ? "application/javascript"
                                          : requestedFile.EndsWith(".css", StringComparison.OrdinalIgnoreCase) ? "text/css"
                                          : requestedFile.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png"
                                          : "application/octet-stream";

                        stream = LocalizeMapResponse(stream, requestedFile);
                        e.Response = webView2.CoreWebView2.Environment.CreateWebResourceResponse(stream, 200, "OK", $"Content-Type: {contentType}");
                        //Debug.WriteLine($"[Debug] {DateTime.Now:yyyy-MM-dd HH:mm:ss} - Delivered embedded resource: {found}");
                        return;
                    }
                    //Debug.WriteLine($"[Debug] {DateTime.Now:yyyy-MM-dd HH:mm:ss} - ManifestStream null für: {found}");
                }
                else
                {
                    Debug.WriteLine($"[Debug] {DateTime.Now:yyyy-MM-dd HH:mm:ss} - Keine eingebettete Ressource gefunden für: {requestedFile}");
                }

                // Debug-Fallback: lokale Datei aus BaseDirectory liefern, falls vorhanden
                string localPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, requestedFile);
                if (System.IO.File.Exists(localPath))
                {
                    System.IO.Stream fileStream = System.IO.File.OpenRead(localPath);
                    string contentType = requestedFile.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ? "text/html; charset=utf-8"
                                      : requestedFile.EndsWith(".js", StringComparison.OrdinalIgnoreCase) ? "application/javascript"
                                      : requestedFile.EndsWith(".css", StringComparison.OrdinalIgnoreCase) ? "text/css"
                                      : requestedFile.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png"
                                      : "application/octet-stream";

                    fileStream = LocalizeMapResponse(fileStream, requestedFile);
                    e.Response = webView2.CoreWebView2.Environment.CreateWebResourceResponse(fileStream, 200, "OK", $"Content-Type: {contentType}");
                    //Debug.WriteLine($"[Debug] {DateTime.Now:yyyy-MM-dd HH:mm:ss} - Delivered local file: {localPath}");
                    return;
                }

                Debug.WriteLine($"[Debug] {DateTime.Now:yyyy-MM-dd HH:mm:ss} - Resource {requestedFile} nicht gefunden (embedded/local).");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Debug] {DateTime.Now:yyyy-MM-dd HH:mm:ss} - WebResourceRequested-Fehler: {ex.Message}");
            }
        }

        private static System.IO.Stream LocalizeMapResponse(System.IO.Stream source, string requestedFile)
        {
            if (!string.Equals(requestedFile, "map.html", StringComparison.OrdinalIgnoreCase))
                return source;

            using System.IO.StreamReader reader = new(source, System.Text.Encoding.UTF8);
            string html = reader.ReadToEnd();
            string[] keys =
            [
                "Title", "Horizon", "OuterRose", "InnerRose", "AutopilotCourse", "Plane",
                "ZoomIn", "ZoomOut", "ClosePopup", "OpenStreetMap", "Hillshade",
                "NauticalMiles", "Feet", "Knots", "AltitudeMissing", "SpeedMissing"
            ];
            Dictionary<string, string> texts = keys.ToDictionary(key => key,
                key => Resources_DE.GetString("Map." + key));
            html = html.Replace("__AF_MAP_TITLE__", System.Net.WebUtility.HtmlEncode(texts["Title"]), StringComparison.Ordinal)
                .Replace("__AF_MAP_HORIZON__", System.Net.WebUtility.HtmlEncode(texts["Horizon"]), StringComparison.Ordinal)
                .Replace("__AF_MAP_TEXTS_JSON__", System.Text.Json.JsonSerializer.Serialize(texts), StringComparison.Ordinal);

            return new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes(html));
        }

        private void Simconnect_AdviseItems(DataItem item)
        {
            try
            {
                if (item.DataType == SIMCONNECT_DATATYPE.STRING256)
                    simconnect!.AddToDataDefinition(
                        (SimConnectDefinitionId)(uint)item.DefinitionId,
                        item.Name,
                        item.Unit,
                        SIMCONNECT_DATATYPE.STRING256,
                        0.0f,
                        SimConnect.SIMCONNECT_UNUSED);
                else if (item.DataType == SIMCONNECT_DATATYPE.FLOAT64)
                    simconnect!.AddToDataDefinition(
                        (SimConnectDefinitionId)(uint)item.DefinitionId,
                        item.Name,
                        item.Unit,
                        SIMCONNECT_DATATYPE.FLOAT64,
                        0.0f,
                        SimConnect.SIMCONNECT_UNUSED);
                else if (item.DataType == SIMCONNECT_DATATYPE.INT32)
                    simconnect!.AddToDataDefinition(
                        (SimConnectDefinitionId)(uint)item.DefinitionId,
                        item.Name,
                        item.Unit,
                        SIMCONNECT_DATATYPE.INT32,
                        0.0f,
                        SimConnect.SIMCONNECT_UNUSED);
                else
                    simconnect!.AddToDataDefinition(
                        (SimConnectDefinitionId)(uint)item.DefinitionId,
                        item.Name,
                        item.Unit,
                        SIMCONNECT_DATATYPE.FLOAT64,
                        0.0f,
                        SimConnect.SIMCONNECT_UNUSED);

                //RegisterDataDefineStruct(Enum definitionId)
                if (item.DataType == SIMCONNECT_DATATYPE.STRING256)
                    simconnect.RegisterDataDefineStruct<Struct1>((SimConnectDefinitionId)(uint)item.DefinitionId);
                else if (item.DataType == SIMCONNECT_DATATYPE.FLOAT64)
                    simconnect.RegisterDataDefineStruct<double>((SimConnectDefinitionId)(uint)item.DefinitionId);
                else if (item.DataType == SIMCONNECT_DATATYPE.INT32)
                    simconnect.RegisterDataDefineStruct<int>((SimConnectDefinitionId)(uint)item.DefinitionId);
                else
                    // Fallback: benutze double als Default
                    simconnect.RegisterDataDefineStruct<double>((SimConnectDefinitionId)(uint)item.DefinitionId);

                simconnect!.RequestDataOnSimObject(
                        (SimConnectRequestId)(uint)item.RequestId,
                        (SimConnectDefinitionId)(uint)item.DefinitionId,
                        SimConnect.SIMCONNECT_OBJECT_ID_USER,
                        Microsoft.FlightSimulator.SimConnect.SIMCONNECT_PERIOD.SIM_FRAME,
                        SIMCONNECT_DATA_REQUEST_FLAG.CHANGED,
                        0u,
                        0u,
                        SimConnect.SIMCONNECT_UNUSED);
                //Debug.WriteLine($"[Debug] {DateTime.Now:yyyy-MM-dd HH:mm:ss} - Variable angemeldet: {item.RequestId} - {item.Name}");
            }
            catch (Exception ex)
            {
                ShowStatus("Fehler bei SimconnectAdviseItems: " + ex.Message);
            }
        }

        private void SimConnect_OnRecvOpen(SimConnect sender, SIMCONNECT_RECV_OPEN data)
        {
            try
            {
                ResetTerrainTelemetry();
            ShowStatus(Resources_DE.GetString("MainForm_Connected"));
                if (simconnect == null) return;
                //***************************************************************
                //****************** SimVar Variablen anmelden ******************
                //***************************************************************
                var fields = typeof(Flight_Model).GetFields(BindingFlags.Static | BindingFlags.Public);
                foreach (var fi in fields)
                {
                    if (fi.GetValue(null) is not DataItem item) continue;
                    Simconnect_AdviseItems(item);
                }
                fields = typeof(Flight_Data).GetFields(BindingFlags.Static | BindingFlags.Public);
                foreach (var fi in fields)
                {
                    if (fi.GetValue(null) is not DataItem item) continue;
                    Simconnect_AdviseItems(item);
                }
                Debug.WriteLine($"[Debug] {DateTime.Now:yyyy-MM-dd HH:mm:ss} - Alle SimVar-Variablen angemeldet");
                //***************************************************************
                //****************** Autopilot aktivieren ***********************
                //***************************************************************
                autopilot = new AutoPilot(simconnect)
                {
                    localTargetAltDeg = _targetAltitude,
                    LocalTargetHdgDeg = _targetHeading
                };
                if (_isAutoThrottleActive)
                    autopilot.AutoThrottleOnOff(true);
                Debug.WriteLine($"[Debug] {DateTime.Now:yyyy-MM-dd HH:mm:ss} - Autopilot initialisiert");
                autopilotTimer.Start();
            }
            catch (Exception ex)
            {
                ShowStatus("Fehler bei DataDefinition/Request: " + ex.Message);
            }
        }

        private static DataItem? FindDataItemByRequestId(uint requestId, Type flightModelType)
        {
            var fields = flightModelType.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            foreach (var fi in fields)
            {
                if (fi.GetValue(null) is DataItem item)
                {
                    if (item.RequestId == requestId || item.DefinitionId == requestId)
                        return item;
                }
            }
            return null;
        }

        // errechnet die Entfernung zwischen zwei GPS-Koordinaten in Metern (Haversine-Formel)
        private static double GetDistanceInMeters(double lat1, double lon1, double lat2, double lon2)
        {
            const double R = 6371000.0;
            static double toRad(double deg) => deg * Math.PI / 180.0;
            var dLat = toRad(lat2 - lat1);
            var dLon = toRad(lon2 - lon1);
            lat1 = toRad(lat1);
            lat2 = toRad(lat2);
            var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                  + Math.Sin(dLon / 2) * Math.Sin(dLon / 2) * Math.Cos(lat1) * Math.Cos(lat2);
            var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
            return R * c;
        }

        // Prüft, ob die Position um mindestens minMeters geändert wurde
        private bool ShouldSendPosition(double lat, double lon, double minMeters = 10.0)
        {
            if (!_lastSentLat.HasValue || !_lastSentLon.HasValue)
            {
                _lastSentLat = lat;
                _lastSentLon = lon;
                return true;
            }
            var d = GetDistanceInMeters(_lastSentLat.Value, _lastSentLon.Value, lat, lon);
            if (d >= minMeters)
            {
                _lastSentLat = lat;
                _lastSentLon = lon;
                return true;
            }
            return false;
        }

        private bool SendPlanePositionToMap()
        {
            // CoreWebView2 existiert bereits vor dem Laden von map.html.
            // Erst die bereite Karte darf den Positionsfilter fortschreiben.
            if (!waypointClientReady || webView2?.CoreWebView2 == null ||
                Flight_Data.PLANE_LATITUDE.Value is not double lat ||
                Flight_Data.PLANE_LONGITUDE.Value is not double lng ||
                Flight_Data.PLANE_HEADING_DEGREES_MAGNETIC.Value is not double head ||
                !double.IsFinite(lat) || lat < -90.0 || lat > 90.0 ||
                !double.IsFinite(lng) || lng < -180.0 || lng > 180.0 ||
                !double.IsFinite(head))
                return false;
            if (!ShouldSendPosition(lat, lng, 10.0))
                return false;

            var latS = Math.Round(lat, 5, MidpointRounding.AwayFromZero);
            var lngS = Math.Round(lng, 5, MidpointRounding.AwayFromZero);
            int headInt = (int)Math.Round(head);
            double h = ((headInt % 360) + 360) % 360;
            string js = string.Format(CultureInfo.InvariantCulture,
                "window.updatePlane({0}, {1}, {2}, {3});",
                latS, lngS, h, _isPlaneCenterActive ? "true" : "false");
            try
            {
                _ = webView2.CoreWebView2.ExecuteScriptAsync(js);
                return true;
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException)
            {
                _lastSentLat = null;
                _lastSentLon = null;
                Debug.WriteLine($"[Debug] {DateTime.Now:yyyy-MM-dd HH:mm:ss} - [Map] Positionsübergabe fehlgeschlagen: {ex.Message}");
                return false;
            }
        }

        private async void AutopilotTimer_Tick(object? sender, EventArgs e)
        {
            //***************************************************************
            //************* Übermitteln der Werte an Autopilot **************
            //***************************************************************
            try
            {
                if (IsAutostartActive)
                {
                    return;
                }
                if (simconnect == null || autopilot == null || relocationControlsSuspended)
                {
                    return;
                }
                ApplyWaypointNavigation();
                ApplyAutoLandAlignment();
                autopilot.ProcessCurrentValues(
                    Flight_Data.INDICATED_ALTITUDE,                 //FlyLevel:Höhe (feet)
                    Flight_Data.PLANE_HEADING_DEGREES_MAGNETIC,     //FlyLevel:Kurs (Grad)
                    Flight_Data.PLANE_BANK_DEGREES,                 //FlyLevel:Rollwinkel (Grad)
                    Flight_Data.AILERON_TRIM_PCT,                   //FlyLevel:Querrollwinkel (Grad)
                    Flight_Data.VERTICAL_SPEED,                     //AltitudeHold:Vertikale Geschwindigkeit (feet/min)
                    Flight_Data.AIRSPEED_INDICATED,                 //FlyLevel:Angezeigte Fluggeschwindigkeit (knots)
                    Flight_Data.ELEVATOR_TRIM_POSITION,             //AltitudeHold:Höhe Trim Position (radians)
                    Flight_Model.ELEVATOR_TRIM_UP_LIMIT,            //AltitudeHold:Höhe Trim UP Limit (radians)
                    Flight_Model.ELEVATOR_TRIM_DOWN_LIMIT,          //AltitudeHold:Höhe Trim DOWN Limit (radians)
                    Flight_Data.AUTOPILOT_MASTER,                   //Flugzeug Autopilot (bool)
                    Flight_Model.DESIGN_SPEED_CLIMB,                //AltitudeHold:VY (knots)
                    Flight_Model.TITLE,                             //Flugzeug Name (string)
                    Flight_Model.TOTAL_WEIGHT,                      //Flugzeug Gewicht (kg)
                    Flight_Data.AILERON_POSITION,                   //Rollwinkel (percent)
                    Flight_Model.DESIGN_SPEED_VC,                   //Design Speed VC (knots)
                    Flight_Model.NUMBER_OF_ENGINES,                 //Anzahl der Triebwerke (int)
                    engineThrottlePositions,                       //Triebwerksdrosselstellungen (percent)
                    Flight_Data.TURN_INDICATOR_RATE,                //Drehrate (degrees per second)
                    Flight_Model.DESIGN_SPEED_VS1,                  //Überziehgeschwindigkeit (knots)
                    Flight_Model.WING_AREA);                       //Flügelfläche (square feet)
                await QueueFileTerrainFollowUpdate();
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"[Debug] {DateTime.Now:HH:mm:ss.fff} - " + $"Fehler beim Aufruf des lokal Autopilot: {ex}");
            }
        }

        private async void Simconnect_OnRecvSimobjectData(SimConnect sender, SIMCONNECT_RECV_SIMOBJECT_DATA data)
        {
            try
            {
                if (ReceiveAutoLandFlightSample(sender, data))
                    return;
                if (ReceiveAutostartReadiness(data))
                    return;
                if (ReceiveRelocationSnapshot(data))
                    return;
                //***************************************************************
                //****************** Finde aktuelle Variable ********************
                //***************************************************************
                var Item = FindDataItemByRequestId(data.dwRequestID, typeof(Flight_Model));
                if (Item == null)
                {
                    Item = FindDataItemByRequestId(data.dwRequestID, typeof(Flight_Data));
                    if (Item == null)
                    {
                            ShowStatus(Resources_DE.Format("MainForm_UnknownRequest", data.dwRequestID));
                        return;
                    }
                }
                //***************************************************************
                //*************** Empfang der Variablen-Werte *******************
                //***************************************************************
                switch (Item.DataType)
                {
                    case SIMCONNECT_DATATYPE.STRING256:
                        {
                            Struct1 result = (Struct1)data.dwData[0];
                            Item.Value = result.sValue ?? string.Empty;
                            break;
                        }
                    case SIMCONNECT_DATATYPE.FLOAT64:
                        {
                            // dwData[0] kann verschiedene numerische Typen enthalten; Convert.ToDouble ist robust
                            Item.Value = Convert.ToDouble(data.dwData[0]);
                            break;
                        }
                    case SIMCONNECT_DATATYPE.INT32:
                        {
                            // Behandle signed/unsigned Varianten sicher
                            var raw = data.dwData[0];
                            if (raw is int i) Item.Value = i;
                            else if (raw is uint ui) Item.Value = unchecked((int)ui);
                            else Item.Value = Convert.ToInt32(raw);
                            break;
                        }
                    case SIMCONNECT_DATATYPE.FLOAT32:
                        {
                            // 32-bit float
                            Item.Value = Convert.ToSingle(data.dwData[0]);
                            break;
                        }
                    default:
                        {
                            // Fallback: versuche eine Double-Konvertierung, sonst das Rohobjekt
                            try
                            {
                                Item.Value = Convert.ToDouble(data.dwData[0]);
                            }
                            catch
                            {
                                Item.Value = data.dwData[0];
                            }
                            break;
                        }
                }
                ObserveAutostartDataItem(Item);
                ObserveTerrainTelemetry(Item);
                if (ReferenceEquals(Item, Flight_Data.GROUND_ALTITUDE))
                    ApplyTerrainFollow();
                //Debug.WriteLine($"[Debug] {DateTime.Now:yyyy-MM-dd HH:mm:ss} - Simconnect_OnRecvSimobjectData: Received({Item?.Name} - {data.dwRequestID} : {Item?.Value})");
                //***************************************************************
                //********** Übermitteln der Werte an Browser-Fenster ***********
                //***************************************************************
                try
                {
                    if (webView2?.CoreWebView2 == null) return;
                    string js = string.Empty;
                    double value;
                    int valueInt;

                    // Lat/Lon/Heading senden (Flugzeug mit grünem Pfeil, äußere Kompassrose) 
                    if (!SendPlanePositionToMap()) return;

                    // Ware Nord-Kurs senden (innere Kompassrose)
                    value = Convert.ToDouble(Flight_Data.PLANE_HEADING_DEGREES_TRUE.Value);
                    valueInt = (int)Math.Round(value);
                    js = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                         "window.setTrueHeading({0});", valueInt);
                    if (!string.IsNullOrEmpty(js) && webView2?.CoreWebView2 != null)
                        _ = webView2.CoreWebView2.ExecuteScriptAsync(js);
                    //Debug.WriteLine($"[Debug] {DateTime.Now:yyyy-MM-dd HH:mm:ss} - GPS-Head:{js}");

                    // Autopilot Kurse senden (rotes  Dreieck)
                    valueInt = _targetHeading;
                    js = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                         "window.setAutopilotCourse({0});", valueInt);
                    if (!string.IsNullOrEmpty(js) && webView2?.CoreWebView2 != null)
                        _ = webView2.CoreWebView2.ExecuteScriptAsync(js);
                    //Debug.WriteLine($"[Debug] {DateTime.Now:yyyy-MM-dd HH:mm:ss} - AP-Head:{js}");

                    value = Convert.ToDouble(Flight_Data.AIRSPEED_INDICATED.Value);
                    valueInt = (int)Math.Round(value);
                    js = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                         "window.setSpeedKts({0});", valueInt);
                    if (!string.IsNullOrEmpty(js) && webView2?.CoreWebView2 != null)
                        _ = webView2.CoreWebView2.ExecuteScriptAsync(js);
                    //Debug.WriteLine($"[Debug] {DateTime.Now:yyyy-MM-dd HH:mm:ss} - Speed:{js}");

                    // Höhe über Grund senden
                    value = Convert.ToDouble(Flight_Data.PLANE_ALT_ABOVE_GROUND.Value);
                    valueInt = (int)Math.Round(value);
                    js = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                         "window.setAltitudeAGL({0});", valueInt);
                    if (!string.IsNullOrEmpty(js) && webView2?.CoreWebView2 != null)
                        _ = webView2.CoreWebView2.ExecuteScriptAsync(js);
                    //Debug.WriteLine($"[Debug] {DateTime.Now:yyyy-MM-dd HH:mm:ss} - ALT:{js}");

                    // Höhe über Meeresspiegel senden
                    value = Convert.ToDouble(Flight_Data.INDICATED_ALTITUDE.Value);
                    valueInt = (int)Math.Round(value);
                    js = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                         "window.setAltitudeAMSL({0});", valueInt);
                    if (!string.IsNullOrEmpty(js) && webView2?.CoreWebView2 != null)
                        _ = webView2.CoreWebView2.ExecuteScriptAsync(js);
                    //Debug.WriteLine($"[Debug] {DateTime.Now:yyyy-MM-dd HH:mm:ss} - ALT0:{js}");

                    // Pitch Steigen/Senken setzen 
                    value = -Convert.ToDouble(Flight_Data.PLANE_PITCH_DEGREES.Value);
                    valueInt = (int)Math.Round(value);
                    js = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                         "window.setHorizonPitch({0});", valueInt);
                    if (!string.IsNullOrEmpty(js) && webView2?.CoreWebView2 != null)
                        _ = webView2.CoreWebView2.ExecuteScriptAsync(js);
                    //Debug.WriteLine($"[Debug] {DateTime.Now:yyyy-MM-dd HH:mm:ss} - Pitch:{js}");

                    // Bank rechts/links Kurve setzen 
                    value = -Convert.ToDouble(Flight_Data.PLANE_BANK_DEGREES.Value);
                    valueInt = (int)Math.Round(value);
                    js = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                         "window.setHorizonBank({0});", valueInt);
                    if (!string.IsNullOrEmpty(js) && webView2?.CoreWebView2 != null)
                        _ = webView2.CoreWebView2.ExecuteScriptAsync(js);
                    //Debug.WriteLine($"[Debug] {DateTime.Now:yyyy-MM-dd HH:mm:ss} - Bank:{js}");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Debug] {DateTime.Now:yyyy-MM-dd HH:mm:ss} - Fehler beim Senden von Map-Updates: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                ShowStatus(Resources_DE.Format("MainForm_DataReceiveError", ex.Message));
            }
        }

        private enum DummyGroup : uint
        {
            Group0 = 0
        }

        private void SimConnect_OnRecvException(SimConnect sender, SIMCONNECT_RECV_EXCEPTION data)
        {
            if (ReceiveAirportException(sender, data))
                return;
            if (ReceiveAutostartException(sender, data))
                return;
            try
            {
                ReceiveRelocationException(data);
                uint code = data.dwException;
                uint sendId = data.dwSendID;
                uint index = data.dwIndex;

                // Dynamischen Enum-Namen ermitteln (falls definiert)
                string codeName = Enum.GetName(typeof(SIMCONNECT_EXCEPTION), code) ?? Resources_DE.Format("MainForm_UnknownErrorCode", code);

                // Versuche zu ermitteln, welche Variable die Exception ausgelöst hat
                var item = FindDataItemByRequestId(index, typeof(Flight_Model));
                string itemInfo = item != null
                    ? Resources_DE.Format("MainForm_DataItemDetails", item.Name, item.RequestId, item.DefinitionId)
                    : Resources_DE.Format("MainForm_DataItemNotFound", index);

                string message = Resources_DE.Format("MainForm_SimConnectError", code, codeName, sendId, index, itemInfo);
                Debug.WriteLine($"[Debug] {DateTime.Now:yyyy-MM-dd HH:mm:ss} - [SimConnectException] " + message);
                ShowStatus(message);

                // Optional: erklärender Hinweis für häufig gesehene numerische Codes (z.B. 7)
                if (code == 7)
                {
                    Debug.WriteLine($"[Debug] {DateTime.Now:yyyy-MM-dd HH:mm:ss} - [Hinweis] Exception 7 empfangen. Mögliche Ursachen: falscher Unit-Name, falscher DATATYPE, doppelte/ungültige Definition- oder Request-IDs.");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Debug] {DateTime.Now:yyyy-MM-dd HH:mm:ss} - Fehler in SimConnect_OnRecvException-Handler: " + ex.Message);
                ShowStatus(Resources_DE.Format("MainForm_ExceptionHandlerError", ex.Message));
            }
        }

        protected override async void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            if (e.Cancel || terrainFollowCloseReady)
                return;

            e.Cancel = true;
            if (terrainFollowClosePending)
                return;

            terrainFollowClosePending = true;
            autopilotTimer.Stop();
            CancelAutostart(closing: true);
            ResetTerrainTelemetry();
            terrainFollowShutdownTask = terrainFollow.DisposeAsync().AsTask();
            try
            {
                await CancelAutoLandAsync();
                await terrainFollowShutdownTask;
                await terrainFollowUpdateTask;
            }
            catch (IOException)
            {
                // Reader-Freigabefehler ändern den zuletzt gültigen ALT-Sollwert nicht.
            }
            finally
            {
                terrainFollowCloseReady = true;
                if (!IsDisposed && !Disposing)
                    BeginInvoke(new Action(Close));
            }
        }

        private async void TerrainFollow_DisposedAsync(object? sender, EventArgs e)
        {
            ResetTerrainTelemetry();
            terrainFollowShutdownTask = terrainFollow.DisposeAsync().AsTask();
            try
            {
                await terrainFollowShutdownTask.ConfigureAwait(false);
            }
            catch (IOException)
            {
                // Auch direkte Dispose-Aufrufe geben den Reader bestmöglich asynchron frei.
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            SaveLanguagePreference();
            autopilotTimer.Stop();
            CancelAutostart(closing: true);
            ShowStatus(Resources_DE.GetString("MainForm_Closing"));
            base.OnFormClosed(e);
        }

        private void SimConnect_OnRecvQuit(SimConnect sender, SIMCONNECT_RECV data)
        {
            ShowStatus(Resources_DE.GetString("MainForm_Disconnecting"));
            // MSFS hat beendet — schließe das Panel
            Disconnect();
        }

        private void Disconnect()
        {
            autopilotTimer.Stop();
            StopAutoLand();
            ResetTerrainTelemetry();
            terrainFollow.SetEnabled(false);
            CancelAutostart(connectionLost: true);
            ShowStatus(Resources_DE.GetString("MainForm_Disconnecting"));
            if (simconnect != null)
            {
                simconnect?.Dispose();
                simconnect = null;
            }
            if (autopilot != null)
            {
                autopilot?.AutopilotLocalOnOff(false);
                autopilot = null;
            }
            Debug.WriteLine($"[Debug] {DateTime.Now:yyyy-MM-dd HH:mm:ss} - Application.Exit...");
            Application.Exit();
        }

        public void ShowStatus(string message)
        {
            try
            {
                // Nur auf dem UI-Thread loggen, damit die Meldung nicht doppelt erscheint
                Debug.WriteLine($"[Debug] {DateTime.Now:yyyy-MM-dd HH:mm:ss} - {message}");
                statusLabel.Text = message;
            }
            catch
            {
                // Fehler beim Anzeigen ignorieren
            }
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_USER_SIMCONNECT = 0x0402; // bereits oben definiert
            try
            {
                if (m.Msg == WM_USER_SIMCONNECT && simconnect != null)
                {
                    // SimConnect-Nachrichten verarbeiten
                    simconnect.ReceiveMessage();
                }
            }
            catch
            {
                // Logging optional
            }

            base.WndProc(ref m);
        }

        private void TsbPlaneCenter_Click(object sender, EventArgs e)
        {
            _isPlaneCenterActive = !_isPlaneCenterActive;
            if (_isPlaneCenterActive)
            {
                TsbPlaneCenter.BackColor = Color.LightGreen;
                _lastSentLat = null;
                _lastSentLon = null;
                SendPlanePositionToMap();
                //Debug.WriteLine($"[Debug] {DateTime.Now:yyyy-MM-dd HH:mm:ss} - Plane Center AKTIVIERT.");
            }
            else
            {
                TsbPlaneCenter.BackColor = Color.LightGray; // Zurück auf Standard-Grau
                //Debug.WriteLine($"[Debug] {DateTime.Now:yyyy-MM-dd HH:mm:ss} - Plane Center DEAKTIVIERT.");
            }
        }

        private void TsbAP_Click(object sender, EventArgs e)
        {
            if (IsAutostartActive) return;
            if (simconnect == null) return;
            if (autopilot == null) return;
            _isLocalAutopilotActive = !_isLocalAutopilotActive;
            if (_isLocalAutopilotActive)
            {
                tsbAP.BackColor = Color.LightGreen;
                autopilot.DisablePlaneAutopilot();
                autopilot.AutopilotLocalOnOff(true);
                //Debug.WriteLine($"[Debug] {DateTime.Now:yyyy-MM-dd HH:mm:ss} - Autopilot (AP) aktiviert.");
            }
            else
            {
                tsbAP.BackColor = Color.LightGray;
                autopilot.AutopilotLocalOnOff(false);
                //Debug.WriteLine($"[Debug] {DateTime.Now:yyyy-MM-dd HH:mm:ss} - Autopilot (AP) deaktiviert.");
            }
        }

        private void TsbHDG_Click(object sender, EventArgs e)
        {
            if (IsAutostartActive) return;
            if (autopilot == null) return;
            _isHDGActive = !_isHDGActive;
            if (_isHDGActive)
            {
                autopilot.HdgOnOff(true);
                tsbHDG.BackColor = Color.LightGreen;
                //Debug.WriteLine($"[Debug] {DateTime.Now:yyyy-MM-dd HH:mm:ss} - HDG AKTIVIERT.");
            }
            else
            {
                autopilot.HdgOnOff(false);
                tsbHDG.BackColor = Color.LightGray; // Zurück auf Standard-Grau
                //Debug.WriteLine($"[Debug] {DateTime.Now:yyyy-MM-dd HH:mm:ss} - HDG DEAKTIVIERT.");
            }

        }

        private void TsbALT_Click(object sender, EventArgs e)
        {
            if (IsAutostartActive) return;
            if (autopilot == null) return;
            _isALTActive = !_isALTActive;
            if (_isALTActive)
            {
                autopilot.AltOnOff(true);
                tsbALT.BackColor = Color.LightGreen;
                //Debug.WriteLine($"[Debug] {DateTime.Now:yyyy-MM-dd HH:mm:ss} - ALT AKTIVIERT.");
            }
            else
            {
                autopilot.AltOnOff(false);
                tsbALT.BackColor = Color.LightGray; // Zurück auf Standard-Grau
                //Debug.WriteLine($"[Debug] {DateTime.Now:yyyy-MM-dd HH:mm:ss} - ALT DEAKTIVIERT.");
            }
        }

        private void TstbHeading_KeyDown(object sender, KeyEventArgs e)
        {
            if (IsAutostartActive) return;
            if (e.KeyCode == Keys.Enter)
            {
                toolStripBottom.Select();
                if (double.TryParse(tstbHeading.Text, NumberStyles.Float | NumberStyles.AllowThousands,
                    CultureInfo.InvariantCulture, out double newHdg) && double.IsFinite(newHdg))
                {
                    //Debug.WriteLine($"[Debug] {DateTime.Now:yyyy-MM-dd HH:mm:ss} - HDG neuer Wert: {newHdg}");
                    _targetHeading = RoundTargetHeading(newHdg);
                    autopilot?.LocalTargetHdgDeg = _targetHeading;
                    tstbHeading.Text = _targetHeading.ToString(CultureInfo.InvariantCulture);
                }
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        }

        private async void TstbAltitude_KeyDown(object sender, KeyEventArgs e)
        {
            if (IsAutostartActive) return;
            if (e.KeyCode == Keys.Enter)
            {
                toolStripBottom.Select();
                if (double.TryParse(tstbAltitude.Text, NumberStyles.Float | NumberStyles.AllowThousands,
                    CultureInfo.InvariantCulture, out double newALT) && double.IsFinite(newALT) &&
                    newALT >= int.MinValue && newALT <= int.MaxValue)
                {
                    //Debug.WriteLine($"[Debug] {DateTime.Now:yyyy-MM-dd HH:mm:ss} - ALT neuer Wert {newALT}");
                    _targetAltitude = RoundTargetAltitude(newALT);
                    autopilot?.localTargetAltDeg = _targetAltitude;
                    tstbAltitude.Text = _targetAltitude.ToString(CultureInfo.InvariantCulture);
                }
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        }

        private void GetCurrHDG_Click(object sender, EventArgs e)
        {
            if (IsAutostartActive) return;
            double currentTarget = Convert.ToDouble(Flight_Data.AUTOPILOT_HEADING_LOCK_DIR.Value);
            if (!double.IsFinite(currentTarget)) return;
            _targetHeading = RoundTargetHeading(currentTarget);
            autopilot?.LocalTargetHdgDeg = _targetHeading;
            tstbHeading.Text = _targetHeading.ToString(CultureInfo.InvariantCulture);
        }

        private void GetCurrALT_Click(object sender, EventArgs e)
        {
            if (IsAutostartActive) return;
            double currentTarget = Convert.ToDouble(Flight_Data.INDICATED_ALTITUDE.Value);
            if (!double.IsFinite(currentTarget) || currentTarget < int.MinValue || currentTarget > int.MaxValue) return;
            _targetAltitude = RoundTargetAltitude(currentTarget);
            autopilot?.localTargetAltDeg = _targetAltitude;
            tstbAltitude.Text = _targetAltitude.ToString(CultureInfo.InvariantCulture);
        }

        private void TsbAutoThrottle_Click(object? sender, EventArgs e)
        {
            if (IsAutostartActive) return;
            if (autopilot == null) return;
            bool enabled = !_isAutoThrottleActive;
            autopilot.AutoThrottleOnOff(enabled);
            _isAutoThrottleActive = enabled;
            tsbAutoThrottle.BackColor = enabled ? Color.LightGreen : Color.LightGray;
        }

        private void TsbATT_Click(object sender, EventArgs e)
        {
            if (IsAutostartActive) return;
            if (autopilot == null) return;
            _isATTActive = !_isATTActive;
            if (_isATTActive)
            {
                autopilot.AttOnOff(true);
                TsbATT.BackColor = Color.LightGreen;
                //Debug.WriteLine($"[Debug] {DateTime.Now:yyyy-MM-dd HH:mm:ss} - ATT AKTIVIERT.");
            }
            else
            {
                autopilot.AttOnOff(false);
                TsbATT.BackColor = Color.LightGray; // Zurück auf Standard-Grau
                //Debug.WriteLine($"[Debug] {DateTime.Now:yyyy-MM-dd HH:mm:ss} - ATT DEAKTIVIERT.");
            }
        }

        private void TsbLVL_Click(object sender, EventArgs e)
        {
            if (IsAutostartActive) return;
            if (autopilot == null) return;
            _isLVLActive = !_isLVLActive;
            if (_isLVLActive)
            {
                autopilot.LvlOnOff(true);
                TsbLVL.BackColor = Color.LightGreen;
                //Debug.WriteLine($"[Debug] {DateTime.Now:yyyy-MM-dd HH:mm:ss} - LVL AKTIVIERT.");
            }
            else
            {
                autopilot.LvlOnOff(false);
                TsbLVL.BackColor = Color.LightGray; // Zurück auf Standard-Grau
                //Debug.WriteLine($"[Debug] {DateTime.Now:yyyy-MM-dd HH:mm:ss} - LVL DEAKTIVIERT.");
            }
        }

        private void TsbInfo_Click(object? sender, EventArgs e)
        {
            string title = Resources_DE.GetString("InfoDialog_Title");
            string version = typeof(AutoflyMain).Assembly.GetName().Version?.ToString(3) ?? string.Empty;
            RichTextBox content = new()
            {
                Dock = DockStyle.Fill,
                Enabled = false,
                ReadOnly = true,
                DetectUrls = true,
                WordWrap = true,
                ScrollBars = RichTextBoxScrollBars.Vertical,
                BorderStyle = BorderStyle.None,
                BackColor = SystemColors.Window,
                ForeColor = SystemColors.WindowText,
                Text = Resources_DE.GetString("InfoDialog_Text").Replace("{{VERSION}}", version),
                AccessibleName = title,
                AccessibleDescription = Resources_DE.GetString("InfoDialog_ContentDescription"),
                TabIndex = 0
            };
            using Font contentFont = new(content.Font.FontFamily, content.Font.SizeInPoints + 1F,
                content.Font.Style, GraphicsUnit.Point);
            using Form dialog = new()
            {
                Text = title,
                AccessibleName = title,
                StartPosition = FormStartPosition.CenterParent,
                AutoScaleMode = AutoScaleMode.Dpi,
                AutoScaleDimensions = new SizeF(96F, 96F),
                Padding = new Padding(12),
                MinimizeBox = false,
                MaximizeBox = false,
                ShowInTaskbar = false
            };
            content.Font = contentFont;
            Button closeButton = new()
            {
                Text = Resources_DE.GetString("InfoDialog_Close"),
                AccessibleName = Resources_DE.GetString("InfoDialog_Close").Replace("&", string.Empty),
                AccessibleDescription = Resources_DE.GetString("InfoDialog_CloseDescription"),
                AutoSize = true,
                DialogResult = DialogResult.Cancel,
                TabIndex = 0
            };
            FlowLayoutPanel buttons = new()
            {
                Dock = DockStyle.Bottom,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(6),
                TabIndex = 1
            };
            buttons.Controls.Add(closeButton);
            dialog.Controls.Add(content);
            dialog.Controls.Add(buttons);
            dialog.CancelButton = closeButton;
            dialog.AcceptButton = closeButton;

            dialog.PerformAutoScale();
            dialog.PerformLayout();
            Rectangle workingArea = Screen.FromControl(this).WorkingArea;
            Size nonClient = dialog.Size - dialog.ClientSize;
            int maxClientWidth = Math.Max(1, workingArea.Width - nonClient.Width);
            int maxClientHeight = Math.Max(1, workingArea.Height - nonClient.Height);
            int textInset = (int)Math.Ceiling(8 * DeviceDpi / 96F);
            int textOverhead = SystemInformation.VerticalScrollBarWidth + textInset;
            int maxTextWidth = Math.Max(1, maxClientWidth - dialog.Padding.Horizontal - textOverhead);
            int readingWidth = TextRenderer.MeasureText(new string('0', 90), contentFont).Width;
            TextFormatFlags textFlags = TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl |
                TextFormatFlags.NoPrefix;
            // Eine zusätzliche Zeile lässt auch abschließende Leerzeilen und RichEdit-Abstände zu.
            string measuredText = content.Text + "\n ";
            Size textSize = TextRenderer.MeasureText(measuredText, contentFont,
                new Size(Math.Min(readingWidth, maxTextWidth), int.MaxValue), textFlags);
            int clientWidth = Math.Min(maxClientWidth, dialog.Padding.Horizontal +
                Math.Max(textSize.Width + textOverhead, buttons.GetPreferredSize(Size.Empty).Width));
            int textWidth = Math.Max(1, clientWidth - dialog.Padding.Horizontal - textOverhead);
            textSize = TextRenderer.MeasureText(measuredText, contentFont,
                new Size(textWidth, int.MaxValue), textFlags);
            int buttonHeight = buttons.GetPreferredSize(new Size(clientWidth - dialog.Padding.Horizontal,
                int.MaxValue)).Height;
            dialog.MaximumSize = workingArea.Size;
            dialog.ClientSize = new Size(clientWidth, Math.Min(maxClientHeight,
                textSize.Height + textInset + dialog.Padding.Vertical + buttonHeight));
            dialog.StartPosition = FormStartPosition.Manual;
            dialog.Location = new Point(
                Math.Clamp(Left + (Width - dialog.Width) / 2, workingArea.Left, workingArea.Right - dialog.Width),
                Math.Clamp(Top + (Height - dialog.Height) / 2, workingArea.Top, workingArea.Bottom - dialog.Height));

            void ShowInfoError(string message)
            {
                if (!dialog.IsDisposed)
                    MessageBox.Show(dialog, Resources_DE.Format("InfoDialog_LinkError", message), title,
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
            }

            content.LinkClicked += (_, args) =>
            {
                const string pomaxUrl = "https://pomax.github.io/are-we-flying";
                if (args.LinkText != pomaxUrl)
                    return;

                try
                {
                    using Process? browserProcess = Process.Start(new ProcessStartInfo(pomaxUrl) { UseShellExecute = true });
                }
                catch (System.ComponentModel.Win32Exception ex)
                {
                    ShowInfoError(ex.Message);
                }
                catch (InvalidOperationException ex)
                {
                    ShowInfoError(ex.Message);
                }
            };
            dialog.ShowDialog(this);
        }

    }

    internal static class Resources_DE
    {
        // Bilder bleiben sprachunabhängig in der vorhandenen deutschen Ressourcendatei.
        internal static ResourceManager ResourceManager { get; } = new("AutoFly.Resources_DE", typeof(Resources_DE).Assembly);
        private static ResourceManager EnglishResourceManager { get; } = new("AutoFly.Resources_EN", typeof(Resources_DE).Assembly);

        internal static string Language { get; private set; } = "de";

        internal static void SetLanguage(string language)
        {
            if (language is not ("de" or "en"))
                throw new ArgumentException(null, nameof(language));

            Language = language;
            CultureInfo culture = CultureInfo.GetCultureInfo(language == "en" ? "en-US" : "de-DE");
            CultureInfo.CurrentUICulture = culture;
            CultureInfo.DefaultThreadCurrentUICulture = culture;
        }

        internal static string GetString(string key) =>
            (Language == "en" ? EnglishResourceManager : ResourceManager).GetString(key, CultureInfo.InvariantCulture)
            ?? throw new MissingManifestResourceException(key);

        internal static string Format(string key, params object?[] arguments) =>
            string.Format(CultureInfo.CurrentCulture, GetString(key), arguments);
    }
}
