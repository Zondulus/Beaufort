using KSP.UI.Screens;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using WindAPI;

namespace Beaufort
{
    // =========================================================================
    // Configuration Data Class
    // =========================================================================
    public class BeaufortConfig
    {
        public float lightWind = 5.0f;
        public float medWind = 15.0f;
        public float highWind = 25.0f;

        public float lightTurbulence = 1.5f;
        public float heavyTurbulence = 4.0f;

        public float lightLift = 0.5f;
        public float strongLift = 2.0f;

        public float barbHalfFeather = 2.5f;
        public float barbFullFeather = 5.0f;
        public float barbFlag = 25.0f;

        public Rect windowRect = new Rect(200, 200, 180, 220);
    }

    // =========================================================================
    // Control Mode Wind Provider
    // =========================================================================
    public class BeaufortProvider : IWindProvider
    {
        public string ProviderID => "BeaufortControl";

        public bool IsActive = false;
        public float Heading = 0f; // Where wind is coming FROM
        public float HorizSpeed = 0f;
        public float VertSpeed = 0f;

        public Vector3 GetWind(CelestialBody body, Part part, Vector3 position)
        {
            if (!IsActive) return Vector3.zero;

            Vector3 up = (position - body.transform.position).normalized;
            Vector3 north = Vector3.ProjectOnPlane(body.transform.up, up).normalized;
            if (north.sqrMagnitude < 0.001f)
                north = Vector3.ProjectOnPlane(Vector3.forward, up).normalized;
            Vector3 east = Vector3.Cross(up, north).normalized;

            // Heading is where wind comes FROM
            float headingRad = Heading * Mathf.Deg2Rad;
            Vector3 windMovementDir = (-north * Mathf.Cos(headingRad)) + (-east * Mathf.Sin(headingRad));

            return (windMovementDir.normalized * HorizSpeed) + (up * VertSpeed);
        }
    }

    // =========================================================================
    // Main Controller & Telemetry
    // =========================================================================
    [KSPAddon(KSPAddon.Startup.Flight, once: false)]
    public class BeaufortController : MonoBehaviour
    {
        // --- Configuration & UI State ---
        private BeaufortConfig config = new BeaufortConfig();
        private ApplicationLauncherButton appButton = null;
        private bool showUI = false;
        private bool uiHidden = false; // Tracks F2 state
        private int activeTab = 0; // 0=Default, 1=Exact, 2=Barb, 3=Control

        // --- Telemetry State ---
        private Coroutine telemetryRoutine;
        private Queue<Vector3> windHistory = new Queue<Vector3>();
        private const int MAX_HISTORY = 50; // 50 samples at 0.1s = 5 seconds window

        // Raw 10Hz Variables
        private float currentHorizSpeed = 0f;
        private float currentVertSpeed = 0f;
        private float currentHeading = 0f;
        private float turbulenceMagnitude = 0f;
        private string headingText = "N";

        // Cached 2Hz (500ms) Variables for Exact Tab
        private string exactHeadingStr = "-";
        private string exactHorizStr = "-";
        private string exactVertStr = "-";

        // --- Control Mode State ---
        private BeaufortProvider controlProvider;
        private string inputHeading = "0";
        private string inputHoriz = "10";
        private string inputVert = "0";

        // --- Textures ---
        private Texture2D texStaff;
        private Texture2D texFlag;
        private Texture2D texFull;
        private Texture2D texHalf;
        private Texture2D texDot;

        // --- Lifecycle ---
        private void Awake()
        {
            LoadConfig();

            GameEvents.onGUIApplicationLauncherReady.Add(OnGUIAppLauncherReady);
            GameEvents.onGUIApplicationLauncherDestroyed.Add(OnGUIAppLauncherDestroyed);
            GameEvents.onHideUI.Add(() => uiHidden = true);
            GameEvents.onShowUI.Add(() => uiHidden = false);

            controlProvider = new BeaufortProvider();
        }

