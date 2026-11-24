using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditorInternal;
using UnityEngine;

namespace PoKingHill.EditorTools
{
    /// <summary>
    /// Domain-reload-safe automated review capture for Unity Editor + Play Mode:
    ///   Unity.exe -projectPath <repo> -executeMethod PoKingHill.EditorTools.ReviewCapture.Run -kothExit
    /// Captures Editor viewport gizmo proof (before/after Move gizmo), Play Mode screenshots of new features,
    /// two 30-frame gameplay GIF bursts, and live ONNX inference telemetry into SCREENSHOTS/review-261124/raw.
    /// </summary>
    [InitializeOnLoad]
    public static class ReviewCapture
    {
        const string KeyStage = "PoKingHill.ReviewCapture.Stage";
        const string KeyOut = "PoKingHill.ReviewCapture.Out";
        const string KeyTargetPath = "PoKingHill.ReviewCapture.TargetPath";
        const string KeyBeforePos = "PoKingHill.ReviewCapture.BeforePos";
        const string KeyAfterPos = "PoKingHill.ReviewCapture.AfterPos";

        static int _editTicks;
        static int _playStep;
        static double _nextAt;
        static int _gifIdx;
        static int _exceptions;
        static readonly List<string> _exceptionLines = new();
        static string _overlaySnapshot = "";
        static int _strikesThrownAtOverlay, _strikesLandedAtOverlay;

        static ReviewCapture()
        {
            EditorApplication.update += OnUpdate;
            Application.logMessageReceived += OnLog;
        }

        static void OnLog(string condition, string stackTrace, LogType type)
        {
            if (type == LogType.Exception || type == LogType.Error)
            {
                _exceptions++;
                if (_exceptionLines.Count < 10) _exceptionLines.Add($"{type}: {condition}");
            }
        }

        public static void Run()
        {
            string outDir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "SCREENSHOTS", "review-261124", "raw"));
            Directory.CreateDirectory(outDir);
            Directory.CreateDirectory(Path.Combine(outDir, "gif1"));
            Directory.CreateDirectory(Path.Combine(outDir, "gif2"));

            // Refresh roster brains so Game.unity binds the freshly exported overnight ONNX models
            AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
            GameScene.RefreshBrains();

