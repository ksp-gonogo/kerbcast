using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace Kerbcast
{
    /*
     * The process's lock reservation, kept as a Reservation node in the
     * user's GameData/Kerbcast/PluginData/settings.cfg, beside the Settings
     * node, so it outlives scenes and restarts. A player clears it by
     * deleting that node. The light-time history samples while one is held.
     *
     * Only the Reservation node's text is replaced, so the player's own
     * settings and comments stay as written. The file is written to a temp
     * file first and then moved into place. A file that does not parse is
     * kept aside as settings.cfg.bad and read as no reservation; the owner
     * reserves again when it starts.
     */
    internal static class LockReservationStore
    {
        private const string NodeName = "Reservation";

        private static LockReservation _reservation;
        private static bool _lapseChecked;
        private static bool _saveFailureLogged;

        private static string FilePath => Path.Combine(KSPUtil.ApplicationRootPath, "GameData", "Kerbcast", "PluginData", "settings.cfg");

        public static LockReservation Reservation
        {
            get
            {
                if (_reservation != null) return _reservation;
                _reservation = new LockReservation();
                Load(_reservation);
                _reservation.Changed = Saved;
                LightHistoryHost.History.SetReservationHeld(_reservation.Held);
                return _reservation;
            }
        }

        /* Once per process, at the first scene: a reservation whose assembly is not loaded lapses. */
        public static void CheckLapse()
        {
            if (_lapseChecked) return;
            _lapseChecked = true;
            var r = Reservation;
            string owner = r.Owner, assembly = r.Assembly;
            if (r.LapseUnless(AssemblyLoaded))
                Debug.LogWarning($"[Kerbcast][CMDCAM] reservation for {owner} lapsed: assembly '{assembly}' is not loaded");
        }

        /* Matches KSP's name for the file (without .dll) or the assembly's own
           name, which Reserve checks the caller by; an empty name is never loaded. */
        public static bool AssemblyLoaded(string assembly)
        {
            if (string.IsNullOrEmpty(assembly)) return false;
            string bare = Api.ReservedAssemblies.Bare(assembly);
            foreach (var a in AssemblyLoader.loadedAssemblies)
            {
                if (a == null) continue;
                if (string.Equals(a.name, bare, StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(a.assembly?.GetName().Name, bare, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static void Saved()
        {
            var r = _reservation;
            LightHistoryHost.History.SetReservationHeld(r.Held);
            try
            {
                string text = File.Exists(FilePath) ? File.ReadAllText(FilePath) : "";
                if (text.Length > 0 && ConfigNode.Parse(text) == null)
                {
                    KeepAside("it does not parse");
                    text = "";
                }
                var sb = new StringBuilder(SettingsLayer.WithoutNode(text, NodeName).TrimEnd());
                if (r.Held)
                {
                    /* TakeLock and Reserve already refuse such text; never write a node something could break out of. */
                    string node = SettingsLayer.Node(NodeName,
                        new[] { "Kerbcast.API lock reservation: every scene starts locked for this owner while its", "assembly is loaded. Delete this node to clear it." },
                        "owner", r.Owner, "assembly", r.Assembly, "silentText", string.IsNullOrEmpty(r.SilentText) ? null : r.SilentText);
                    if (node == null)
                    {
                        Debug.LogError("[Kerbcast][CMDCAM] lock reservation not saved: its owner, assembly or silence text could break out of settings.cfg");
                        return;
                    }
                    if (sb.Length > 0) sb.Append("\n\n");
                    sb.Append(node);
                }
                else if (sb.Length > 0) sb.Append('\n');
                WriteAtomically(sb.ToString());
                _saveFailureLogged = false;
                Debug.Log(r.Held
                    ? $"[Kerbcast][CMDCAM] lock reserved for {r.Owner} while '{r.Assembly}' is loaded"
                    : "[Kerbcast][CMDCAM] lock reservation cleared");
            }
            catch (Exception ex)
            {
                /* Every lock change saves, so a failure that persists is logged once until a save succeeds. */
                if (!_saveFailureLogged) Debug.LogWarning($"[Kerbcast][CMDCAM] lock reservation not saved: {ex.Message}");
                _saveFailureLogged = true;
            }
        }

        private static void Load(LockReservation r)
        {
            try
            {
                if (!File.Exists(FilePath)) return;
                var root = ConfigNode.Parse(File.ReadAllText(FilePath));
                if (root == null)
                {
                    KeepAside("it does not parse");
                    return;
                }
                var node = root.GetNode(NodeName);
                if (node != null) r.Load(node.GetValue("owner"), node.GetValue("assembly"), node.GetValue("silentText"));
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Kerbcast][CMDCAM] lock reservation not read, so there is none: {ex.Message}");
            }
        }

        /* A copy of the file as it was, for the player, with the reason logged loudly. */
        private static void KeepAside(string why)
        {
            try { File.Copy(FilePath, FilePath + ".bad", true); }
            catch (Exception) { /* the log line below still says what happened */ }
            Debug.LogError($"[Kerbcast][CMDCAM] {FilePath}: {why}; kept as settings.cfg.bad and read as no reservation");
        }

        private static void WriteAtomically(string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            string temp = FilePath + ".tmp";
            File.WriteAllText(temp, text);
            if (File.Exists(FilePath)) File.Replace(temp, FilePath, null);
            else File.Move(temp, FilePath);
        }
    }
}