        private void Start()
        {
            telemetryRoutine = StartCoroutine(TelemetryLoop());
            StartCoroutine(RegisterProviderRoutine());
            // Assuming PNGs are in GameData/Beaufort/UI/
            texStaff = GameDatabase.Instance.GetTexture("Beaufort/UI/staff", false);
            texFlag = GameDatabase.Instance.GetTexture("Beaufort/UI/flag", false);
            texFull = GameDatabase.Instance.GetTexture("Beaufort/UI/feather_full", false);
            texHalf = GameDatabase.Instance.GetTexture("Beaufort/UI/feather_half", false);
            //texDot = GameDatabase.Instance.GetTexture("Beaufort/UI/dot", false); // Optional dot texture
        }

        private void OnDestroy()
        {
            GameEvents.onGUIApplicationLauncherReady.Remove(OnGUIAppLauncherReady);
            GameEvents.onGUIApplicationLauncherDestroyed.Remove(OnGUIAppLauncherDestroyed);

            if (appButton != null)
                ApplicationLauncher.Instance.RemoveModApplication(appButton);

            if (telemetryRoutine != null)
                StopCoroutine(telemetryRoutine);

            if (WindManager.Instance != null && controlProvider != null)
                WindManager.Instance.DeregisterProvider(controlProvider);

            if (compassRingTexture != null)
                Destroy(compassRingTexture);
        }

        private void LoadConfig()
        {
            if (GameDatabase.Instance == null) return;

            ConfigNode[] nodes = GameDatabase.Instance.GetConfigNodes("BEAUFORT_SETTINGS");
            if (nodes != null && nodes.Length > 0)
            {
                ConfigNode node = nodes[0];
                if (node.HasValue("lightWind")) float.TryParse(node.GetValue("lightWind"), out config.lightWind);
                if (node.HasValue("medWind")) float.TryParse(node.GetValue("medWind"), out config.medWind);
                if (node.HasValue("highWind")) float.TryParse(node.GetValue("highWind"), out config.highWind);

                if (node.HasValue("lightTurbulence")) float.TryParse(node.GetValue("lightTurbulence"), out config.lightTurbulence);
                if (node.HasValue("heavyTurbulence")) float.TryParse(node.GetValue("heavyTurbulence"), out config.heavyTurbulence);

                if (node.HasValue("lightLift")) float.TryParse(node.GetValue("lightLift"), out config.lightLift);
                if (node.HasValue("strongLift")) float.TryParse(node.GetValue("strongLift"), out config.strongLift);

                if (node.HasValue("barbHalfFeather")) float.TryParse(node.GetValue("barbHalfFeather"), out config.barbHalfFeather);
                if (node.HasValue("barbFullFeather")) float.TryParse(node.GetValue("barbFullFeather"), out config.barbFullFeather);
                if (node.HasValue("barbFlag")) float.TryParse(node.GetValue("barbFlag"), out config.barbFlag);
            }
        }

        private IEnumerator RegisterProviderRoutine()
        {
            while (WindManager.Instance == null)
                yield return new WaitForSeconds(0.5f);

            WindManager.Instance.RegisterProvider(controlProvider);
        }

        // --- Toolbar Integration ---
        private void OnGUIAppLauncherReady()
        {
            if (appButton == null)
            {
                Texture2D icon = GameDatabase.Instance.GetTexture("Beaufort/Icons/icon", false) ?? new Texture2D(38, 38);
                appButton = ApplicationLauncher.Instance.AddModApplication(
                    () => showUI = true,
                    () => showUI = false,
                    null, null, null, null,
                    ApplicationLauncher.AppScenes.FLIGHT,
                    icon
                );
            }
        }

        private void OnGUIAppLauncherDestroyed()
        {
            if (appButton != null)
            {
                ApplicationLauncher.Instance.RemoveModApplication(appButton);
                appButton = null;
            }
        }

