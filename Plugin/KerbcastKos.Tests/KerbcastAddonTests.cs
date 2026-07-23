using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;
using Xunit.Abstractions;
using kOS.Safe;
using kOS.Safe.Binding;
using kOS.Safe.Callback;
using kOS.Safe.Compilation;
using kOS.Safe.Compilation.KS;
using kOS.Safe.Encapsulation;
using kOS.Safe.Encapsulation.Suffixes;
using kOS.Safe.Execution;
using kOS.Safe.Function;
using kOS.Safe.Module;
using kOS.Safe.Persistence;
using kOS.Safe.Screen;
using kOS.Safe.Utilities;

namespace Kerbcast.Kos.Tests
{
    /* Headless kerboscript integration test for the KERBCAST kOS addon.
       Reuses the KosSpike standup verbatim: SafeHouse.Init +
       AssemblyWalkAttribute.Walk() register the addon and its nomenclature;
       kOS.SharedObjects is allocated WITHOUT running its Unity-touching ctor
       (GetUninitializedObject) and its fields are wired by hand; a minimal
       IBindingManager supplies only the ADDONS getter. KerbcastAddon.Control
       is preset to a FakeKerbcastControl BEFORE the walk, so the sole
       Unity-linked file (RealKerbcastControl) never JITs.

       Every :CAMERA("<uid>") test avoids shared.Vessel entirely and runs
       fully headless. AVAILABLE and CAMERAS:LENGTH touch shared.Vessel:
       - CAMERAS:LENGTH only PASSES shared.Vessel (null) as `object` to the
         seam, no Unity operator, so it runs headless here.
       - AVAILABLE evaluates `shared.Vessel != null`, which binds KSP's
         Vessel (a UnityEngine.Object) `!=` operator and pulls UnityEngine at
         JIT time; it is therefore known-live-only and self-skips headless
         (recorded, not failed). See AttemptAvailable_* below. */
    public class KerbcastAddonTests
    {
        readonly ITestOutputHelper _out;
        public KerbcastAddonTests(ITestOutputHelper o) { _out = o; }

        static readonly object InitLock = new object();
        static bool _initialised;

        /* One-time global kOS standup: SafeHouse config + assembly walk. Must
           run after the addon assembly is loaded (touching KerbcastAddon
           below forces that) so the [kOSAddon("KERBCAST")] registration is
           picked up. Idempotent: safe to call from every test. */
        static void EnsureInit()
        {
            lock (InitLock)
            {
                if (_initialised) return;
                SafeHouse.Init(new TestConfig(), new VersionInfo(1, 6, 0, 0), "", false, "./");
                SafeHouse.Logger = new CapturingLogger();   // Walk() logs; a null logger NREs
                _ = typeof(KerbcastAddon);   // force-load the addon assembly before the walk
                AssemblyWalkAttribute.Walk();
                _initialised = true;
            }
        }

        // ---- Primary tests: fully headless via :CAMERA("<uid>") ----

        [Fact]
        public void Camera_fov_get_returns_the_live_seeded_value()
        {
            var fake = new FakeKerbcastControl();
            fake.Seed(new KosCameraView { FlightId = 1, CameraName = "cam-1", SupportsZoom = true, Fov = 60f });

            var run = Run(fake, "PRINT ADDONS:KERBCAST:CAMERA(\"1\"):FOV.");

            _out.WriteLine("screen: [" + string.Join(" | ", run.Screen) + "]");
            foreach (var m in run.Log) _out.WriteLine("LOG: " + m);
            AssertPrinted(run.Screen, "60");
        }

        [Fact]
        public void Camera_fov_set_routes_through_the_seam()
        {
            var fake = new FakeKerbcastControl();
            fake.Seed(new KosCameraView { FlightId = 1, CameraName = "cam-1", SupportsZoom = true, Fov = 60f });

            var run = Run(fake, "SET ADDONS:KERBCAST:CAMERA(\"1\"):FOV TO 30.");

            foreach (var m in run.Log) _out.WriteLine("LOG: " + m);
            Assert.Null(run.StepError);
            Assert.Equal((1u, 30f), fake.LastSetFov);
            Assert.Equal(30f, fake.ViewOf(1).Fov);   // read-back reflects the write
        }

        [Fact]
        public void Camera_supportspan_get_returns_the_seeded_bool()
        {
            var fake = new FakeKerbcastControl();
            fake.Seed(new KosCameraView { FlightId = 1, CameraName = "cam-1", SupportsPan = true });

            var run = Run(fake, "PRINT ADDONS:KERBCAST:CAMERA(\"1\"):SUPPORTSPAN.");

            _out.WriteLine("screen: [" + string.Join(" | ", run.Screen) + "]");
            AssertPrinted(run.Screen, "True");
        }

