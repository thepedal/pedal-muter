// Pedal Muter — ReBuzz Managed Control Machine
//
// Mutes any number of native or managed machines via pattern automation
// or peer control.  Each track on this machine maps to one or more target
// machines; setting the track's Mute parameter to 1 mutes them all (via
// IMachine.IsMuted), 0 unmutes them.
//
// Build:  dotnet build -c Release  (set ReBuzzDir in Directory.Build.props)
// Output: $(ReBuzzDir)\Gear\Generators\

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using Buzz.MachineInterface;
using BuzzGUI.Interfaces;

namespace WDE.PedalMuter
{
    // =========================================================================
    // One assignment on a track  (a track may have many of these)
    // =========================================================================

    [Serializable]
    public class MuteAssignment
    {
        public string MachineName { get; set; } = "";

        // Resolved at runtime; not serialised.
        [System.Xml.Serialization.XmlIgnore]
        public IMachine ResolvedMachine;
    }

    // =========================================================================
    // Runtime per-track state (rebuilt from parameters each session)
    // =========================================================================

    public class TrackState
    {
        public bool MuteState = false;
        public List<MuteAssignment> Assignments = new List<MuteAssignment>();

        // Decremented each Work cycle while > 0; while non-zero, the track's
        // assigned targets get Note-Off injection on every Work pass.  Set
        // by SetMute on the mute-to-unmute transition.  See §StaleVoiceFlush
        // in the PedalMuter source comments.
        public int PostUnmuteFlushBuffers = 0;
    }

    // =========================================================================
    // Serialisable machine state
    // =========================================================================

    [Serializable]
    public class PedalMuterState
    {
        public List<List<MuteAssignment>> TrackAssignments { get; set; }
            = new List<List<MuteAssignment>>();

        // Saved Mute parameter for each track (0/1) so the load-time mute
        // state is applied to targets before the song starts playing.
        public List<int> SavedMuteValues { get; set; } = new List<int>();
    }

    // =========================================================================
    // The machine
    // =========================================================================

    [MachineDecl(
        Name        = "Pedal Muter",
        ShortName   = "Muter",
        Author      = "WDE",
        MaxTracks   = PedalMuterMachine.MAX_TRACKS,
        InputCount  = 0,
        OutputCount = 0)]
    public class PedalMuterMachine : IBuzzMachine, INotifyPropertyChanged
    {
        public const int MAX_TRACKS = 64;

        IBuzzMachineHost host;
        readonly TrackState[] _tracks = new TrackState[MAX_TRACKS];
        bool _initialising = true;

        IBuzz Buzz => host?.Machine?.Graph?.Buzz;

        // =====================================================================
        // Constructor
        // =====================================================================

        public PedalMuterMachine(IBuzzMachineHost host)
        {
            this.host = host;
            for (int i = 0; i < MAX_TRACKS; i++)
                _tracks[i] = new TrackState();
        }

        public IBuzzMachineHost Host
        {
            get => host;
            set => host = value;
        }

        // =====================================================================
        // Track parameter: Mute
        //   int form, MaxValue=1 → ParameterType.Byte rendered as 00/01.
        //   Pattern-automatable; receives writes from peer control too.
        // =====================================================================