        // --- Core Telemetry Loop (10Hz) ---
        private IEnumerator TelemetryLoop()
        {
            WaitForSeconds wait = new WaitForSeconds(0.1f);
            int tickCounter = 0;

            while (true)
            {
                yield return wait;

                if (!FlightGlobals.ready || FlightGlobals.ActiveVessel == null || WindManager.Instance == null)
                {
                    ResetTelemetry();
                    continue;
                }

                Vessel v = FlightGlobals.ActiveVessel;
                CelestialBody body = v.mainBody;
                Vector3 craftPos = v.GetWorldPos3D();

                Vector3 rawWind = WindManager.Instance.GetWindAtLocation(body, v.rootPart, craftPos);

                Vector3 up = (craftPos - body.transform.position).normalized;
                Vector3 north = Vector3.ProjectOnPlane(body.transform.up, up).normalized;
                if (north.sqrMagnitude < 0.001f)
                    north = Vector3.ProjectOnPlane(Vector3.forward, up).normalized;
                Vector3 east = Vector3.Cross(up, north).normalized;

                currentVertSpeed = Vector3.Dot(rawWind, up);
                Vector3 horizWind = rawWind - (up * currentVertSpeed);
                currentHorizSpeed = horizWind.magnitude;

                // INVERTED to show where wind is coming FROM
                float dotN = Vector3.Dot(horizWind, north);
                float dotE = Vector3.Dot(horizWind, east);

                // Negate both because we want heading to represent where wind is coming from
                float headingRad = Mathf.Atan2(-dotE, -dotN);
                currentHeading = (headingRad * Mathf.Rad2Deg + 360f) % 360f;
                headingText = GetHeadingString(currentHeading);

                // Turbulence Window Update
                windHistory.Enqueue(horizWind);
                if (windHistory.Count > MAX_HISTORY)
                    windHistory.Dequeue();

                CalculateTurbulence();

                // Cache Exact Strings every 5 ticks (0.5 seconds)
                if (tickCounter % 5 == 0)
                {
                    exactHeadingStr = $"{currentHeading:F0}°";
                    exactHorizStr = $"{currentHorizSpeed:F1}";
                    exactVertStr = $"{currentVertSpeed:F1}";
                }
                tickCounter++;
            }
        }

        private void CalculateTurbulence()
        {
            if (windHistory.Count == 0) return;

            Vector3 sum = Vector3.zero;
            foreach (Vector3 w in windHistory) sum += w;
            Vector3 averageWind = sum / windHistory.Count;

            float maxDev = 0f;
            foreach (Vector3 w in windHistory)
            {
                float dev = (w - averageWind).magnitude;
                if (dev > maxDev) maxDev = dev;
            }
            turbulenceMagnitude = maxDev;
        }

        private void ResetTelemetry()
        {
            currentHorizSpeed = 0f; currentVertSpeed = 0f;
            currentHeading = 0f; turbulenceMagnitude = 0f;
            headingText = "-"; windHistory.Clear();
            exactHeadingStr = "-"; exactHorizStr = "-"; exactVertStr = "-";
        }

        private string GetHeadingString(float heading)
        {
            string[] dirs = { "N", "NNE", "NE", "ENE", "E", "ESE", "SE", "SSE", "S", "SSW", "SW", "WSW", "W", "WNW", "NW", "NNW", "N" };
            return dirs[Mathf.RoundToInt((heading % 360f) / 22.5f)];
        }

        // =========================================================================
        // GUI Rendering
        // =========================================================================
        public void OnGUI()
        {
            if (showUI && !uiHidden)
            {
                GUI.skin = HighLogic.Skin;
                config.windowRect = GUILayout.Window(854125, config.windowRect, DrawWindow, "Beaufort Weather", GUILayout.Width(180), GUILayout.ExpandHeight(false));

                config.windowRect.x = Mathf.Clamp(config.windowRect.x, 0, Screen.width - config.windowRect.width);
                config.windowRect.y = Mathf.Clamp(config.windowRect.y, 0, Screen.height - config.windowRect.height);
            }
        }

