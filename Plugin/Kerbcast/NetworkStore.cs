using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Kerbcast
{
    /*
     * The current save's tracking networks, kept in
     * saves/<game>/Kerbcast/networks.cfg rather than in the save itself, so a
     * revert or a quickload never takes them back. Applies viewers' saves and
     * deletes, runs each network's camera in this scene under its fixed
     * flightId, and publishes every network, mods' included, to the sidecar in
     * global.networks.json.
     */
    internal sealed class NetworkStore
    {
        private const string Tag = "[Kerbcast][NETWORK]";
        private const string HostIdPrefix = "host-";
        private const float JsonCheckSeconds = 1f;

        private readonly string _jsonPath;
        private readonly string _cfgPath;
        private SavedNetworkSet _set = new SavedNetworkSet();
        /* The file was there but could not be read: saving now would replace it, so saves are refused. */
        private bool _unreadable;
        private string _jsonWritten;
        private float _jsonCooldown;
        private readonly List<CommandCameraProvider.HostNetwork> _hosts = new List<CommandCameraProvider.HostNetwork>();

        public NetworkStore(string ringDir)
        {
            _jsonPath = Path.Combine(ringDir, SavedNetworkSet.JsonFileName);
            string folder = HighLogic.SaveFolder;
            _cfgPath = string.IsNullOrEmpty(folder) ? null
                : Path.Combine(KSPUtil.ApplicationRootPath, "saves", folder, "Kerbcast", SavedNetworkSet.FileName);
        }

        public void Load()
        {
            if (_cfgPath == null || !File.Exists(_cfgPath)) return;
            try
            {
                var skipped = new List<string>();
                _set = SavedNetworkSet.FromCfg(File.ReadAllText(_cfgPath), skipped);
                foreach (var why in skipped) Debug.LogWarning($"{Tag} {_cfgPath}: {why}; left out");
                Debug.Log($"{Tag} {_set.Networks.Count} saved network(s) loaded from {_cfgPath}");
            }
            catch (Exception ex)
            {
                _unreadable = true;
                Debug.LogWarning($"{Tag} {_cfgPath} could not be read ({ex.Message}); saved networks are off until it can");
            }
        }

        /* Every saved network's camera for this scene, before the first camera list is built. */
        public void StartCameras()
        {
            foreach (var n in _set.Networks) Start(n);
        }

        private void Start(SavedNetwork n)
        {
            uint id = KerbcastControl.StartSavedNetwork(n, out string error);
            if (id == 0) Debug.LogWarning($"{Tag} '{n.Name}' ({n.NetworkId}) not started: {error}");
        }

        /* A saved network's camera, which only a delete removes. */
        public bool IsSaved(uint flightId) => _set.FindByFlightId(flightId) != null;

        public void Apply(CommandCameraRequest r, KerbcastCore core)
        {
            SavedNetworkOutcome outcome;
            if (r.NetworkId.StartsWith(HostIdPrefix, StringComparison.Ordinal))
                outcome = SavedNetworkOutcome.Refused(r.Token, r.NetworkId, SavedNetworkCodes.NotOwner, "a mod made this network; only it can change it");
            else if (_unreadable || _cfgPath == null)
                outcome = SavedNetworkOutcome.Refused(r.Token, r.NetworkId, SavedNetworkCodes.WriteFailed, "the save's networks file can not be read");
            else if (r.Op == CommandRequestOp.DeleteNetwork)
            {
                outcome = _set.Delete(r, out var removed);
                if (removed != null)
                {
                    core.DestroyCommandCamera(removed.FlightId);
                    Debug.Log($"{Tag} '{removed.Name}' ({removed.NetworkId}) deleted by a viewer");
                }
            }
            else
            {
                outcome = _set.Save(r, NewId, out var before, out var saved);
                if (saved != null) Run(core, before, saved);
            }
            if (outcome.Ok && !Persist())
                outcome = SavedNetworkOutcome.Refused(r.Token, outcome.NetworkId, SavedNetworkCodes.WriteFailed,
                    "applied in this scene, but the networks file could not be written");
            if (!outcome.Ok) Debug.LogWarning($"{Tag} request {r.Id} refused: {outcome.Code}: {outcome.Text}");
            _set.Record(outcome);
            Publish(core, force: true);
        }

        /* A saved network's camera after an edit: in place, or rebuilt under the same flightId when its name changed. */
        private void Run(KerbcastCore core, SavedNetwork before, SavedNetwork saved)
        {
            if (before == null)
            {
                Debug.Log($"{Tag} '{saved.Name}' ({saved.NetworkId}) created by a viewer, cam={saved.FlightId}");
                Start(saved);
                return;
            }
            Debug.Log($"{Tag} '{saved.Name}' ({saved.NetworkId}) edited by a viewer, revision {saved.Revision}");
            if (SavedNetwork.NeedsRebuild(before, saved))
            {
                core.DestroyCommandCamera(saved.FlightId);
                Start(saved);
            }
            else KerbcastControl.UpdateSavedNetwork(saved, before.WatchPersistentId != saved.WatchPersistentId);
        }

        /*
         * A viewer pointed a camera: a saved network's watch follows a craft
         * aim or a return to the default (the active vessel). A path aim is
         * live only, and the lock owner's aims never come through here.
         */
        public void ViewerAimed(uint flightId, CommandAimSpec aim, KerbcastCore core)
        {
            if (!CameraId.IsNetworkId(flightId)) return;
            if (aim != null && aim.Kind != "vessel") return;
            if (!_set.SetWatch(flightId, aim?.PersistentId ?? 0)) return;
            Debug.Log($"{Tag} cam={flightId} retargeted by a viewer; its saved watch follows");
            Persist();
            Publish(core, force: true);
        }

        /* Once a second, and at once after a change: the file is rewritten only when it differs. */
        public void Tick(KerbcastCore core)
        {
            _jsonCooldown -= Time.unscaledDeltaTime;
            if (_jsonCooldown > 0f) return;
            Publish(core, force: false);
        }

        /* Leaving the scene: no network runs until the next one starts them. */
        public void Stop()
        {
            Write(_set.Json(_ => false, null));
        }

        private void Publish(KerbcastCore core, bool force)
        {
            _jsonCooldown = JsonCheckSeconds;
            var provider = core.CommandProvider;
            var hosts = new List<SavedNetwork>();
            if (provider != null)
            {
                provider.HostNetworks(_hosts);
                foreach (var h in _hosts) hosts.Add(HostDefinition(h));
            }
            string json = _set.Json(id => provider != null && provider.IsNetwork(id), hosts);
            if (!force && json == _jsonWritten) return;
            Write(json);
        }

        private static SavedNetwork HostDefinition(CommandCameraProvider.HostNetwork h)
        {
            var n = new SavedNetwork
            {
                NetworkId = HostIdPrefix + h.FlightId,
                Name = string.IsNullOrEmpty(h.Name) ? NetworkMath.DefaultName : h.Name,
                Owner = h.Owner.Length > 0 ? h.Owner : "a mod",
                HostFlightId = h.FlightId,
                MinHoldUt = h.Tuning.MinHoldUt,
                MinHoldRealSeconds = h.Tuning.MinHoldRealSeconds,
                MarginStops = h.Tuning.MarginStops,
            };
            if (KerbcastControl.Find(h.FlightId)?.Rig is CommandCameraRig gr && gr.Aim != null && gr.Aim.Kind == CommandAimKind.Vessel)
                n.WatchPersistentId = gr.Aim.PersistentId;
            if (h.Vantages != null)
                foreach (var v in h.Vantages)
                    n.Vantages.Add(new SavedVantage { Vantage = v.Vantage, Label = v.Label, Fallback = v.Fallback });
            return n;
        }

        private void Write(string json)
        {
            try
            {
                var tmp = _jsonPath + ".tmp";
                File.WriteAllText(tmp, json);
                if (File.Exists(_jsonPath)) File.Delete(_jsonPath);
                File.Move(tmp, _jsonPath);
                _jsonWritten = json;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{Tag} {_jsonPath} write failed: {ex.Message}");
            }
        }

        private bool Persist()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_cfgPath));
                var tmp = _cfgPath + ".tmp";
                File.WriteAllText(tmp, _set.ToCfg());
                if (File.Exists(_cfgPath)) File.Delete(_cfgPath);
                File.Move(tmp, _cfgPath);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{Tag} {_cfgPath} write failed: {ex.Message}");
                return false;
            }
        }

        private static string NewId() => "n" + Guid.NewGuid().ToString("N").Substring(0, 12);
    }
}