        [ParameterDecl(
            Name        = "Mute",
            Description = "Mute target machine(s) on this track (0=play, 1=mute)",
            MinValue    = 0,
            MaxValue    = 1,
            DefValue    = 0)]
        public void SetMute(int value, int track)
        {
            if ((uint)track >= MAX_TRACKS) return;
            if (_initialising)
            {
                _initialising = false;
                ResolveAllTargets();
            }

            bool newState = (value == 1);
            var ts = _tracks[track];
            if (ts.MuteState == newState) return;

            bool isMuting   = !ts.MuteState && newState;
            bool isUnmuting =  ts.MuteState && !newState;
            ts.MuteState = newState;

            if (isMuting)
            {
                // ──────────────────────────────────────────────────────────
                // Mute-time flush: send Note-Off WHILE the target is still
                // active, so its AudioTick this same buffer delivers the
                // release to the plugin.  Reduces (but doesn't eliminate)
                // the "stale voice on resume" problem — plugins with a long
                // release envelope will still have an unfinished release
                // frozen in their state when bypass kicks in.
                // ──────────────────────────────────────────────────────────
                FlushTargetsNoteOff(ts);
            }
            else if (isUnmuting)
            {
                // ──────────────────────────────────────────────────────────
                // Unmute-time flush: arm a counter that sends Note-Off on
                // every Work() pass for the next few buffers.  This handles
                // the cases the mute-time flush misses:
                //   - long-release voices that were frozen mid-release in
                //     the plugin's internal state during bypass
                //   - plugins where the standard pt_note Note-Off didn't
                //     reach the voice during the mute transition
                //
                // Spread across multiple buffers because the IsMuted=false
                // dispatch is async (BeginInvoke on the UI dispatcher);
                // the first few buffers after this setter call may still
                // be running with WorkMachine bypassed, so any single Work
                // pass might or might not actually be rendering.  By the
                // last of STALE_FLUSH_BUFFERS, the dispatch has definitely
                // landed and the plugin is rendering — that Note-Off is
                // guaranteed to take effect.
                // ──────────────────────────────────────────────────────────
                ts.PostUnmuteFlushBuffers = STALE_FLUSH_BUFFERS;
            }

            ApplyTrack(ts);
        }

        // Number of consecutive Work() calls after an unmute on which we
        // re-send Note-Off to the affected targets.
        //
        // At a typical 44.1 kHz / 512-sample buffer, 32 buffers ≈ 370 ms;
        // at 256 samples, ≈ 186 ms.  This covers two distinct sources of
        // need-to-keep-trying:
        //
        //   1. Async IsMuted=false dispatch latency — the BeginInvoke can
        //      take several buffers to land if the UI thread is busy, so
        //      the first few Work passes after this setter call may still
        //      be running with the target excluded from
        //      CollectMachinesThatCanWork.
        //
        //   2. Plugin-side voice state on long-release synths — if the
        //      voice was frozen mid-attack at mute time and Note-Off
        //      didn't get processed before bypass engaged, it'll still
        //      be in attack when Work resumes.  Successive Note-Offs
        //      across the flush window guarantee that at least one
        //      reaches the active voice once the plugin is rendering
        //      again.
        //
        // Audible cost is zero when no fresh notes are being delivered to
        // the target (the common case immediately after an unmute).
        // Increase further if very-long-release pads still leak through.
        const int STALE_FLUSH_BUFFERS = 32;

        void FlushTargetsNoteOff(TrackState ts)
        {
            var hostMachine = host?.Machine;
            for (int i = 0; i < ts.Assignments.Count; i++)
            {
                var a = ts.Assignments[i];
                var m = a.ResolvedMachine;
                if (m == null) continue;
                if (ReferenceEquals(m, hostMachine)) continue;
                FlushStaleVoicesOnMachine(m);
            }
        }

        // =====================================================================
        // State serialisation
        // =====================================================================

        public PedalMuterState MachineState
        {
            get
            {
                var st = new PedalMuterState();
                for (int t = 0; t < MAX_TRACKS; t++)
                {
                    st.TrackAssignments.Add(
                        new List<MuteAssignment>(_tracks[t].Assignments));
                    st.SavedMuteValues.Add(_tracks[t].MuteState ? 1 : 0);
                }
                return st;
            }
            set
            {
                if (value == null) return;
                for (int t = 0; t < MAX_TRACKS; t++)
                {
                    _tracks[t].Assignments.Clear();
                    if (t < value.TrackAssignments.Count
                        && value.TrackAssignments[t] != null)
                    {
                        _tracks[t].Assignments.AddRange(value.TrackAssignments[t]);
                    }
                    if (t < value.SavedMuteValues.Count && value.SavedMuteValues[t] >= 0)
                    {
                        _tracks[t].MuteState = (value.SavedMuteValues[t] == 1);
                    }
                }
                _initialising = false;
                ResolveAllTargets();
            }
        }

        // ReBuzz calls this after template import so machine renames are
        // applied to our string-based references.
        public void ImportFinished(IDictionary<string, string> nameMap)
        {
            foreach (var ts in _tracks)
                foreach (var a in ts.Assignments)
                    if (nameMap != null && nameMap.TryGetValue(a.MachineName, out var n))
                        a.MachineName = n;
            ResolveAllTargets();
        }