            SessionState.SetString(KeyOut, outDir);
            SessionState.SetString(KeyStage, "edit_setup");
            _editTicks = 0;
            Debug.Log("[ReviewCapture] Started, output -> " + outDir);
        }

        static void OnUpdate()
        {
            string stage = SessionState.GetString(KeyStage, "");
            if (string.IsNullOrEmpty(stage)) return;
            string outDir = SessionState.GetString(KeyOut, "");
            if (string.IsNullOrEmpty(outDir)) return;

            if (stage == "edit_setup")
            {
                if (EditorApplication.isPlayingOrWillChangePlaymode) return;
                if (++_editTicks < 10) return;

                var target = FindGizmoTarget();
                if (target == null)
                {
                    Debug.LogError("[ReviewCapture] No suitable scene object found for gizmo proof.");
                    Finish(outDir, 1);
                    return;
                }

                string path = HierarchyPath(target.transform);
                Vector3 before = target.transform.position;
                SessionState.SetString(KeyTargetPath, path);
                SessionState.SetString(KeyBeforePos, VecStr(before));

                Selection.activeGameObject = target;
                Tools.current = Tool.Move;
                Tools.pivotMode = PivotMode.Pivot;
                Tools.pivotRotation = PivotRotation.Global;

                var sv = SceneView.lastActiveSceneView ?? EditorWindow.GetWindow<SceneView>();
                sv.Focus();
                sv.in2DMode = false;
                sv.drawGizmos = true;
                sv.LookAt(before, Quaternion.Euler(26f, -38f, 0f), 9.5f, false, true);
                EditorGUIUtility.PingObject(target);
                SceneView.RepaintAll();
                InternalEditorUtility.RepaintAllViews();

                _editTicks = 0;
                SessionState.SetString(KeyStage, "edit_before_shot");
                Debug.Log($"[ReviewCapture] Selected gizmo target '{path}' at {before}");
                return;
            }

            if (stage == "edit_before_shot")
            {
                if (++_editTicks < 12) { SceneView.RepaintAll(); return; }
                bool ok = CaptureEditorWindow(Path.Combine(outDir, "editor_gizmo_before.png"));
                Debug.Log($"[ReviewCapture] Captured editor_gizmo_before.png ok={ok}");

                var target = FindByPath(SessionState.GetString(KeyTargetPath, ""));
                if (target != null)
                {
                    Vector3 delta = new Vector3(2.4f, 0.85f, -1.8f);
                    target.transform.position += delta;
                    SessionState.SetString(KeyAfterPos, VecStr(target.transform.position));
                    Selection.activeGameObject = target;
                    Tools.current = Tool.Move;
                    var sv = SceneView.lastActiveSceneView;
                    if (sv != null)
                    {
                        sv.Focus();
                        sv.LookAt(target.transform.position, Quaternion.Euler(26f, -38f, 0f), 9.5f, false, true);
                    }
                    EditorUtility.SetDirty(target.transform);
                    SceneView.RepaintAll();
                    InternalEditorUtility.RepaintAllViews();
                    Debug.Log($"[ReviewCapture] Moved '{target.name}' by {delta} -> {target.transform.position}");
                }

                _editTicks = 0;
                SessionState.SetString(KeyStage, "edit_after_shot");
                return;
            }

            if (stage == "edit_after_shot")
            {
                if (++_editTicks < 12) { SceneView.RepaintAll(); return; }
                bool ok = CaptureEditorWindow(Path.Combine(outDir, "editor_gizmo_after.png"));
                Debug.Log($"[ReviewCapture] Captured editor_gizmo_after.png ok={ok}");

                // Write gizmo proof JSON
                string gizmoJson =
                    "{\n" +
                    $"  \"object\": \"{Escape(SessionState.GetString(KeyTargetPath, ""))}\",\n" +
                    $"  \"before\": [{SessionState.GetString(KeyBeforePos, "0,0,0")}],\n" +
                    $"  \"after\": [{SessionState.GetString(KeyAfterPos, "0,0,0")}],\n" +
                    "  \"delta\": [2.4, 0.85, -1.8]\n" +
                    "}\n";
                File.WriteAllText(Path.Combine(outDir, "gizmo_proof.json"), gizmoJson);

                // Size Game View to 540x960 (crisp 9:16 portrait) and enter Play Mode WITHOUT saving the temporary gizmo move
                Debug.Log("[ReviewCapture] " + HudShots.SetGameView(540, 960));
                FocusGameView();
                SessionState.SetString(KeyStage, "play_run");
                _playStep = 0;
                EditorApplication.isPlaying = true;
                return;
            }

            if (stage == "play_run")
            {
                if (!EditorApplication.isPlaying) return;
                TickPlayMode(outDir);
                return;
            }

            if (stage == "done_exit")
            {
                if (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode) return;
                if (++_editTicks < 8) return;
                // Reopen clean Game.unity so the temporary gizmo move is discarded cleanly
                EditorSceneManager.OpenScene(GameScene.Path, OpenSceneMode.Single);
                Finish(outDir, 0);
            }
        }

        static void TickPlayMode(string outDir)
        {
            double now = EditorApplication.timeSinceStartup;
            if (now < _nextAt) return;

            var dir = MatchDirector.Instance;
            var hud = UnityEngine.Object.FindFirstObjectByType<HudView>();
            if (dir == null || hud == null) return;

            switch (_playStep)
            {
                case 0:
                    Application.runInBackground = true;
                    Application.targetFrameRate = 60;
                    FocusGameView();
                    _nextAt = now + 2.0;
                    _playStep = 1;
                    Debug.Log("[ReviewCapture] PlayMode initialized, waiting for Menu...");
                    break;

                case 1:
                    // Capture Main Menu / Tournament & Elo Roster
                    ScreenCapture.CaptureScreenshot(Path.Combine(outDir, "01_menu_roster.png"));
                    Debug.Log("[ReviewCapture] Shot 01_menu_roster.png");
                    _nextAt = now + 0.6;
                    _playStep = 2;
                    break;

                case 2:
                    // Launch 8-fighter FFA Quick Match on the widened Concrete Platform
                    dir.QuickMatch();
                    Debug.Log($"[ReviewCapture] Launched QuickMatch (playing={dir.Playing}, outRadius={dir.OutRadiusNow})");
                    _nextAt = now + 1.0;
                    _playStep = 3;
                    break;

                case 3:
                    // Capture Tale-of-the-Tape Intro shot, then skip straight to live fighting
                    ScreenCapture.CaptureScreenshot(Path.Combine(outDir, "02_intro_tape.png"));
                    Debug.Log("[ReviewCapture] Shot 02_intro_tape.png");
                    _nextAt = now + 0.5;
                    _playStep = 4;
                    break;

                case 4:
                    dir.SkipIntro();
                    Debug.Log("[ReviewCapture] Skipped intro -> live Fight state!");
                    _gifIdx = 0;
                    _nextAt = now + 0.6;
                    _playStep = 5;
                    break;

                case 5:
                    // GIF Burst 1: 30 frames @ ~8.3 fps (3.6 s of opening 8-fighter brawl on concrete platform)
                    if (_gifIdx < 30)
                    {
                        ScreenCapture.CaptureScreenshot(Path.Combine(outDir, "gif1", $"f_{_gifIdx:000}.png"));
                        _gifIdx++;
                        _nextAt = now + 0.12;
                    }
                    else
                    {
                        Debug.Log($"[ReviewCapture] Captured gif1 (30 frames), strikes={dir.StrikesThrown}/{dir.StrikesLanded}");
                        if (!hud.OverlayShown) hud.ToggleOverlay();
                        _nextAt = now + 0.8;
                        _playStep = 6;
                    }
                    break;

                case 6:
                    // Shot 3: Combat + TAB RL Diagnostics Overlay
                    _overlaySnapshot = hud.OverlayText ?? "";
                    _strikesThrownAtOverlay = dir.StrikesThrown;
                    _strikesLandedAtOverlay = dir.StrikesLanded;
                    ScreenCapture.CaptureScreenshot(Path.Combine(outDir, "03_combat_rl_overlay.png"));
                    Debug.Log("[ReviewCapture] Shot 03_combat_rl_overlay.png | " + _overlaySnapshot.Replace("\n", " / "));
                    _nextAt = now + 0.5;
                    _playStep = 7;
                    break;

                case 7:
                    if (hud.OverlayShown) hud.ToggleOverlay();
                    _nextAt = now + 0.8;
                    _playStep = 8;
                    break;

                case 8:
                    // Shot 4: Clean unobstructed Shoves & Jabs / Concrete Platform brawl shot
                    ScreenCapture.CaptureScreenshot(Path.Combine(outDir, "04_shoves_action.png"));
                    Debug.Log($"[ReviewCapture] Shot 04_shoves_action.png (strikes thrown={dir.StrikesThrown}, landed={dir.StrikesLanded})");
                    _gifIdx = 0;
                    _nextAt = now + 0.4;
                    _playStep = 9;
                    break;

                case 9:
                    // GIF Burst 2: 30 frames of mid/late-round concrete platform ejections, get-ups & rising sea
                    if (_gifIdx < 30)
                    {
                        ScreenCapture.CaptureScreenshot(Path.Combine(outDir, "gif2", $"f_{_gifIdx:000}.png"));
                        _gifIdx++;
                        _nextAt = now + 0.12;
                    }
                    else
                    {
                        Debug.Log($"[ReviewCapture] Captured gif2 (30 frames), alive={dir.Alive.Count}, fallen={dir.Fallen.Count}");
                        _nextAt = now + 0.4;
                        _playStep = 10;
                    }
                    break;

                case 10:
                    // Capture full Unity Editor window DURING Play Mode proving gizmo-moved object + unbroken gameplay
                    var movedObj = FindByPath(SessionState.GetString(KeyTargetPath, ""));
                    if (movedObj != null)
                    {
                        Selection.activeGameObject = movedObj;
                        Tools.current = Tool.Move;
                    }
                    SceneView.RepaintAll();
                    InternalEditorUtility.RepaintAllViews();
                    CaptureEditorWindow(Path.Combine(outDir, "05_editor_playmode_unbroken.png"));
                    Debug.Log("[ReviewCapture] Captured 05_editor_playmode_unbroken.png");

                    WriteTelemetry(outDir, dir, movedObj);
                    _editTicks = 0;
                    SessionState.SetString(KeyStage, "done_exit");
                    EditorApplication.isPlaying = false;
                    break;
            }
        }

        static void WriteTelemetry(string outDir, MatchDirector dir, GameObject movedObj)
        {
            double latSum = 0; int latN = 0;
            var brainLines = new List<string>();
            for (int i = 0; i < dir.Playing && i < dir.bodies.Length; i++)
            {
                var b = dir.bodies[i];
                if (b == null) continue;
                if (b.LastInferenceMs > 0) { latSum += b.LastInferenceMs; latN++; }
                string pname = b.policy != null ? b.policy.name : "none";
                brainLines.Add($"    \"{Escape(b.robotPrefix)} ({Escape(b.body)}): {Escape(pname)} (obs={b.policyObsDim}, inf={b.LastInferenceMs:0.00} ms)\"");
            }
            double avgLat = latN > 0 ? latSum / latN : -1;
            string movedPos = movedObj != null ? VecStr(movedObj.transform.position) : "null";
            string json =
                "{\n" +
                $"  \"playing\": {dir.Playing},\n" +
                $"  \"alive\": {dir.Alive.Count},\n" +
                $"  \"fallen\": {dir.Fallen.Count},\n" +
                $"  \"out_radius_m\": {dir.OutRadiusNow:0.00},\n" +
                $"  \"physics_step_ms\": {dir.StepMs:0.000},\n" +
                $"  \"avg_inference_ms\": {avgLat:0.000},\n" +
                $"  \"brains_ran\": {latN},\n" +
                $"  \"strikes_thrown\": {dir.StrikesThrown},\n" +
                $"  \"strikes_landed\": {dir.StrikesLanded},\n" +
                $"  \"strikes_at_overlay\": [{_strikesThrownAtOverlay}, {_strikesLandedAtOverlay}],\n" +
                $"  \"round_time_s\": {dir.RoundTime:0.00},\n" +
                $"  \"state\": \"{dir.State}\",\n" +
                $"  \"overlay_text\": \"{Escape(_overlaySnapshot)}\",\n" +
                $"  \"moved_object\": \"{Escape(SessionState.GetString(KeyTargetPath, ""))}\",\n" +
                $"  \"moved_pos_in_play\": [{movedPos}],\n" +
                $"  \"exceptions\": {_exceptions},\n" +
                $"  \"active_brains\": [\n{string.Join(",\n", brainLines)}\n  ]\n" +
                "}\n";
            File.WriteAllText(Path.Combine(outDir, "playmode_telemetry.json"), json);
            Debug.Log("[ReviewCapture] Wrote playmode_telemetry.json");
        }

        static void Finish(string outDir, int exitCode)
        {
            SessionState.EraseString(KeyStage);
            Debug.Log($"[ReviewCapture] Finished (exitCode={exitCode})");
            if (Cli.Exit) EditorApplication.Exit(exitCode);
        }

        static GameObject FindGizmoTarget()
        {
            var roots = UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects();
            string[] preferred = { "Banner", "Colonnade", "Pillar", "Crag", "Arch", "Platform", "Rock", "Tree" };
            foreach (string kw in preferred)
            {
                foreach (var r in roots)
                {
                    if (r.name.StartsWith("Mj_")) continue;
                    foreach (var t in r.GetComponentsInChildren<Transform>(false))
                    {
                        if (t.name.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0 && t.GetComponent<Renderer>() != null)
                            return t.gameObject;
                    }
                }
            }
            // Fallback: any visible non-Mj renderer, else Directional Light
            foreach (var r in roots)
            {
                if (r.name.StartsWith("Mj_") || r.name == "ArenaVisual (render only)" || r.name == "Sea (render only)") continue;
                var ren = r.GetComponentInChildren<Renderer>(false);
                if (ren != null) return ren.gameObject;
            }
            return roots.FirstOrDefault(r => r.name == "Directional Light") ?? roots.FirstOrDefault(r => r.name == "Sea (render only)");
        }

        static GameObject FindByPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            var parts = path.Split('/');
            var roots = UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects();
            Transform cur = roots.FirstOrDefault(r => r.name == parts[0])?.transform;
            for (int i = 1; i < parts.Length && cur != null; i++) cur = cur.Find(parts[i]);
            return cur != null ? cur.gameObject : null;
        }

        static string HierarchyPath(Transform t)
        {
            var stack = new Stack<string>();
            for (; t != null; t = t.parent) stack.Push(t.name);
            return string.Join("/", stack);
        }

        static string VecStr(Vector3 v) =>
            $"{v.x.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}, {v.y.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}, {v.z.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}";

        static string Escape(string s) => (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", "\\n");

        static void FocusGameView()
        {
            var gvType = typeof(Editor).Assembly.GetType("UnityEditor.GameView");
            if (gvType != null)
            {
                var w = EditorWindow.GetWindow(gvType, false, null, true);
                w.Focus();
                w.Repaint();
            }
        }

        // ------------------------------------------------------------------ Win32 PrintWindow (window-only Editor capture)
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] struct BITMAPINFOHEADER
        {
            public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount;
            public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter;
            public uint biClrUsed, biClrImportant;
        }

        delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);
        [DllImport("user32.dll")] static extern IntPtr GetWindowDC(IntPtr hWnd);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr hObj);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr hObj);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] static extern int GetDIBits(IntPtr hdc, IntPtr hbmp, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER bmi, uint usage);

        static IntPtr FindMainUnityHwnd()
        {
            uint myPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
            IntPtr best = IntPtr.Zero;
            int bestArea = 0;
            EnumWindows((h, _) =>
            {
                if (!IsWindowVisible(h)) return true;
                GetWindowThreadProcessId(h, out uint pid);
                if (pid != myPid) return true;
                if (GetWindowRect(h, out RECT r))
                {
                    int area = (r.Right - r.Left) * (r.Bottom - r.Top);
                    if (area > bestArea) { bestArea = area; best = h; }
                }
                return true;
            }, IntPtr.Zero);
            return best;
        }

        static bool CaptureEditorWindow(string path)
        {
            IntPtr hwnd = FindMainUnityHwnd();
            if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out RECT r)) return false;
            int w = r.Right - r.Left, h = r.Bottom - r.Top;
            if (w <= 0 || h <= 0) return false;

            IntPtr wdc = GetWindowDC(hwnd);
            IntPtr mdc = CreateCompatibleDC(wdc);
            IntPtr bmp = CreateCompatibleBitmap(wdc, w, h);
            IntPtr old = SelectObject(mdc, bmp);
            bool ok = PrintWindow(hwnd, mdc, 2); // PW_RENDERFULLCONTENT = 2

            var bmi = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = w,
                biHeight = h, // bottom-up matches Texture2D!
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0
            };
            byte[] bgra = new byte[w * h * 4];
            GetDIBits(mdc, bmp, 0, (uint)h, bgra, ref bmi, 0);

            SelectObject(mdc, old);
            DeleteObject(bmp);
            DeleteDC(mdc);
            ReleaseDC(hwnd, wdc);

            if (!ok) return false;
            // Convert BGRA -> RGBA (alpha = 255)
            for (int i = 0; i < bgra.Length; i += 4)
            {
                byte b = bgra[i];
                bgra[i] = bgra[i + 2];
                bgra[i + 2] = b;
                bgra[i + 3] = 255;
            }
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            tex.LoadRawTextureData(bgra);
            tex.Apply();
            File.WriteAllBytes(path, tex.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(tex);
            return true;
        }
    }
}