        // Total body height (pixels) reserved below the tab buttons.
        // BRB drives this: GUILayout.Space(180) + ~8px padding = 188. Round up to 190.
        private const float BODY_HEIGHT = 190f;

        private void DrawWindow(int windowID)
        {
            // --- Tab Navigation ---
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("DEF")) activeTab = 0;
            if (GUILayout.Button("EXC")) activeTab = 1;
            if (GUILayout.Button("BRB")) activeTab = 2;
            if (GUILayout.Button("CTL")) activeTab = 3;
            GUILayout.EndHorizontal();

            // Fixed-height content area keeps window size identical across all tabs.
            GUILayout.BeginVertical(GUILayout.Height(BODY_HEIGHT));

            // --- Tab Rendering ---
            switch (activeTab)
            {
                case 0: DrawDefaultTab(); break;
                case 1: DrawExactTab(); break;
                case 2: DrawBarbTab(); break;
                case 3: DrawControlTab(); break;
            }

            GUILayout.EndVertical();

            GUI.DragWindow();
        }

        private void DrawDefaultTab()
        {
            GUIStyle style = new GUIStyle(GUI.skin.label) { fontSize = 20 };
            string spdStr = "LOW";
            if (currentHorizSpeed >= config.highWind) spdStr = "<color=red>HIGH</color>";
            else if (currentHorizSpeed >= config.medWind) spdStr = "<color=orange>MED</color>";

            string vertStr = "LEVEL";
            if (currentVertSpeed >= config.strongLift) vertStr = "<color=#00FF00>STRONG ↑</color>";
            else if (currentVertSpeed >= config.lightLift) vertStr = "<color=#AFFF80>LIGHT ↑</color>";
            else if (currentVertSpeed <= -config.strongLift) vertStr = "<color=red>STRONG ↓</color>";
            else if (currentVertSpeed <= -config.lightLift) vertStr = "<color=orange>LIGHT ↓</color>";

            string turbStr = "CLEAR";
            if (turbulenceMagnitude >= config.heavyTurbulence) turbStr = "<color=red>HEAVY</color>";
            else if (turbulenceMagnitude >= config.lightTurbulence) turbStr = "<color=orange>LIGHT</color>";

            GUILayout.Space(15);
            GUILayout.Label($"<b>Direction:</b> {headingText}", style);
            GUILayout.Space(15);
            GUILayout.Label($"<b>Wind:</b> {spdStr}", style);
            GUILayout.Space(15);
            GUILayout.Label($"<b>Vert:</b> {vertStr}", style);
            GUILayout.Space(15);
            GUILayout.Label($"<b>Turb:</b> {turbStr}", style);
            GUILayout.FlexibleSpace();
        }

        private void DrawExactTab()
        {
            GUIStyle style = new GUIStyle(GUI.skin.label) { fontSize = 20 };
            GUILayout.Space(15);
            GUILayout.Label($"<b>Head:</b> {exactHeadingStr}", style);
            GUILayout.Space(15);
            GUILayout.Label($"<b>Horiz:</b> {exactHorizStr} m/s", style);
            GUILayout.Space(15);
            GUILayout.Label($"<b>Vert:</b> {exactVertStr} m/s", style);
            GUILayout.Space(15);
            GUILayout.Label($"<b>Turb:</b> {turbulenceMagnitude:F2} m/s", style);
            GUILayout.FlexibleSpace();
        }