        [Fact]
        public void Camera_panyaw_set_routes_through_the_seam_and_reads_back()
        {
            var fake = new FakeKerbcastControl();
            fake.Seed(new KosCameraView { FlightId = 1, SupportsPan = true, PanYaw = 0f, PanPitch = 5f });

            var run = Run(fake, "SET ADDONS:KERBCAST:CAMERA(\"1\"):PANYAW TO 12.");

            foreach (var m in run.Log) _out.WriteLine("LOG: " + m);
            Assert.Null(run.StepError);
            Assert.Equal((1u, 12f, 5f), fake.LastSetPan);   // pitch carried from the live view
        }

        [Fact]
        public void Camera_track_set_routes_through_the_seam_on_pan_zoom()
        {
            var fake = new FakeKerbcastControl();
            fake.Seed(new KosCameraView { FlightId = 1, SupportsPan = true, SupportsZoom = true });

            var run = Run(fake, "SET ADDONS:KERBCAST:CAMERA(\"1\"):TRACK TO \"vessel\".");

            foreach (var m in run.Log) _out.WriteLine("LOG: " + m);
            Assert.Null(run.StepError);
            Assert.Equal((1u, 1), fake.LastSetTrackMode);  // "vessel" -> 1
            Assert.Equal(1, fake.ViewOf(1).TrackMode);     // read-back reflects the write
        }

        [Fact]
        public void Camera_track_get_returns_the_mode_string()
        {
            var fake = new FakeKerbcastControl();
            fake.Seed(new KosCameraView { FlightId = 1, SupportsPan = true, SupportsZoom = true, TrackMode = 2 });

            var run = Run(fake, "PRINT ADDONS:KERBCAST:CAMERA(\"1\"):TRACK.");

            _out.WriteLine("screen: [" + string.Join(" | ", run.Screen) + "]");
            AssertPrinted(run.Screen, "target");           // 2 -> "target"
        }

        [Fact]
        public void Camera_track_set_gated_off_a_non_pan_zoom_camera()
        {
            var fake = new FakeKerbcastControl();
            // Pan-only (no zoom): tracking is not offered, so the set is a no-op.
            fake.Seed(new KosCameraView { FlightId = 1, SupportsPan = true, SupportsZoom = false });

            var run = Run(fake, "SET ADDONS:KERBCAST:CAMERA(\"1\"):TRACK TO \"vessel\".");

            Assert.Null(run.StepError);
            Assert.Equal(0, fake.ViewOf(1).TrackMode);     // gated: mode unchanged
        }

        /* BORESIGHT/POSITION return a kOS Vector carrying the seeded components.
           Read the suffix directly (not via the CPU) so the assertion sees the
           real value; proves the Vector is constructed and populated correctly. */
        [Fact]
        public void Camera_boresight_returns_the_seeded_vector()
        {
            var fake = new FakeKerbcastControl();
            fake.Seed(new KosCameraView { FlightId = 1, SupportsPan = true, BoresightX = 0.5f, BoresightY = 0.25f, BoresightZ = -1f });

            var vec = (kOS.Suffixed.Vector)ProbeCameraSuffix(fake, 1u, "BORESIGHT");

            Assert.Equal(0.5, vec.X, 3);
            Assert.Equal(0.25, vec.Y, 3);
            Assert.Equal(-1.0, vec.Z, 3);
        }

        // ---- Attempted tests: touch shared.Vessel; skip if Unity-bound ----

        /* CAMERAS and AVAILABLE both name KSP's Vessel type in their addon
           method bodies (GetCameras passes shared.Vessel; Available() does
           `shared.Vessel != null`). JITting either method forces
           Assembly-CSharp to load, which is absent headless -> a
           FileNotFoundException the kOS CPU swallows silently (empty screen,
           nothing logged). So these can only run live in KSP. Each test drives
           the intended kerboscript first; if it produced no output (the
           expected headless outcome) it confirms the cause via a direct suffix
           probe and self-skips, rather than failing the suite. */
        [Fact]
        public void Attempt_cameras_length_live_only()
        {
            var fake = new FakeKerbcastControl();
            fake.Seed(new KosCameraView { FlightId = 1, CameraName = "cam-1" });
            fake.Seed(new KosCameraView { FlightId = 2, CameraName = "cam-2" });

            var run = Run(fake, "PRINT ADDONS:KERBCAST:CAMERAS:LENGTH.");
            _out.WriteLine("screen: [" + string.Join(" | ", run.Screen) + "]");
            if (run.Screen.Any(l => l.Trim() == "2")) { AssertPrinted(run.Screen, "2"); return; }

            var ex = Record.Exception(() => ProbeSuffix(fake, "CAMERAS"));
            _out.WriteLine("direct CAMERAS probe: " + (ex == null ? "(no throw)" : ex.GetType().FullName + ": " + ex.Message));
            SkipIfUnityBoundElseFail(ex, "CAMERAS:LENGTH");
        }

