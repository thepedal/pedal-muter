// Pedal Muter — Assignment Settings dialog, fully code-only (no XAML).
//
// Code-only avoids BAML-loading issues that hit WPF dialogs hosted inside
// a plugin-loaded assembly.  Without MIDI to configure, the per-assignment
// editor is just a target-machine combo, so the layout is much simpler
// than PeerCtrl's.

using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using BuzzGUI.Interfaces;

namespace WDE.PedalMuter
{
    public class SettingsWindow : Window
    {
        readonly PedalMuterMachine _machine;
        int  _selTrack      = 0;
        int  _selAssignment = -1;
        bool _suppress;

        // ── Controls ──────────────────────────────────────────────────────────
        ComboBox  _trackCombo;
        ListBox   _assignmentList;
        ComboBox  _machineCombo;
        TextBlock _statusText;

        // ── Brushes ───────────────────────────────────────────────────────────
        static readonly SolidColorBrush BgWindow  = MkBrush("#1E1E1E");
        static readonly SolidColorBrush BgInput   = MkBrush("#2D2D2D");
        static readonly SolidColorBrush BgList    = MkBrush("#252525");
        static readonly SolidColorBrush BgButton  = MkBrush("#3A3A3A");
        static readonly SolidColorBrush Border    = MkBrush("#555555");
        static readonly SolidColorBrush FgNormal  = MkBrush("#DDDDDD");
        static readonly SolidColorBrush FgMuted   = MkBrush("#888888");
        static readonly SolidColorBrush FgWarn    = MkBrush("#E89A30");

        static SolidColorBrush MkBrush(string hex)
        {
            hex = hex.TrimStart('#');
            byte r = Convert.ToByte(hex.Substring(0, 2), 16);
            byte g = Convert.ToByte(hex.Substring(2, 2), 16);
            byte b = Convert.ToByte(hex.Substring(4, 2), 16);
            var br = new SolidColorBrush(Color.FromRgb(r, g, b));
            br.Freeze();
            return br;
        }

        // =====================================================================
        // Construction
        // =====================================================================

        public SettingsWindow(PedalMuterMachine machine)
        {
            _machine = machine;

            Title                 = "Pedal Muter — Assignments";
            Width                 = 620;
            Height                = 460;
            MinWidth              = 480;
            MinHeight             = 360;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background            = BgWindow;
            Foreground            = FgNormal;

            BuildUI();

            PopulateTrackCombo();
            PopulateMachineCombo();

            _trackCombo.SelectedIndex = 0;
            _selTrack = 0;
            RefreshAssignmentList();

            // Live divergence poll: every 500 ms, compare each assignment's
            // MuteState to the target's actual IsMuted and flag mismatches
            // in the status line.  Real-time visibility into the exact bug
            // the user is hunting — "Pedal Muter says unmuted, target says
            // muted (or vice versa)".
            _statusTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(500),
            };
            _statusTimer.Tick += (_, _) => UpdateStatus();
            _statusTimer.Start();
            Closed += (_, _) => { try { _statusTimer.Stop(); } catch { } };
        }

        DispatcherTimer _statusTimer;

        // =====================================================================
        // Layout
        //
        //  ┌─ Track ─────────────┐  ┌─ Target machine ────────────┐
        //  │ [combo]             │  │ [combo]                     │
        //  ├─────────────────────┤  └─────────────────────────────┘
        //  │ → SynthA            │  ┌─ How it works ──────────────┐
        //  │ → SynthB [missing]  │  │ Help text...                │
        //  │                     │  │                             │
        //  │ [Add][Del][Clear]   │  └─────────────────────────────┘
        //  └─────────────────────┘
        //  Track 1: 1/2 resolved · MUTED                  [Close]
        // =====================================================================