        // =====================================================================
        // Machine-graph resolution
        //   ALWAYS called on the UI thread (constructor, ImportFinished,
        //   MachineState setter, dialog operations).  NEVER from Work().
        // =====================================================================

        public void ResolveAssignment(MuteAssignment a)
        {
            a.ResolvedMachine = null;
            if (Buzz == null || string.IsNullOrEmpty(a.MachineName)) return;
            a.ResolvedMachine = Buzz.Song.Machines
                .FirstOrDefault(m => m.Name == a.MachineName);
        }

        public void ResolveAllTargets()
        {
            if (Buzz == null) return;
            foreach (var ts in _tracks)
                foreach (var a in ts.Assignments)
                    ResolveAssignment(a);
            // Re-apply current mute state to any newly resolved targets.
            foreach (var ts in _tracks)
                ApplyTrack(ts);
        }

        // =====================================================================
        // Mute application
        // =====================================================================

        void ApplyTrack(TrackState ts)
        {
            var hostMachine = host?.Machine;
            for (int i = 0; i < ts.Assignments.Count; i++)
            {
                var a = ts.Assignments[i];
                if (a.ResolvedMachine == null) continue;
                // Refuse to mute ourselves — that would freeze the controller.
                if (ReferenceEquals(a.ResolvedMachine, hostMachine)) continue;
                SetMachineMutedUiSafe(a.ResolvedMachine, ts.MuteState);
            }
        }

        /// <summary>
        /// Set IMachine.IsMuted on a foreign machine, marshalling to the UI
        /// thread when called from the audio thread.  Idempotent: short-
        /// circuits when target.IsMuted already matches, so it's cheap to
        /// call every Work() cycle.
        /// </summary>
        static void SetMachineMutedUiSafe(IMachine m, bool muted)
        {
            if (m == null) return;
            try { if (m.IsMuted == muted) return; } catch { return; }

            var d = System.Windows.Application.Current?.Dispatcher;
            if (d == null || d.CheckAccess())
            {
                try { m.IsMuted = muted; } catch { }
            }
            else
            {
                d.BeginInvoke(new Action(() =>
                {
                    try { if (m != null) m.IsMuted = muted; } catch { }
                }));
            }
        }

        // =====================================================================
        // Stale-voice flush
        //
        // Writes Buzz Note-Off (255) to every track of the target's note
        // parameter, then calls SCC so the target's TickAndWork does an
        // extra AudioTick(force) that picks up our 255 in addition to the
        // regular AudioTick.
        //
        // We don't gate on the current pvalue here.  An earlier version
        // tried "only write 255 if pvalue == NoValue" to preserve concurrent
        // notes — but IParameter.GetValue() doesn't reliably return NoValue
        // when there's no event this row (it tends to return the last
        // actually-played value), so the gate makes the flush a no-op.
        // The trade-off — a fresh note delivered on the same row as the
        // mute transition gets killed — is preferable to the flush silently
        // doing nothing.
        // =====================================================================

        static void FlushStaleVoicesOnMachine(IMachine target)
        {
            if (target == null) return;
            var noteParam = FindNoteParam(target);
            if (noteParam == null) return;

            // Scan a generous range of tracks.  Synths whose TrackCount
            // reports lower than the actual voice count can still hold
            // active voice state on higher-numbered tracks, especially
            // after PeerCtrl-style writers have hit them.
            int tc = 16;
            try { tc = Math.Max(tc, target.TrackCount); } catch { }

            for (int t = 0; t < tc; t++)
            {
                try { noteParam.SetValue(t, 255); }   // 255 = Buzz Note-Off
                catch { }
            }

            // SCC sets sendControlChangesFlag on the target.  When its
            // TickAndWork runs (in step 3b of the audio loop), it will
            // detect the flag and run an extra Tick(forceTick) that
            // re-reads pvalues — including our 255 — before WorkMachine.
            try { target.SendControlChanges(); } catch { }
        }