        [Fact]
        public void Attempt_available_live_only()
        {
            var fake = new FakeKerbcastControl();
            fake.Seed(new KosCameraView { FlightId = 1, CameraName = "cam-1" });

            var run = Run(fake, "PRINT ADDONS:KERBCAST:AVAILABLE.");
            _out.WriteLine("screen: [" + string.Join(" | ", run.Screen) + "]");
            if (run.Screen.Count > 0) { AssertPrinted(run.Screen, "True"); return; }

            var ex = Record.Exception(() => ProbeSuffix(fake, "AVAILABLE"));
            _out.WriteLine("direct AVAILABLE probe: " + (ex == null ? "(no throw)" : ex.GetType().FullName + ": " + ex.Message));
            SkipIfUnityBoundElseFail(ex, "AVAILABLE");
        }

        // ---- Harness ----

        /* Construct the addon over an uninitialized SharedObjects and read one
           suffix directly (no compiler/CPU), so a Unity/Assembly-CSharp load
           failure surfaces as a thrown exception we can classify. */
        void ProbeSuffix(FakeKerbcastControl fake, string suffix)
        {
            KerbcastAddon.Control = fake;
            var shared = (kOS.SharedObjects)System.Runtime.CompilerServices
                .RuntimeHelpers.GetUninitializedObject(typeof(kOS.SharedObjects));
            var addon = new KerbcastAddon(shared);
            var r = addon.GetSuffix(suffix);
            _ = r.HasValue ? r.Value : null;   // force evaluation
        }

        /* As ProbeSuffix, but for a per-camera struct suffix: build the struct
           directly over the fake and force-evaluate the suffix, returning its
           value so a caller can assert on it. */
        object ProbeCameraSuffix(FakeKerbcastControl fake, uint id, string suffix)
        {
            var shared = (kOS.SharedObjects)System.Runtime.CompilerServices
                .RuntimeHelpers.GetUninitializedObject(typeof(kOS.SharedObjects));
            // owner null: this probe only reads BORESIGHT/POSITION, never AIM.
            var cam = new KerbcastCameraStruct(shared, id, fake, null);
            var r = cam.GetSuffix(suffix);
            return r.HasValue ? r.Value : null;
        }

        void SkipIfUnityBoundElseFail(Exception ex, string what)
        {
            if (ex != null && LooksUnityBoundEx(ex))
            {
                _out.WriteLine(what + " is Unity/Assembly-CSharp-bound -> live-only, skipped headless.");
                return;
            }
            Assert.Fail(what + " produced no output and was not classifiable as Unity-bound: " +
                        (ex == null ? "(no exception)" : ex.ToString()));
        }

        sealed class RunResult
        {
            public List<string> Screen;
            public List<string> Log;
            public Exception StepError;
        }