        // Wind barb visualization inspired by aviation charts
        // This method is more complex because we need to handle rotation
        // and layering of textures to create the wind barb visualization
        private void DrawBarbTab()
        {
            // Reserve layout space for the entire barb panel (compass + barb).
            GUILayout.Space(BODY_HEIGHT);

            if (Event.current.type == EventType.Repaint)
            {
                // Compass circle parameters — centred in the window body.
                // The window body starts just below the tab row (~30px title + ~28px tab buttons).
                float bodyTop = 58f; // approximate pixels from window top to body content area
                Vector2 pivot = new Vector2(config.windowRect.width / 2f, bodyTop + BODY_HEIGHT / 2f);
                float compassRadius = 72f;

                // --- 1. DRAW COMPASS CIRCLE (GL-based thin ring) ---
                DrawCompassCircle(pivot, compassRadius);

                // --- 2. DRAW CARDINAL LABELS (always upright, outside the rotation matrix) ---
                // Labels for 8 primary points; N is "up" which means wind blows toward the north.
                string[] cardinals = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };
                float[] angles = { 0f, 45f, 90f, 135f, 180f, 225f, 270f, 315f };
                float labelRadius = compassRadius + 10f; // just outside the ring
                float labelW = 22f;
                float labelH = 16f;
                GUIStyle labelStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 9,
                    alignment = TextAnchor.MiddleCenter,
                    normal = { textColor = new Color(0.75f, 0.75f, 0.75f) }
                };

                for (int i = 0; i < cardinals.Length; i++)
                {
                    float rad = angles[i] * Mathf.Deg2Rad;
                    // Screen Y is flipped: angle 0 = up, so sin goes to X and -cos to Y
                    float cx = pivot.x + Mathf.Sin(rad) * labelRadius;
                    float cy = pivot.y - Mathf.Cos(rad) * labelRadius;
                    Rect lRect = new Rect(cx - labelW / 2f, cy - labelH / 2f, labelW, labelH);
                    GUI.Label(lRect, cardinals[i], labelStyle);
                }

                // --- 3. DRAW WIND BARB (rotated) ---
                if (currentHorizSpeed < 0.5f)
                {
                    // Calm wind: small dot at centre
                    GUI.DrawTexture(new Rect(pivot.x - 4, pivot.y - 4, 8, 8), texDot ?? Texture2D.whiteTexture);
                    return;
                }

                Matrix4x4 backupMatrix = GUI.matrix;
                GUIUtility.RotateAroundPivot(currentHeading, pivot);

                float width = 32f;
                float height = 64f;

                // Staff tip anchored at the compass centre; staff extends "upward" (toward N at 0°)
                Rect baseRect = new Rect(pivot.x - (width / 2f), pivot.y - height, width, height);

                if (texStaff != null) GUI.DrawTexture(baseRect, texStaff);

                float speed = currentHorizSpeed;
                int flags = Mathf.FloorToInt(speed / config.barbFlag); speed %= config.barbFlag;
                int fullFeathers = Mathf.FloorToInt(speed / config.barbFullFeather); speed %= config.barbFullFeather;
                int halfFeathers = Mathf.FloorToInt(speed / config.barbHalfFeather);

                float currentYOffset = 0f;
                float spacing = 8f;

                for (int i = 0; i < flags; i++)
                {
                    Rect drawRect = new Rect(baseRect.x, baseRect.y + currentYOffset, width, height);
                    if (texFlag != null) GUI.DrawTexture(drawRect, texFlag);
                    currentYOffset += 10f;
                }
                for (int i = 0; i < fullFeathers; i++)
                {
                    Rect drawRect = new Rect(baseRect.x, baseRect.y + currentYOffset, width, height);
                    if (texFull != null) GUI.DrawTexture(drawRect, texFull);
                    currentYOffset += spacing;
                }
                for (int i = 0; i < halfFeathers; i++)
                {
                    float halfOffset = (flags == 0 && fullFeathers == 0 && i == 0) ? 3f : 0f;
                    Rect drawRect = new Rect(baseRect.x, baseRect.y + currentYOffset + halfOffset, width, height);
                    if (texHalf != null) GUI.DrawTexture(drawRect, texHalf);
                    currentYOffset += spacing;
                }