        // Multi-pass search for a Note-typed parameter, handling both
        // managed machines (explicit ParameterType.Note) and native
        // generators (Note is conventionally the first track parameter
        // in ParameterGroups[2]).
        static IParameter FindNoteParam(IMachine m)
        {
            if (m?.ParameterGroups == null) return null;

            // Pass 1: explicit Note type — set by managed machines that
            // declare a Note parameter via ParameterDecl, and reflected
            // by ReBuzz's native parameter wrapper for pt_note.
            foreach (var pg in m.ParameterGroups)
            {
                if (pg?.Parameters == null) continue;
                foreach (var p in pg.Parameters)
                {
                    try { if (p?.Type == ParameterType.Note) return p; }
                    catch { }
                }
            }

            // Pass 2: standard Buzz layout — group 2 is the track group,
            // and Note is conventionally its first parameter.
            int tgi = m.ParameterGroups.Count > 2
                    ? 2
                    : m.ParameterGroups.Count - 1;
            if (tgi >= 0)
            {
                var pg = m.ParameterGroups[tgi];
                var p  = pg?.Parameters?.FirstOrDefault(x => x != null);
                if (p != null) return p;
            }

            // Pass 3: any group flagged as Track type.
            foreach (var pg in m.ParameterGroups)
            {
                try
                {
                    if (pg?.Type != ParameterGroupType.Track) continue;
                }
                catch { continue; }
                var p = pg?.Parameters?.FirstOrDefault(x => x != null);
                if (p != null) return p;
            }

            return null;
        }

        // =====================================================================
        // Work() — control machine.  Identifies as IsControlMachine because
        // the signature is exactly `void Work()`.
        //
        // Re-asserts mute state every cycle as a safety net.  The setter
        // path handles all genuine state changes; this only catches drift
        // (another tool flipped IsMuted, target was just resolved, etc.).
        // SetMachineMutedUiSafe is idempotent so the steady-state cost is
        // a few comparisons per assignment per buffer.
        // =====================================================================

        public void Work()
        {
            if (_initialising)
            {
                _initialising = false;
                ResolveAllTargets();
            }

            for (int t = 0; t < MAX_TRACKS; t++)
            {
                var ts = _tracks[t];
                if (ts.Assignments.Count == 0) continue;
                ApplyTrack(ts);

                // Post-unmute Note-Off injection.  Counter is set to
                // STALE_FLUSH_BUFFERS by the SetMute setter on the
                // mute-to-unmute transition; we send Note-Off + SCC on
                // each Work pass while the counter is non-zero so the
                // burst of Note-Offs spans the IsMuted=false dispatch
                // latency and reaches the plugin once it's actually
                // rendering again.
                if (ts.PostUnmuteFlushBuffers > 0)
                {
                    ts.PostUnmuteFlushBuffers--;
                    FlushTargetsNoteOff(ts);
                }
            }
        }

        // =====================================================================
        // Context-menu commands
        // =====================================================================

        public IEnumerable<IMenuItem> Commands => new IMenuItem[]
        {
            new MenuEntry(0, "Assignments...",      OpenSettings),
            new MenuEntry(1, "Unmute All Targets",  UnmuteAllTargetsImmediate),
            new MenuEntry(2, "About...",            ShowAbout),
        };

        public void Command(int id)
        {
            switch (id)
            {
                case 0: OpenSettings();             break;
                case 1: UnmuteAllTargetsImmediate(); break;
                case 2: ShowAbout();                break;
            }
        }

        SettingsWindow _settingsWindow;