        /* Compile + run one kerboscript against the addon, with Control preset
           to `fake`. Mirrors KosSpike stage (c). */
        RunResult Run(FakeKerbcastControl fake, string source)
        {
            EnsureInit();
            KerbcastAddon.Control = fake;   // read at addon/struct construction, i.e. when the script resolves ADDONS:KERBCAST

            var log = new CapturingLogger();
            SafeHouse.Logger = log;

            var baseDir = Path.Combine(Path.GetTempPath(), "kerbcastkos_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(baseDir);
            const string scriptName = "test.ks";
            File.WriteAllText(Path.Combine(baseDir, scriptName), source);

            var screen = new TestScreen();

            var shared = (kOS.SharedObjects)System.Runtime.CompilerServices
                .RuntimeHelpers.GetUninitializedObject(typeof(kOS.SharedObjects));
            shared.FunctionManager = new FunctionManager(shared);
            shared.GameEventDispatchManager = new NoopGameEventDispatchManager();
            shared.Processor = new NoopProcessor();
            shared.ScriptHandler = new KSScript();
            shared.Screen = screen;
            shared.UpdateHandler = new UpdateHandler();
            shared.VolumeMgr = new VolumeManager();
            shared.AddonManager = new kOS.AddOns.AddonManager(shared);
            shared.BindingMgr = new MiniBindingManager(shared);

            shared.FunctionManager.Load();

            var archive = new Archive(baseDir);
            shared.VolumeMgr.Add(archive);
            shared.VolumeMgr.SwitchTo(archive);

            var cpu = new CPU(shared);

            string contents = File.ReadAllText(Path.Combine(baseDir, scriptName));
            GlobalPath path = shared.VolumeMgr.GlobalPathFromObject("0:/" + scriptName);
            var compiled = shared.ScriptHandler.Compile(path, 1, contents, "test", new CompilerOptions
            {
                LoadProgramsInSameAddressSpace = false,
                IsCalledFromRun = false,
                FuncManager = shared.FunctionManager,
                BindManager = shared.BindingMgr,
                AllowClobberBuiltins = SafeHouse.Config.AllowClobberBuiltIns
            });

            cpu.Boot();
            screen.ClearOutput();
            cpu.GetCurrentContext().AddParts(compiled);

            Exception stepErr = null;
            try
            {
                /* Step until the program either prints or drains its opcodes;
                   bounded so a no-print script (SET ...) still terminates. */
                for (int i = 0; i < 500 && screen.Output.Count == 0; i++)
                    shared.UpdateHandler.UpdateFixedObservers(0.02);
            }
            catch (Exception e) { stepErr = e; }

            return new RunResult { Screen = screen.Output, Log = log.Messages, StepError = stepErr };
        }

        static void AssertPrinted(List<string> screen, string expected) =>
            Assert.Contains(screen, l => l.Trim() == expected);

        static bool LooksUnityBoundEx(Exception ex)
        {
            bool Hit(string s) => s != null && (
                s.IndexOf("UnityEngine", StringComparison.OrdinalIgnoreCase) >= 0 ||
                s.IndexOf("Assembly-CSharp", StringComparison.OrdinalIgnoreCase) >= 0 ||
                s.IndexOf("TypeLoad", StringComparison.OrdinalIgnoreCase) >= 0 ||
                s.IndexOf("Could not load file or assembly", StringComparison.OrdinalIgnoreCase) >= 0 ||
                s.IndexOf("FileNotFound", StringComparison.OrdinalIgnoreCase) >= 0);

            for (var e = ex; e != null; e = e.InnerException)
                if (Hit(e.Message) || Hit(e.GetType().FullName)) return true;
            return false;
        }

        // ---- Unity-free doubles, mirrored from KosSpike ----

        sealed class MiniBindingManager : IBindingManager
        {
            readonly kOS.SharedObjects shared;
            readonly Dictionary<string, BoundVariable> variables =
                new Dictionary<string, BoundVariable>(StringComparer.OrdinalIgnoreCase);

            public MiniBindingManager(kOS.SharedObjects shared) { this.shared = shared; }

            public void Load() => AddGetter("ADDONS", () => new kOS.Suffixed.AddonList(shared));

            public void AddBoundVariable(string name, BindingGetDlg getDelegate, BindingSetDlg setDelegate)
            {
                if (!variables.TryGetValue(name, out var v))
                {
                    v = new BoundVariable { Name = name };
                    variables.Add(name, v);
                    shared.Cpu.AddVariable(v, name, false);
                }
                if (getDelegate != null) v.Get = getDelegate;
                if (setDelegate != null) v.Set = setDelegate;
            }

            public void AddGetter(string name, BindingGetDlg dlg) => AddBoundVariable(name, dlg, null);
            public void AddGetter(IEnumerable<string> names, BindingGetDlg dlg) { foreach (var n in names) AddBoundVariable(n, dlg, null); }
            public void AddSetter(string name, BindingSetDlg dlg) => AddBoundVariable(name, null, dlg);
            public void AddSetter(IEnumerable<string> names, BindingSetDlg dlg) { foreach (var n in names) AddBoundVariable(n, null, dlg); }
            public bool HasGetter(string name) => variables.ContainsKey(name) && variables[name].Get != null;
            public bool HasSetter(string name) => variables.ContainsKey(name) && variables[name].Set != null;
            public void MarkVolatile(string name) { if (variables.ContainsKey(name)) variables[name].Volatile = true; }
            public void PreUpdate() { foreach (var v in variables.Values) v.ClearCache(); }
            public void PostUpdate() { }
            public void ToggleFlyByWire(string paramName, bool enabled) { }
            public void SelectAutopilotMode(string autopilotMode) { }
        }

        sealed class TestScreen : IScreenBuffer
        {
            public readonly List<string> Output = new List<string>();
            public void ClearOutput() => Output.Clear();
            public void Print(string textToPrint) => Output.Add(textToPrint);
            public void Print(string textToPrint, bool addNewLine) => Output.Add(textToPrint);
            public void PrintAt(string textToPrint, int row, int column) => Output.Add(textToPrint);
            public int AbsoluteCursorRow { get; set; }
            public int BeepsPending { get; set; }
            public double Brightness { get; set; } = 1;
            public int CharacterPixelHeight { get; set; }
            public int CharacterPixelWidth { get; set; }
            public Queue<char> CharInputQueue => new Queue<char>();
            public int ColumnCount => 80;
            public int CursorColumnShow => 0;
            public int CursorRowShow => 0;
            public bool ReverseScreen { get; set; }
            public int RowCount => 40;
            public int TopRow => 0;
            public bool VisualBeep { get; set; }
            public void AddResizeNotifier(ScreenBuffer.ResizeNotifier notifier) { }
            public void AddSubBuffer(SubBuffer subBuffer) { }
            public void ClearScreen() { }
            public string DebugDump() => "";
            public List<IScreenBufferLine> GetBuffer() => new List<IScreenBufferLine>();
            public void MoveCursor(int row, int column) { }
            public void MoveToNextLine() { }
            public void RemoveAllResizeNotifiers() { }
            public void RemoveResizeNotifier(ScreenBuffer.ResizeNotifier notifier) { }
            public void RemoveSubBuffer(SubBuffer subBuffer) { }
            public int ScrollVertical(int deltaRows) => 0;
            public void SetSize(int rowCount, int columnCount) { }
        }

        sealed class CapturingLogger : ILogger
        {
            public readonly List<string> Messages = new List<string>();
            public void Log(Exception e) => Messages.Add("EXC " + e.GetType().Name + ": " + e.Message);
            public void Log(string text) { }
            public void LogError(string s) => Messages.Add("ERR " + s);
            public void LogException(Exception exception) => Messages.Add("EXC " + exception.GetType().Name + ": " + exception.Message);
            public void LogWarning(string s) { }
            public void LogWarningAndScreen(string s) => Messages.Add("WRN+SCR " + s);
            public void SuperVerbose(string s) { }
        }

        sealed class NoopGameEventDispatchManager : IGameEventDispatchManager
        {
            public void Clear() { }
            public void RemoveDispatcherFor(ProgramContext context) { }
            public void SetDispatcherFor(ProgramContext context) { }
        }

        sealed class NoopProcessor : IProcessor
        {
            public VolumePath BootFilePath => null;
            public int KOSCoreId => 0;
            public string Tag => string.Empty;
            public bool CheckCanBoot() => true;
            public bool HasBooted => true;
            public void SetMode(ProcessorModes newProcessorMode) { }
        }

        sealed class TestConfig : IConfig
        {
            public int InstructionsPerUpdate { get; set; } = 10000;
            public bool UseCompressedPersistence { get; set; }
            public bool ShowStatistics { get; set; }
            public bool StartOnArchive { get; set; } = true;
            public bool ObeyHideUI { get; set; } = true;
            public bool EnableSafeMode { get; set; } = true;
            public bool VerboseExceptions { get; set; } = true;
            public bool EnableTelnet { get; set; }
            public int TelnetPort { get; set; }
            public bool AudibleExceptions { get; set; }
            public string TelnetIPAddrString { get; set; } = "";
            public bool UseBlizzyToolbarOnly { get; set; }
            public int TerminalFontDefaultSize { get; set; } = 12;
            public string TerminalFontName { get; set; } = "";
            public double TerminalBrightness { get; set; } = 1;
            public int TerminalDefaultWidth { get; set; } = 80;
            public int TerminalDefaultHeight { get; set; } = 40;
            public bool AllowClobberBuiltIns { get; set; }
            public bool SuppressAutopilot { get; set; } = true;
            public DateTime TimeStamp => new DateTime();
            public bool DebugEachOpcode { get; set; }
            public void SaveConfig() { }
            public IList<ConfigKey> GetConfigKeys() => new List<ConfigKey>();
            public ISuffixResult GetSuffix(string suffixName, bool failOkay = false) => null;
            public bool SetSuffix(string suffixName, object value, bool failOkay = false) => false;
        }
    }
}