        void BuildUI()
        {
            var root = new Grid { Margin = new Thickness(8) };
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var top = new Grid();
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(280) });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5) });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var left  = BuildLeftPane();
            var right = BuildRightPane();
            var split = new GridSplitter
            {
                Width = 5,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Background = Border,
            };

            Grid.SetColumn(left,  0); top.Children.Add(left);
            Grid.SetColumn(split, 1); top.Children.Add(split);
            Grid.SetColumn(right, 2); top.Children.Add(right);

            Grid.SetRow(top, 0);
            root.Children.Add(top);

            // Status + Close + Re-resolve
            var bottom = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
            _statusText = new TextBlock
            {
                Foreground        = FgMuted,
                VerticalAlignment = VerticalAlignment.Center,
                Margin            = new Thickness(4, 0, 0, 0),
            };
            DockPanel.SetDock(_statusText, Dock.Left);
            bottom.Children.Add(_statusText);

            var btnRow = new StackPanel
            {
                Orientation         = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            DockPanel.SetDock(btnRow, Dock.Right);

            var reResolveBtn = MkButton("Force Re-Resolve", (_, _) =>
            {
                // Fixes the stale-IMachine-pointer scenario where a target
                // was deleted-and-readded under the same name — our cached
                // pointer is to the orphan and writes don't reach the
                // current live machine.  Also a fast diagnostic: if this
                // fixes the bug, the bug was a stale reference.
                _machine.ResolveAllTargets();
                RefreshAssignmentList();
            });
            btnRow.Children.Add(reResolveBtn);

            var closeBtn = MkButton("Close", (_, _) => Close());
            btnRow.Children.Add(closeBtn);

            bottom.Children.Add(btnRow);

            Grid.SetRow(bottom, 1);
            root.Children.Add(bottom);

            Content = root;
        }

        UIElement BuildLeftPane()
        {
            var g = new Grid();
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Track selector
            var hdr = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
            hdr.Children.Add(MkLabel("Track:"));
            _trackCombo = MkCombo();
            _trackCombo.SelectionChanged += (s, e) =>
            {
                if (_suppress) return;
                _selTrack      = _trackCombo.SelectedIndex;
                _selAssignment = -1;
                RefreshAssignmentList();
            };
            hdr.Children.Add(_trackCombo);
            Grid.SetRow(hdr, 0); g.Children.Add(hdr);

            // Assignment list
            _assignmentList = new ListBox
            {
                Background  = BgList,
                Foreground  = FgNormal,
                BorderBrush = Border,
                Margin      = new Thickness(0, 0, 0, 4),
            };
            _assignmentList.SelectionChanged += (s, e) =>
            {
                if (_suppress) return;
                _selAssignment = _assignmentList.SelectedIndex;
                LoadEditor();
            };
            Grid.SetRow(_assignmentList, 1); g.Children.Add(_assignmentList);

            // Buttons
            var btns = new StackPanel { Orientation = Orientation.Horizontal };
            btns.Children.Add(MkButton("Add", (_, _) =>
            {
                _machine.GetTrack(_selTrack).Assignments.Add(new MuteAssignment());
                _selAssignment = _machine.GetTrack(_selTrack).Assignments.Count - 1;
                RefreshAssignmentList();
            }));
            btns.Children.Add(MkButton("Delete", (_, _) =>
            {
                var ts = _machine.GetTrack(_selTrack);
                if (_selAssignment < 0 || _selAssignment >= ts.Assignments.Count) return;
                // Unmute the orphaned target so the user doesn't have to
                // hunt it down by hand.
                var a = ts.Assignments[_selAssignment];
                if (a.ResolvedMachine != null && a.ResolvedMachine.IsMuted)
                {
                    try { a.ResolvedMachine.IsMuted = false; } catch { }
                }
                ts.Assignments.RemoveAt(_selAssignment);
                _selAssignment = Math.Min(_selAssignment, ts.Assignments.Count - 1);
                RefreshAssignmentList();
            }));
            btns.Children.Add(MkButton("Clear", (_, _) =>
            {
                var ts = _machine.GetTrack(_selTrack);
                foreach (var a in ts.Assignments)
                    if (a.ResolvedMachine != null && a.ResolvedMachine.IsMuted)
                        try { a.ResolvedMachine.IsMuted = false; } catch { }
                ts.Assignments.Clear();
                _selAssignment = -1;
                RefreshAssignmentList();
            }));
            Grid.SetRow(btns, 2); g.Children.Add(btns);

            return g;
        }

        UIElement BuildRightPane()
        {
            var sp = new StackPanel { Margin = new Thickness(8, 0, 0, 0) };

            // Target machine
            var targetGB   = MkGroupBox("Target machine");
            var targetGrid = new Grid { Margin = new Thickness(4) };
            targetGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
            targetGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            targetGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var lblM = MkLabel("Machine:");
            Grid.SetRow(lblM, 0); Grid.SetColumn(lblM, 0);
            targetGrid.Children.Add(lblM);

            _machineCombo = MkCombo();
            _machineCombo.SelectionChanged += (s, e) =>
            {
                if (_suppress) return;
                var a = CurrentAssignment();
                if (a == null) return;

                if (_machineCombo.SelectedIndex <= 0)
                {
                    a.MachineName = "";
                }
                else if (_machineCombo.SelectedItem is ComboBoxItem ci)
                {
                    a.MachineName = ci.Content?.ToString() ?? "";
                }
                _machine.ResolveAssignment(a);
                RefreshAssignmentList();
            };
            Grid.SetRow(_machineCombo, 0); Grid.SetColumn(_machineCombo, 1);
            targetGrid.Children.Add(_machineCombo);

            targetGB.Content = targetGrid;
            sp.Children.Add(targetGB);

            // Help text
            var helpGB = MkGroupBox("How it works");
            var help = new TextBlock
            {
                Margin       = new Thickness(8),
                Foreground   = FgMuted,
                TextWrapping = TextWrapping.Wrap,
                FontSize     = 11,
                Text =
                    "Each track on Pedal Muter has a Mute parameter (0 = play, 1 = mute). " +
                    "Setting it to 1 mutes every machine assigned to that track via " +
                    "IMachine.IsMuted; setting it to 0 unmutes them.\n\n" +
                    "Drive the Mute parameter from a PeerCtrl track (or directly via " +
                    "pattern automation). Add several assignments to the same track to " +
                    "mute a group of machines from one fader — e.g. fan one PeerCtrl " +
                    "slider out to mute every voice in a synth bus.\n\n" +
                    "When ReBuzz's “Process muted machines” setting is off, muting " +
                    "engages true zero-CPU bypass on the target. This is what makes " +
                    "Pedal Muter effective for switching expensive synthesizers in and " +
                    "out under peer control."
            };
            helpGB.Content = help;
            sp.Children.Add(helpGB);

            return sp;
        }

        // =====================================================================
        // Population
        // =====================================================================

        void PopulateTrackCombo()
        {
            _suppress = true;
            _trackCombo.Items.Clear();
            for (int t = 0; t < PedalMuterMachine.MAX_TRACKS; t++)
                _trackCombo.Items.Add(MkComboItem($"Track {t + 1}"));
            _suppress = false;
        }

        void PopulateMachineCombo()
        {
            _suppress = true;
            _machineCombo.Items.Clear();
            _machineCombo.Items.Add(MkComboItem("(none)"));
            try
            {
                var buzz  = _machine.BuzzHost;
                var hostM = _machine.HostMachine;
                if (buzz?.Song?.Machines != null)
                {
                    foreach (var m in buzz.Song.Machines)
                    {
                        if (m == null) continue;
                        if (ReferenceEquals(m, hostM)) continue; // can't mute self
                        _machineCombo.Items.Add(MkComboItem(m.Name));
                    }
                }
            }
            catch { }
            _suppress = false;
        }

        // =====================================================================
        // List + editor sync
        // =====================================================================

        MuteAssignment CurrentAssignment()
        {
            var ts = _machine.GetTrack(_selTrack);
            if (ts == null) return null;
            if (_selAssignment < 0 || _selAssignment >= ts.Assignments.Count)
                return null;
            return ts.Assignments[_selAssignment];
        }

        void RefreshAssignmentList()
        {
            _suppress = true;
            int prev = _selAssignment;
            _assignmentList.Items.Clear();

            var ts = _machine.GetTrack(_selTrack);
            for (int i = 0; i < ts.Assignments.Count; i++)
            {
                var a = ts.Assignments[i];
                string name   = string.IsNullOrEmpty(a.MachineName)
                              ? "(unassigned)"
                              : a.MachineName;
                string status = a.ResolvedMachine != null
                              ? ""
                              : (string.IsNullOrEmpty(a.MachineName) ? "" : "  [missing]");
                var item = new ListBoxItem
                {
                    Content    = $"→ {name}{status}",
                    Background = BgList,
                    Foreground = (a.ResolvedMachine == null && !string.IsNullOrEmpty(a.MachineName))
                                 ? FgWarn : FgNormal,
                };
                _assignmentList.Items.Add(item);
            }

            _selAssignment = ts.Assignments.Count == 0
                ? -1
                : Math.Min(prev < 0 ? 0 : prev, ts.Assignments.Count - 1);
            if (_selAssignment >= 0)
                _assignmentList.SelectedIndex = _selAssignment;

            _suppress = false;
            LoadEditor();
            UpdateStatus();
        }

        void LoadEditor()
        {
            _suppress = true;
            var a = CurrentAssignment();
            if (a == null)
            {
                _machineCombo.SelectedIndex = 0;
                _suppress = false;
                return;
            }

            // Find the assignment's machine in the combo (if present).
            int idx = 0;
            for (int i = 1; i < _machineCombo.Items.Count; i++)
            {
                if (_machineCombo.Items[i] is ComboBoxItem ci
                    && (ci.Content?.ToString() ?? "") == a.MachineName)
                {
                    idx = i;
                    break;
                }
            }
            _machineCombo.SelectedIndex = idx;
            _suppress = false;
        }

        void UpdateStatus()
        {
            var ts       = _machine.GetTrack(_selTrack);
            if (ts == null) return;
            int total    = ts.Assignments.Count;
            int resolved = ts.Assignments.Count(a => a.ResolvedMachine != null);
            string state = ts.MuteState ? "MUTED" : "playing";

            // Divergence check: any target whose actual IsMuted differs
            // from what we think it should be.  These are the smoking
            // guns for the "mute parameter triggered but not passed on"
            // bug — count them and list the first offender by name.
            int diverged   = 0;
            string firstBad = null;
            foreach (var a in ts.Assignments)
            {
                if (a.ResolvedMachine == null) continue;
                bool live;
                try { live = a.ResolvedMachine.IsMuted; }
                catch { continue; }
                if (live != ts.MuteState)
                {
                    diverged++;
                    if (firstBad == null) firstBad = a.MachineName;
                }
            }

            string divText = diverged == 0
                ? ""
                : $"  ⚠ {diverged} DIVERGED (first: {firstBad}={(ts.MuteState ? "should-be-muted" : "should-be-playing")})";
            _statusText.Foreground = diverged == 0 ? FgMuted : FgWarn;
            _statusText.Text =
                $"Track {_selTrack + 1}: {resolved}/{total} resolved · we say {state}.{divText}";
        }

        // =====================================================================
        // Style helpers
        // =====================================================================

        static TextBlock MkLabel(string text) => new TextBlock
        {
            Text              = text,
            Foreground        = FgNormal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(0, 2, 4, 2),
        };

        static ComboBox MkCombo() => new ComboBox
        {
            Background  = BgInput,
            Foreground  = FgNormal,
            BorderBrush = Border,
            Margin      = new Thickness(0, 2, 0, 2),
        };

        static ComboBoxItem MkComboItem(string text) => new ComboBoxItem
        {
            Content    = text,
            Background = BgInput,
            Foreground = FgNormal,
        };

        static Button MkButton(string label, RoutedEventHandler click)
        {
            var b = new Button
            {
                Content     = label,
                Background  = BgButton,
                Foreground  = FgNormal,
                BorderBrush = Border,
                Padding     = new Thickness(10, 3, 10, 3),
                Margin      = new Thickness(0, 2, 6, 2),
                MinWidth    = 64,
            };
            b.Click += click;
            return b;
        }

        static GroupBox MkGroupBox(string header) => new GroupBox
        {
            Header      = header,
            BorderBrush = Border,
            Foreground  = FgNormal,
            Margin      = new Thickness(0, 0, 0, 6),
        };
    }
}