                GUI.matrix = backupMatrix;

                // Station dot on top, unrotated
                GUI.DrawTexture(new Rect(pivot.x - 4, pivot.y - 4, 8, 8), texDot ?? Texture2D.whiteTexture);
            }
        }

        // Draws a thin compass circle using a cached ring texture.
        // The texture is created once and reused; it is destroyed with the component.
        private Texture2D compassRingTexture = null;
        private int compassRingSize = 0;

        private void DrawCompassCircle(Vector2 center, float radius)
        {
            int texSize = Mathf.RoundToInt(radius * 2f) + 4; // a little padding
            if (compassRingTexture == null || compassRingSize != texSize)
            {
                if (compassRingTexture != null) Destroy(compassRingTexture);
                compassRingTexture = MakeRingTexture(texSize, 1.5f, new Color(0.55f, 0.55f, 0.55f, 0.85f));
                compassRingSize = texSize;
            }

            float half = texSize / 2f;
            GUI.DrawTexture(new Rect(center.x - half, center.y - half, texSize, texSize), compassRingTexture);
        }

        // Generates a square texture with an antialiased ring drawn into it.
        private static Texture2D MakeRingTexture(int size, float thickness, Color ringColor)
        {
            Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Bilinear;
            Color[] pixels = new Color[size * size];
            float cx = size / 2f;
            float cy = size / 2f;
            float outerR = cx - 1f;
            float innerR = outerR - thickness;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = x - cx;
                    float dy = y - cy;
                    float dist = Mathf.Sqrt(dx * dx + dy * dy);

                    // Soft anti-aliased edge: ramp alpha over ±0.75px
                    float alpha = 0f;
                    if (dist > innerR && dist < outerR)
                    {
                        alpha = ringColor.a;
                    }
                    else if (dist <= innerR)
                    {
                        float fade = Mathf.InverseLerp(innerR - 0.75f, innerR, dist);
                        alpha = ringColor.a * fade;
                    }
                    else
                    {
                        float fade = 1f - Mathf.InverseLerp(outerR, outerR + 0.75f, dist);
                        alpha = ringColor.a * fade;
                    }

                    pixels[y * size + x] = new Color(ringColor.r, ringColor.g, ringColor.b, alpha);
                }
            }

            tex.SetPixels(pixels);
            tex.Apply();
            return tex;
        }

        private void DrawControlTab()
        {
            GUILayout.Space(15);
            GUILayout.BeginHorizontal();
            GUILayout.Label("HDG:", GUILayout.Width(55));
            inputHeading = GUILayout.TextField(inputHeading);
            GUILayout.EndHorizontal();

            GUILayout.Space(10);
            GUILayout.BeginHorizontal();
            GUILayout.Label("HOR:", GUILayout.Width(55));
            inputHoriz = GUILayout.TextField(inputHoriz);
            GUILayout.EndHorizontal();

            GUILayout.Space(10);
            GUILayout.BeginHorizontal();
            GUILayout.Label("VER:", GUILayout.Width(55));
            inputVert = GUILayout.TextField(inputVert);
            GUILayout.EndHorizontal();

            GUILayout.Space(10);

            string btnText = controlProvider.IsActive ? "<color=red>DEACTIVATE</color>" : "<color=#00FF00>ACTIVATE</color>";
            if (GUILayout.Button(btnText))
            {
                controlProvider.IsActive = !controlProvider.IsActive;
            }

            // Sync User Strings to Provider
            if (float.TryParse(inputHeading, out float h)) controlProvider.Heading = h;
            if (float.TryParse(inputHoriz, out float spd)) controlProvider.HorizSpeed = spd;
            if (float.TryParse(inputVert, out float v)) controlProvider.VertSpeed = v;

            GUILayout.FlexibleSpace();
        }
    }
}