        void OpenSettings()
        {
            if (_settingsWindow != null)
            {
                try { _settingsWindow.Dispatcher.Invoke(() => _settingsWindow.Activate()); }
                catch { }
                return;
            }

            // Dedicated STA thread — avoids deadlock with multiple instances
            // all calling ShowDialog() through Application.Current.Dispatcher.
            var thread = new System.Threading.Thread(() =>
            {
                try
                {
                    _settingsWindow = new SettingsWindow(this);
                    _settingsWindow.Closed += (s, e) => _settingsWindow = null;
                    _settingsWindow.ShowDialog();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        $"{ex.GetType().Name}\n{ex.Message}\n\n{ex.StackTrace}",
                        "Pedal Muter",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
                finally
                {
                    _settingsWindow = null;
                }
            });
            thread.SetApartmentState(System.Threading.ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
        }

        // Hard-override: clear Mute on every track via the parameter system,
        // so the change is delivered identically to a pattern automation
        // (writes pvalues, fires SetMute, persists on save).
        void UnmuteAllTargetsImmediate()
        {
            for (int t = 0; t < MAX_TRACKS; t++)
            {
                if (_tracks[t].MuteState)
                    WritebackMuteParameter(t, 0);
            }
        }

        void WritebackMuteParameter(int track, int value)
        {
            try
            {
                var pg = host?.Machine?.ParameterGroups;
                if (pg == null || pg.Count <= 2) return;
                var trackGroup = pg[2];
                var muteParam  = trackGroup?.Parameters?.FirstOrDefault(
                    p => p?.Name == "Mute");
                muteParam?.SetValue(track, value);
            }
            catch { }
        }

        void ShowAbout()
        {
            MessageBox.Show(
                "Pedal Muter 1.0\n\n" +
                "Mutes any number of native or managed machines via pattern " +
                "automation or peer control. Each track maps to one or more " +
                "target machines; setting the track's Mute parameter to 1 " +
                "mutes them all by writing IMachine.IsMuted = true.\n\n" +
                "When ReBuzz's “Process muted machines” setting is off, " +
                "muting engages true zero-CPU bypass on the target — the " +
                "main use case is switching expensive synthesizers in and " +
                "out under peer control.\n\n" +
                "© WDE — ReBuzz managed control machine.",
                "Pedal Muter",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        // =====================================================================
        // Helpers exposed to the settings dialog
        // =====================================================================

        public TrackState GetTrack(int t) => (uint)t < MAX_TRACKS ? _tracks[t] : null;
        public IBuzz      BuzzHost        => Buzz;
        public IMachine   HostMachine     => host?.Machine;

        // =====================================================================
        // INotifyPropertyChanged
        // =====================================================================

        public event PropertyChangedEventHandler PropertyChanged;
        void N(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    // =========================================================================
    // MenuEntry — derives from DependencyObject so WPF TwoWay bindings on
    // IsChecked etc. work without TypeDescriptor/interface read-only issues.
    // =========================================================================

    public sealed class MenuEntry : DependencyObject, IMenuItem, INotifyPropertyChanged
    {
        public static readonly DependencyProperty IsCheckedProperty =
            DependencyProperty.Register(
                "IsChecked", typeof(bool), typeof(MenuEntry),
                new PropertyMetadata(false));

        public static readonly DependencyProperty IsEnabledProperty =
            DependencyProperty.Register(
                "IsEnabled", typeof(bool), typeof(MenuEntry),
                new PropertyMetadata(true));

        readonly Action _action;

        public MenuEntry(int id, string text, Action action)
        {
            ID      = id;
            Text    = text;
            _action = action;
        }

        public int    ID               { get; }
        public string Text             { get; }
        public string GestureText      => null;
        public object CommandParameter => null;

        public bool IsChecked
        {
            get => (bool)GetValue(IsCheckedProperty);
            set => SetValue(IsCheckedProperty, value);
        }
        public bool IsEnabled
        {
            get => (bool)GetValue(IsEnabledProperty);
            set => SetValue(IsEnabledProperty, value);
        }

        public bool IsCheckable      { get; set; } = false;
        public bool IsDefault        { get; set; } = false;
        public bool IsSeparator      { get; set; } = false;
        public bool IsLabel          { get; set; } = false;
        public bool StaysOpenOnClick { get; set; } = false;

        public IEnumerable<IMenuItem> Children => null;

        public ICommand Command => new RelayCommand(() => _action?.Invoke());

        public event PropertyChangedEventHandler PropertyChanged
        { add { } remove { } }
    }

    sealed class RelayCommand : ICommand
    {
        readonly Action _execute;
        public RelayCommand(Action execute) { _execute = execute; }
        public bool CanExecute(object parameter) => true;
        public void Execute(object parameter)    => _execute?.Invoke();
        public event EventHandler CanExecuteChanged { add { } remove { } }
    }
}
