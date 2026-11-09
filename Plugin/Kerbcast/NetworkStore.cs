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
     * global.networks.json. A save or delete is all or nothing: if its camera
     * refuses or the file cannot be written, it is undone and answered with
     * the reason.
     */
    internal sealed class NetworkStore
    {
        private const string Tag = "[Kerbcast][NETWORK]";
        private const string HostIdPrefix = "host-";
        private const float JsonCheckSeconds = 1f;
        private const int TokensKept = 256;

        private static readonly SavedNetworkTokens Tokens = new SavedNetworkTokens(TokensKept);

        private readonly string _jsonPath;
        private readonly string _cfgPath;
        private SavedNetworkSet _set = new SavedNetworkSet();
        /* The file is there but could not be read: saving now would replace it, so saves are refused. */
        private bool _unreadable;
        private readonly SavedNetworkPublisher _publisher;
        private float _jsonCooldown;
        private readonly List<CommandCameraProvider.HostNetwork> _hosts = new List<CommandCameraProvider.HostNetwork>();

        public NetworkStore(string ringDir)
        {
            _jsonPath = Path.Combine(ringDir, SavedNetworkSet.JsonFileName);
            string folder = HighLogic.SaveFolder;
            _cfgPath = string.IsNullOrEmpty(folder) ? null
                : Path.Combine(KSPUtil.ApplicationRootPath, "saves", folder, "Kerbcast", SavedNetworkSet.FileName);
            _publisher = new SavedNetworkPublisher(Write);
            GameEvents.onVesselPersistentIdChanged.Add(OnMerged);
        }

        /* Leaving the scene: no request may change anything now. One that arrives is carried into the next scene. */
        public bool Stopped => _publisher.Stopped;

        public void Load()
        {
            if (_cfgPath == null) return;
            var log = new List<string>();
            try
            {
                _unreadable = SavedNetworkFile.Load(_cfgPath, log, out _set) == SavedNetworkFile.LoadResult.Unreadable;
            }
            catch (Exception ex)
            {
                _unreadable = true;
                log.Add($"{_cfgPath} could not be read ({ex.Message}); saved networks are read only until it can");
            }
            foreach (var line in log) Debug.LogWarning($"{Tag} {line}");
            if (!_unreadable && _set.Networks.Count > 0) Debug.Log($"{Tag} {_set.Networks.Count} saved network(s) loaded from {_cfgPath}");
        }

        /* Every saved network's camera for this scene, before the first camera list is built. One that fails is logged and skipped. */
        public void StartCameras()
        {
            foreach (var n in _set.Networks) Start(n);
        }

        /* `carried` restores a lock holder's aim and zoom instead of the saved watch. */
        private bool Start(SavedNetwork n, LiveAim carried = null)
        {
            try
            {
                uint id = KerbcastControl.StartSavedNetwork(n, out string error, carried);
                if (id != 0) return true;
                Debug.LogWarning($"{Tag} '{n.Name}' ({n.NetworkId}) not started: {error}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{Tag} '{n.Name}' ({n.NetworkId}) not started: {ex}");
            }
            return false;
        }

        /* A saved network's camera, which only a delete removes. */
        public bool IsSaved(uint flightId) => _set.FindByFlightId(flightId) != null;

        /* False when the token was applied earlier this game session, so a carried-over repeat changes nothing. */
        public static bool Claim(string token) => Tokens.Claim(token);

        public void Apply(CommandCameraRequest r, KerbcastCore core)
        {
            var outcome = r.Op == CommandRequestOp.DeleteNetwork ? Delete(r, core) : Save(r, core);
            if (!outcome.Ok) Debug.LogWarning($"{Tag} request {r.Id} refused: {outcome.Code}: {outcome.Text}");
            Tokens.Remember(outcome);
            _set.Record(outcome, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            Publish(core, force: true);
        }

        /* A token already applied changes nothing, but the client retrying it hears its answer again. */
        public void Replay(CommandCameraRequest r, KerbcastCore core)
        {
            if (!Tokens.TryLast(r.Token, out var last))
            {
                Debug.Log($"{Tag} request {r.Id}: token {r.Token} already claimed, with no answer to repeat");
                return;
            }
            Debug.Log($"{Tag} request {r.Id}: token {r.Token} already applied; answering again");
            _set.Record(last, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            Publish(core, force: true);
        }

        private SavedNetworkOutcome Refusal(CommandCameraRequest r)
        {
            if (r.NetworkId.StartsWith(HostIdPrefix, StringComparison.Ordinal))
                return SavedNetworkOutcome.Refused(r.Token, r.NetworkId, SavedNetworkCodes.NotOwner, "a mod made this network; only it can change it");
            if (_unreadable || _cfgPath == null)
                return SavedNetworkOutcome.Refused(r.Token, r.NetworkId, SavedNetworkCodes.WriteFailed, "the save's networks file can not be read");
            return default;
        }

        private SavedNetworkOutcome Save(CommandCameraRequest r, KerbcastCore core)
        {
            var refused = Refusal(r);
            if (refused.Code != null) return refused;
            /* What only the game can check (a surface vantage's body), before anything is stored. */
            if (r.RefusalCode == null && SavedNetworkSet.Check(r) == null)
            {
                var specs = new List<NetworkVantageSpec>(r.Vantages.Count);
                foreach (var v in r.Vantages) specs.Add(new NetworkVantageSpec { Vantage = v.Vantage, Label = v.Label, Fallback = v.Fallback });
                var probe = new SavedNetwork { MinHoldUt = r.MinHoldUt, MinHoldRealSeconds = r.MinHoldRealSeconds, MarginStops = r.MarginStops };
                string why = CommandCameraProvider.NetworkInvalid(specs, probe.Tuning);
                if (why != null) return SavedNetworkOutcome.Refused(r.Token, r.NetworkId, SavedNetworkCodes.BadVantage, why);
            }
            var outcome = _set.Save(r, NewId, out var before, out var saved);
            if (!outcome.Ok || saved == null) return outcome;
            /* Read before the edit touches the camera, so a revert can restore it too. */
            LiveAim live = before != null && core.CommandAuthority.Registered ? KerbcastControl.LiveAimOf(saved.FlightId) : null;
            if (!Run(core, before, saved, live))
            {
                Revert(core, before, saved, live);
                return SavedNetworkOutcome.Refused(r.Token, r.NetworkId, SavedNetworkCodes.NotRunning,
                    "the game could not start the network's camera; nothing changed");
            }
            if (!Persist())
            {
                Revert(core, before, saved, live);
                return SavedNetworkOutcome.Refused(r.Token, r.NetworkId, SavedNetworkCodes.WriteFailed,
                    "the networks file could not be written; nothing changed");
            }
            Debug.Log(before == null
                ? $"{Tag} '{saved.Name}' ({saved.NetworkId}) created by a viewer, cam={saved.FlightId}"
                : $"{Tag} '{saved.Name}' ({saved.NetworkId}) edited by a viewer, revision {saved.Revision}");
            return outcome;
        }

        private SavedNetworkOutcome Delete(CommandCameraRequest r, KerbcastCore core)
        {
            var refused = Refusal(r);
            if (refused.Code != null) return refused;
            var target = _set.Find(r.NetworkId);
            int at = target != null ? _set.IndexOf(target) : -1;
            var outcome = _set.Delete(r, out var removed);
            if (removed == null) return outcome;
            if (!Persist())
            {
                _set.Undo(null, null, removed, at);
                return SavedNetworkOutcome.Refused(r.Token, r.NetworkId, SavedNetworkCodes.WriteFailed,
                    "the networks file could not be written; nothing changed");
            }
            core.DestroyCommandCamera(removed.FlightId);
            Debug.Log($"{Tag} '{removed.Name}' ({removed.NetworkId}) deleted by a viewer");
            return outcome;
        }

        /* A saved network's camera after a save: started, changed in place, or rebuilt under the same flightId when its name changed. */
        private bool Run(KerbcastCore core, SavedNetwork before, SavedNetwork saved, LiveAim live)
        {
            if (before == null) return Start(saved);
            var plan = SavedNetwork.PlanEdit(before, saved, core.CommandProvider.IsNetwork(saved.FlightId), core.CommandAuthority.Registered);
            if (plan.Rebuild)
            {
                core.DestroyCommandCamera(saved.FlightId);
                return Start(saved, plan.CarryLiveAim ? live : null);
            }
            return KerbcastControl.UpdateSavedNetwork(saved, plan.ApplyWatch);
        }

        /* Puts a save back as it was, camera included. */
        private void Revert(KerbcastCore core, SavedNetwork before, SavedNetwork saved, LiveAim live)
        {
            _set.Undo(before, saved);
            core.DestroyCommandCamera(saved.FlightId);
            if (before != null) Start(saved, live);
        }

        /* The lock changed and every aim was dropped: a saved network goes back to the craft it is saved to watch. */
        public void ReapplyWatches()
        {
            if (Stopped) return;
            foreach (var n in _set.Watching()) KerbcastControl.ApplySavedWatch(n);
        }

        /* The live camera already rides the survivor; this keeps the saved ids from naming a craft that no longer exists. */
        private void OnMerged(uint oldPid, uint newPid)
        {
            if (Stopped || !_set.FollowMerge(oldPid, newPid)) return;
            Debug.Log($"{Tag} craft {oldPid} merged into {newPid}; saved networks follow");
            /* A failed write leaves the new ids in memory for the next save: the old id is dead, so there is nothing to go back to. */
            Persist();
            _jsonCooldown = 0f;
        }

        /*
         * A viewer pointed a camera: a saved network's watch follows a craft
         * aim or a return to the default (the active vessel). A path aim is
         * live only, and the lock owner's aims never come through here.
         */
        public void ViewerAimed(uint flightId, CommandAimSpec aim, string token, KerbcastCore core)
        {
            if (Stopped || !CameraId.IsNetworkId(flightId) || _unreadable) return;
            if (aim != null && aim.Kind != "vessel") return;
            if (!_set.SetWatch(flightId, aim?.PersistentId ?? 0, out var before, out var saved)) return;
            if (Persist()) Debug.Log($"{Tag} cam={flightId} retargeted by a viewer; its saved watch follows");
            else
            {
                _set.Undo(before, saved);
                _set.Record(SavedNetworkOutcome.Refused(token, saved.NetworkId, SavedNetworkCodes.WriteFailed,
                    "the networks file could not be written; the saved watch is unchanged"), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            }
            Publish(core, force: true);
        }

        /* Once a second, and at once after a change: the file is rewritten only when it differs. */
        public void Tick(KerbcastCore core)
        {
            if (Stopped) return;
            _jsonCooldown -= Time.unscaledDeltaTime;
            if (_jsonCooldown > 0f) return;
            Publish(core, force: false);
        }

        /* Leaving the scene: nothing runs and nothing can be saved until the next one starts. */
        public void Stop()
        {
            if (Stopped) return;
            GameEvents.onVesselPersistentIdChanged.Remove(OnMerged);
            _publisher.Stop(_set.Json(_ => false, null, editable: false));
        }

        private void Publish(KerbcastCore core, bool force)
        {
            if (Stopped) return;
            _jsonCooldown = JsonCheckSeconds;
            var provider = core.CommandProvider;
            var hosts = new List<SavedNetwork>();
            if (provider != null)
            {
                provider.HostNetworks(_hosts);
                foreach (var h in _hosts) hosts.Add(HostDefinition(h));
            }
            string json = _set.Json(id => provider != null && provider.IsNetwork(id), hosts, editable: !_unreadable && _cfgPath != null);
            _publisher.Publish(json, force);
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

        private bool Write(string json)
        {
            try
            {
                var tmp = _jsonPath + ".tmp";
                File.WriteAllText(tmp, json);
                if (File.Exists(_jsonPath)) File.Delete(_jsonPath);
                File.Move(tmp, _jsonPath);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{Tag} {_jsonPath} write failed: {ex.Message}");
                return false;
            }
        }

        private bool Persist()
        {
            try
            {
                SavedNetworkFile.Save(_cfgPath, _set.ToCfg());
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
