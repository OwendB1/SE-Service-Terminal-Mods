using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Sandbox.Definitions;
using Sandbox.ModAPI;
using VRage;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRage.ObjectBuilders;
using VRage.Utils;
using VRageMath;

namespace ShipInsurance
{
    [MySessionComponentDescriptor(MyUpdateOrder.AfterSimulation)]
    public sealed partial class InsuranceSession : MySessionComponentBase
    {
        private const ushort NetworkChannel = 49171;
        private const string ConfigFile = "ShipInsuranceConfig.xml";
        private const string StateFile = "ShipInsuranceState.bin64";
        private const string CommandPrefix = "/insurance";
        private const int CommandPacket = 1;
        private const int ResponsePacket = 2;
        private const int PolicyListRequestPacket = 3;
        private const int PolicyListResponsePacket = 4;

        private readonly Dictionary<long, IMyCubeGrid> _subscribedGrids = new Dictionary<long, IMyCubeGrid>();
        private readonly Dictionary<long, Dictionary<Vector3I, DamageAttribution>> _lastDamage = new Dictionary<long, Dictionary<Vector3I, DamageAttribution>>();
        private readonly Dictionary<long, string> _identityNames = new Dictionary<long, string>();
        private readonly Dictionary<MyDefinitionId, long> _blockValues = new Dictionary<MyDefinitionId, long>();
        private readonly HashSet<long> _repairingGrids = new HashSet<long>();

        private InsuranceConfig _config = new InsuranceConfig();
        private InsuranceState _state = new InsuranceState();
        private bool _active;
        private bool _isServer;
        private bool _networkRegistered;
        private bool _chatRegistered;
        private bool _dirty;
        private int _frame;

        public override void BeforeStart()
        {
            _active = true;
            _isServer = MyAPIGateway.Multiplayer.IsServer;

#if DEBUG
            InsuranceMath.SelfTest();
#endif

            MyAPIGateway.Multiplayer.RegisterSecureMessageHandler(NetworkChannel, OnNetworkMessage);
            _networkRegistered = true;

            if (!MyAPIGateway.Utilities.IsDedicated)
            {
                MyAPIGateway.Utilities.MessageEnteredSender += OnChatMessage;
                _chatRegistered = true;
                RegisterServiceTerminalControls();
            }

            if (_isServer)
            {
                LoadConfig();
                LoadState();
                MyAPIGateway.Session.DamageSystem.RegisterAfterDamageHandler(1000, OnDamageApplied);
                RefreshGridSubscriptions();
            }
        }

        public override void UpdateAfterSimulation()
        {
            if (!_active || !_isServer) return;

            _frame++;
            if (_frame % 100 == 0)
            {
                UpdateLastKnownPoses();
                RefreshGridSubscriptions();
            }

            if (_dirty && _frame % 3600 == 0)
                SaveState();
        }

        public override void SaveData()
        {
            if (_isServer) SaveState();
        }

        protected override void UnloadData()
        {
            _active = false;

            if (_chatRegistered && MyAPIGateway.Utilities != null)
                MyAPIGateway.Utilities.MessageEnteredSender -= OnChatMessage;

            UnregisterServiceTerminalControls();

            if (_networkRegistered && MyAPIGateway.Multiplayer != null)
                MyAPIGateway.Multiplayer.UnregisterSecureMessageHandler(NetworkChannel, OnNetworkMessage);

            foreach (IMyCubeGrid grid in _subscribedGrids.Values)
                UnhookGrid(grid);

            _subscribedGrids.Clear();
            _lastDamage.Clear();
            _identityNames.Clear();
            _blockValues.Clear();
            _repairingGrids.Clear();
            base.UnloadData();
        }

        private void OnChatMessage(ulong sender, string text, ref bool sendToOthers)
        {
            if (string.IsNullOrWhiteSpace(text) || !IsInsuranceCommand(text)) return;

            sendToOthers = false;
            string command = text.Substring(CommandPrefix.Length).Trim();
            string[] commandParts = command.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            string action = commandParts.Length == 0 ? string.Empty : commandParts[0];
            if (!string.Equals(action, "reload", StringComparison.OrdinalIgnoreCase))
            {
                ShowClientText("Use a Services Terminal's Ship Insurance controls. Chat supports only /insurance reload.");
                return;
            }

            NetworkPacket packet = new NetworkPacket
            {
                Kind = CommandPacket,
                Command = command,
                ControlledGridId = 0
            };

            try
            {
                if (_isServer)
                    HandleCommand(sender, packet);
                else
                    MyAPIGateway.Multiplayer.SendMessageToServer(NetworkChannel, MyAPIGateway.Utilities.SerializeToBinary(packet), true);
            }
            catch (Exception exception)
            {
                ShowClientText("Command failed: " + exception.Message);
                Log("Chat command failed", exception);
            }
        }

        private static bool IsInsuranceCommand(string text)
        {
            if (!text.StartsWith(CommandPrefix, StringComparison.OrdinalIgnoreCase)) return false;
            return text.Length == CommandPrefix.Length || char.IsWhiteSpace(text[CommandPrefix.Length]);
        }

        private void OnNetworkMessage(ushort channel, byte[] data, ulong sender, bool fromServer)
        {
            if (!_active || data == null) return;

            try
            {
                NetworkPacket packet = MyAPIGateway.Utilities.SerializeFromBinary<NetworkPacket>(data);
                if (packet == null) return;

                if (_isServer && !fromServer)
                {
                    if (packet.Kind == CommandPacket) HandleCommand(sender, packet);
                    else if (packet.Kind == PolicyListRequestPacket)
                        HandlePolicyListRequest(sender, packet.ServiceTerminalId);
                }
                else if (!_isServer && fromServer)
                {
                    if (packet.Kind == ResponsePacket) ShowClientText(packet.Text);
                    else if (packet.Kind == PolicyListResponsePacket) ApplyServicePolicyList(packet);
                }
            }
            catch (Exception exception)
            {
                Log("Network packet failed", exception);
            }
        }

        private void HandleCommand(ulong steamId, NetworkPacket packet)
        {
            IMyPlayer player = FindPlayer(steamId);
            if (player == null)
            {
                SendResponse(steamId, "Player identity unavailable.");
                return;
            }

            string[] parts = (packet.Command ?? string.Empty).Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            string action = parts.Length == 0 ? "help" : parts[0].ToLowerInvariant();
            long requestedPolicyId = ReadPolicyId(parts);

            IMyCubeGrid serviceGrid = null;
            if (packet.ServiceTerminalId != 0)
            {
                if (!IsServiceTerminalAction(action))
                {
                    SendResponse(steamId, "That action is not available from a service terminal.");
                    return;
                }

                string serviceError;
                bool requireGrid = action == "insure";
                if (!TryValidateServiceTerminalRequest(player, packet.ServiceTerminalId, packet.ControlledGridId,
                    requireGrid, out serviceGrid, out serviceError))
                {
                    SendResponse(steamId, serviceError);
                    return;
                }
            }
            else if (action != "reload")
            {
                SendResponse(steamId, "Use a Services Terminal's Ship Insurance controls. Chat supports only /insurance reload.");
                return;
            }

            try
            {
                switch (action)
                {
                    case "insure":
                        InsureGrid(player, steamId, packet.ControlledGridId, serviceGrid);
                        break;
                    case "status":
                        ShowStatus(player, steamId, packet.ControlledGridId, requestedPolicyId,
                            packet.ServiceTerminalId);
                        break;
                    case "claim":
                        Claim(player, steamId, packet.ControlledGridId, requestedPolicyId, packet.ServiceTerminalId);
                        break;
                    case "expedite":
                        ExpediteRecovery(player, steamId, requestedPolicyId, packet.ServiceTerminalId);
                        break;
                    case "history":
                        ShowHistory(player, steamId, packet.ControlledGridId, requestedPolicyId);
                        break;
                    case "cancel":
                        CancelPolicy(player, steamId, packet.ControlledGridId, requestedPolicyId);
                        break;
                    case "reload":
                        ReloadConfig(player, steamId);
                        break;
                    default:
                        SendResponse(steamId, "Unknown insurance command.");
                        break;
                }
            }
            catch (Exception exception)
            {
                SendResponse(steamId, "Command failed: " + exception.Message);
                Log("Server command failed for " + steamId, exception);
            }

            if (packet.ServiceTerminalId != 0)
                HandlePolicyListRequest(steamId, packet.ServiceTerminalId);
        }

        private void HandlePolicyListRequest(ulong steamId, long serviceTerminalId)
        {
            IMyPlayer player = FindPlayer(steamId);
            if (player == null) return;

            IMyCubeGrid ignoredGrid;
            string error;
            if (!TryValidateServiceTerminalRequest(player, serviceTerminalId, 0, false,
                out ignoredGrid, out error))
            {
                SendResponse(steamId, error);
                return;
            }

            IMyFunctionalBlock terminal = FindServiceTerminal(serviceTerminalId);
            NetworkPacket response = new NetworkPacket
            {
                Kind = PolicyListResponsePacket,
                ServiceTerminalId = serviceTerminalId,
                Policies = new List<PolicySummary>()
            };

            for (int i = 0; i < _state.Policies.Count; i++)
            {
                InsurancePolicy policy = _state.Policies[i];
                if (policy.OwnerIdentityId != player.IdentityId) continue;
                long selectionGridId = 0;
                for (int gridIndex = 0; gridIndex < policy.Grids.Count; gridIndex++)
                {
                    IMyCubeGrid grid = FindGrid(policy.Grids[gridIndex].GridEntityId);
                    if (grid == null) continue;
                    selectionGridId = grid.EntityId;
                    break;
                }

                long nowUtcTicks = DateTime.UtcNow.Ticks;
                long remainingSeconds = InsuranceMath.RemainingSeconds(policy.RecoveryReadyUtcTicks, nowUtcTicks);
                bool canExpedite = policy.RecoveryTerminalEntityId == serviceTerminalId &&
                                   remainingSeconds > 0 && !policy.RecoveryExpedited;
                response.Policies.Add(new PolicySummary
                {
                    PolicyId = policy.PolicyId,
                    GridName = policy.GridName,
                    SelectionGridId = selectionGridId,
                    TotalLoss = selectionGridId == 0,
                    Remote = policy.RecoveryReadyUtcTicks > 0 || selectionGridId == 0 ||
                             !IsPolicyWithinServiceRange(policy, terminal),
                    RecoveryReadyUtcTicks = policy.RecoveryReadyUtcTicks,
                    RecoveryTerminalEntityId = policy.RecoveryTerminalEntityId,
                    RecoveryDistanceMeters = policy.RecoveryDistanceMeters,
                    ExpeditePrice = canExpedite
                        ? InsuranceMath.ExpediteFee(remainingSeconds,
                            _config.RemoteRecoveryExpediteCostPerSecond)
                        : 0,
                    ExpediteReductionPercent = (int)Math.Round(
                        (1.0 - InsuranceMath.Clamp01(_config.RemoteRecoveryExpediteFactor)) * 100.0),
                    RecoveryExpedited = policy.RecoveryExpedited
                });
            }

            if (!MyAPIGateway.Utilities.IsDedicated && steamId == MyAPIGateway.Multiplayer.MyId)
                MyAPIGateway.Utilities.InvokeOnGameThread(delegate { ApplyServicePolicyList(response); });
            else
                MyAPIGateway.Multiplayer.SendMessageTo(NetworkChannel,
                    MyAPIGateway.Utilities.SerializeToBinary(response), steamId, true);
        }

        private static long ReadPolicyId(string[] parts)
        {
            long value;
            return parts.Length > 1 && long.TryParse(parts[1], out value) ? value : 0;
        }

        private static bool IsServiceTerminalAction(string action)
        {
            return action == "insure" || action == "status" || action == "claim" ||
                   action == "expedite" || action == "history" || action == "cancel";
        }

        private bool TryValidateServiceTerminalRequest(IMyPlayer player, long terminalId, long targetGridId,
            bool requireGrid, out IMyCubeGrid grid, out string error)
        {
            grid = null;
            error = null;

            IMyEntity entity;
            IMyFunctionalBlock terminal;
            if (!MyAPIGateway.Entities.TryGetEntityById(terminalId, out entity) ||
                (terminal = entity as IMyFunctionalBlock) == null || terminal.Closed ||
                !IsServicesTerminal(terminal) ||
                !terminal.IsFunctional || !terminal.Enabled)
            {
                error = "Insurance service terminal is unavailable.";
                return false;
            }

            if (terminal.GetUserRelationToOwner(player.IdentityId) == MyRelationsBetweenPlayerAndBlock.Enemies)
            {
                error = "Insurance service terminal access denied.";
                return false;
            }

            double useDistance = ServiceTerminalUseDistance;
            if (Vector3D.DistanceSquared(player.GetPosition(), terminal.GetPosition()) > useDistance * useDistance)
            {
                error = "Move within " + useDistance.ToString("0", CultureInfo.InvariantCulture) +
                        " m of insurance service terminal.";
                return false;
            }

            if (!requireGrid) return true;

            grid = FindGrid(targetGridId);
            if (grid == null)
            {
                error = "Selected grid is no longer available.";
                return false;
            }

            double gridRange = ServiceGridDiscoveryRange + grid.WorldAABB.HalfExtents.Length();
            if (Vector3D.DistanceSquared(grid.WorldAABB.Center, terminal.GetPosition()) > gridRange * gridRange)
            {
                grid = null;
                error = "Selected grid is outside service range.";
                return false;
            }

            if (_config.RequireGridOwner && !grid.BigOwners.Contains(player.IdentityId))
            {
                grid = null;
                error = "You must be a major owner of selected group anchor.";
                return false;
            }

            return true;
        }

        private void InsureGrid(IMyPlayer player, ulong steamId, long clientGridId, IMyCubeGrid serviceGrid)
        {
            IMyCubeGrid grid = serviceGrid;
            if (grid == null || grid.EntityId != clientGridId)
            {
                SendResponse(steamId, "Select a nearby mechanical group at service terminal to insure it.");
                return;
            }

            List<IMyCubeGrid> group = GetMechanicalGroup(grid);
            IMyCubeGrid anchor = group[0];
            for (int i = 0; i < group.Count; i++)
            {
                if (_config.RequireGridOwner && !group[i].BigOwners.Contains(player.IdentityId))
                {
                    SendResponse(steamId, "You must be a major owner of every grid in mechanical group.");
                    return;
                }

                if (FindPolicyByGrid(group[i].EntityId) != null)
                {
                    SendResponse(steamId, "Mechanical group already contains an insured grid.");
                    return;
                }
            }

            int policyCount = 0;
            for (int i = 0; i < _state.Policies.Count; i++)
                if (_state.Policies[i].OwnerIdentityId == player.IdentityId) policyCount++;

            if (policyCount >= _config.MaxPoliciesPerPlayer)
            {
                SendResponse(steamId, "Policy limit reached (" + _config.MaxPoliciesPerPlayer + ").");
                return;
            }

            List<InsuredGridSnapshot> snapshots = new List<InsuredGridSnapshot>(group.Count);
            MatrixD anchorInverse = MatrixD.Invert(anchor.WorldMatrix);
            long baselineValue = 0;
            int blockCount = 0;
            double clearanceRadius = 5.0;
            for (int i = 0; i < group.Count; i++)
            {
                IMyCubeGrid member = group[i];
                MyObjectBuilder_CubeGrid blueprint = CreateSanitizedBlueprint(member);
                if (blueprint == null || blueprint.CubeBlocks == null || blueprint.CubeBlocks.Count == 0)
                {
                    SendResponse(steamId, "Could not snapshot every grid in mechanical group.");
                    return;
                }

                snapshots.Add(new InsuredGridSnapshot
                {
                    GridEntityId = member.EntityId,
                    Blueprint = blueprint,
                    RelativePose = new MyPositionAndOrientation(member.WorldMatrix * anchorInverse)
                });
                baselineValue = InsuranceMath.Add(baselineValue, CalculateBaselineValue(blueprint));
                blockCount += blueprint.CubeBlocks.Count;
                clearanceRadius = Math.Max(clearanceRadius,
                    Vector3D.Distance(anchor.WorldAABB.Center, member.WorldAABB.Center) +
                    member.WorldAABB.HalfExtents.Length() + _config.TotalLossExtraClearance);
            }

            long fee = InsuranceMath.EnrollmentFee(baselineValue, _config.EnrollmentFlatFee, _config.EnrollmentValueFraction);
            long balance;
            if (!player.TryGetBalanceInfo(out balance) || balance < fee)
            {
                SendResponse(steamId, "Enrollment costs " + Money(fee) + " SC; balance " + Money(balance) + " SC.");
                return;
            }

            if (fee > 0) player.RequestChangeBalance(-fee);

            InsurancePolicy policy = new InsurancePolicy
            {
                PolicyId = _state.NextPolicyId++,
                GridEntityId = anchor.EntityId,
                OwnerIdentityId = player.IdentityId,
                OwnerSteamId = steamId,
                GridName = string.IsNullOrWhiteSpace(anchor.CustomName) ? anchor.DisplayName : anchor.CustomName,
                CreatedUtcTicks = DateTime.UtcNow.Ticks,
                BaselineValue = baselineValue,
                LastKnownPose = new MyPositionAndOrientation(anchor.WorldMatrix),
                ClearanceRadius = clearanceRadius,
                Grids = snapshots
            };

            _state.Policies.Add(policy);
            for (int i = 0; i < group.Count; i++) SubscribeGrid(group[i]);
            AddIncident(policy, new IncidentRecord
            {
                UtcTicks = DateTime.UtcNow.Ticks,
                EventType = "PolicyCreated",
                AttackerIdentityId = player.IdentityId,
                AttackerName = player.DisplayName
            });
            _dirty = true;
            SaveState();

            SendResponse(steamId, "Policy #" + policy.PolicyId + " created for " + policy.GridName +
                ". Snapshot " + group.Count + " mechanically linked grids / " + blockCount +
                " blocks, value " + Money(baselineValue) +
                " SC, enrollment " + Money(fee) + " SC.");
        }

        private void ShowStatus(IMyPlayer player, ulong steamId, long clientGridId, long policyId,
            long serviceTerminalId)
        {
            InsurancePolicy policy = ResolveOwnedPolicy(player, clientGridId, policyId);
            if (policy == null)
            {
                SendResponse(steamId, "Policy not found. Refresh the Insurance target selector.");
                return;
            }

            ClaimQuote quote = BuildQuote(policy);
            StringBuilder text = new StringBuilder(FormatQuote(policy, quote));
            if (HasRecoveryOrder(policy))
            {
                text.Append(FormatRecoveryOrder(policy, serviceTerminalId, DateTime.UtcNow.Ticks));
            }
            else
            {
                IMyFunctionalBlock terminal = FindServiceTerminal(serviceTerminalId);
                if (policyId > 0 && terminal != null && !IsPolicyWithinServiceRange(policy, terminal))
                {
                    double distance = Vector3D.Distance(terminal.GetPosition(), policy.LastKnownPose.Position);
                    long transportFee = InsuranceMath.RemoteRecoveryFee(distance,
                        _config.RemoteRecoveryFeePerKilometer);
                    long cooldown = InsuranceMath.RemoteRecoveryCooldownSeconds(distance,
                        _config.RemoteRecoverySecondsPerKilometer, _config.RemoteRecoveryMinimumSeconds,
                        _config.RemoteRecoveryMaximumSeconds);
                    text.Append("\nRemote recovery: ").Append(Distance(distance)).Append(", transport ")
                        .Append(Money(transportFee)).Append(" SC, cooldown ").Append(Duration(cooldown))
                        .Append(". Use Claim to order.");
                }
            }
            SendResponse(steamId, text.ToString());
        }

        private void ShowHistory(IMyPlayer player, ulong steamId, long clientGridId, long policyId)
        {
            InsurancePolicy policy = ResolveOwnedPolicy(player, clientGridId, policyId);
            if (policy == null)
            {
                SendResponse(steamId, "Policy not found. Refresh the Insurance target selector.");
                return;
            }

            StringBuilder text = new StringBuilder("Policy #").Append(policy.PolicyId).Append(" recent incidents:");
            int start = Math.Max(0, policy.Incidents.Count - 8);
            if (start == policy.Incidents.Count) text.Append(" none");
            for (int i = start; i < policy.Incidents.Count; i++)
            {
                IncidentRecord incident = policy.Incidents[i];
                text.Append("\n").Append(new DateTime(incident.UtcTicks, DateTimeKind.Utc).ToString("u", CultureInfo.InvariantCulture))
                    .Append(" ").Append(incident.EventType);
                if (!string.IsNullOrEmpty(incident.DamageType)) text.Append("/").Append(incident.DamageType);
                if (!string.IsNullOrEmpty(incident.AttackerName)) text.Append(" by ").Append(incident.AttackerName);
                if (incident.EventCount > 1) text.Append(" x").Append(incident.EventCount);
            }
            SendResponse(steamId, text.ToString());
        }

        private void CancelPolicy(IMyPlayer player, ulong steamId, long clientGridId, long policyId)
        {
            InsurancePolicy policy = ResolveOwnedPolicy(player, clientGridId, policyId);
            if (policy == null)
            {
                SendResponse(steamId, "Policy not found. Refresh the Insurance target selector.");
                return;
            }

            _state.Policies.Remove(policy);
            for (int i = 0; i < policy.Grids.Count; i++)
            {
                long gridId = policy.Grids[i].GridEntityId;
                if (FindPolicyByGrid(gridId) == null) UnsubscribeGrid(gridId);
            }
            _dirty = true;
            SaveState();
            SendResponse(steamId, "Policy #" + policy.PolicyId + " canceled. Enrollment fee is not refunded.");
        }

        private void ReloadConfig(IMyPlayer player, ulong steamId)
        {
            if (player.PromoteLevel < MyPromoteLevel.Admin)
            {
                SendResponse(steamId, "Admin permission required.");
                return;
            }

            LoadConfig();
            _blockValues.Clear();
            SendResponse(steamId, "ShipInsuranceConfig.xml reloaded.");
        }

        private void Claim(IMyPlayer player, ulong steamId, long clientGridId, long policyId,
            long serviceTerminalId)
        {
            InsurancePolicy policy = ResolveOwnedPolicy(player, clientGridId, policyId);
            if (policy == null)
            {
                SendResponse(steamId, "Policy not found. Refresh the Services Terminal policy list.");
                return;
            }

            IMyFunctionalBlock serviceTerminal = FindServiceTerminal(serviceTerminalId);
            if (HasRecoveryOrder(policy) && FindServiceTerminal(policy.RecoveryTerminalEntityId) == null)
            {
                ClearRecoveryOrder(policy);
                _dirty = true;
                SaveState();
            }

            bool hasLiveGrid = HasLivePolicyGrid(policy);
            bool remoteRecovery = HasRecoveryOrder(policy) ||
                                  (serviceTerminal != null && policyId > 0 &&
                                   !IsPolicyWithinServiceRange(policy, serviceTerminal));
            if (hasLiveGrid)
            {
                if (policyId == 0 && FindPolicyByGrid(clientGridId) != policy)
                {
                    SendResponse(steamId, "Select an insured group or policy at the Services Terminal.");
                    return;
                }

                for (int i = 0; i < policy.Grids.Count; i++)
                {
                    IMyCubeGrid grid = FindGrid(policy.Grids[i].GridEntityId);
                    if (grid == null) continue;
                    if (_config.RequireGridOwner && !grid.BigOwners.Contains(player.IdentityId))
                    {
                        SendResponse(steamId, "You must remain a major owner of every surviving insured grid.");
                        return;
                    }

                    if (!remoteRecovery && _config.RequireStationaryGridForClaim && grid.Physics != null &&
                        grid.Physics.LinearVelocity.LengthSquared() >
                        _config.MaximumClaimLinearSpeed * _config.MaximumClaimLinearSpeed)
                    {
                        SendResponse(steamId, "Stop the mechanical grid group before claiming.");
                        return;
                    }
                }
            }
            else if (!_config.AllowTotalLossRespawn)
            {
                SendResponse(steamId, "Total-loss recovery is disabled by server config.");
                return;
            }

            ClaimQuote quote = BuildQuote(policy);
            if (remoteRecovery)
            {
                quote.Recovery = true;
                quote.Cost = InsuranceMath.ClaimFee(quote.BaselineValue,
                    _config.ClaimValueFraction, _config.MinimumClaimFee);
            }
            if (quote.Recovery && !_config.AllowTotalLossRespawn)
            {
                SendResponse(steamId, "Full group recovery is disabled by server config.");
                return;
            }

            if (remoteRecovery)
            {
                if (!HasRecoveryOrder(policy))
                {
                    BeginRemoteRecovery(player, steamId, policy, serviceTerminal, quote);
                    return;
                }

                if (policy.RecoveryTerminalEntityId != serviceTerminalId)
                {
                    SendResponse(steamId, "Recovery is assigned to the Services Terminal that placed the order." +
                        FormatRecoveryOrder(policy, serviceTerminalId, DateTime.UtcNow.Ticks));
                    return;
                }

                long remainingSeconds = InsuranceMath.RemainingSeconds(policy.RecoveryReadyUtcTicks,
                    DateTime.UtcNow.Ticks);
                if (remainingSeconds > 0)
                {
                    SendResponse(steamId, FormatQuote(policy, quote) +
                        FormatRecoveryOrder(policy, serviceTerminalId, DateTime.UtcNow.Ticks));
                    return;
                }
            }

            MyPositionAndOrientation recoveryPose = policy.LastKnownPose;
            if (remoteRecovery && !TryGetServiceRecoveryPose(serviceTerminal, policy, out recoveryPose))
            {
                SendResponse(steamId, "No clear recovery space was found near this Services Terminal.");
                return;
            }

            if (quote.Recovery && !IsRecoveryAreaClear(policy, recoveryPose))
            {
                SendResponse(steamId, "Recovery area contains an uninsured grid. Clear it first.");
                return;
            }

            if (!quote.Recovery && quote.ConflictingBlocks > 0)
            {
                SendResponse(steamId, FormatQuote(policy, quote) +
                    "\nClaim blocked: " + quote.ConflictingBlocks + " insured positions contain different blocks.");
                return;
            }

            if (!remoteRecovery && quote.LossRatio + 0.0000001 < InsuranceMath.Clamp01(_config.MinimumLossRatio))
            {
                SendResponse(steamId, FormatQuote(policy, quote) +
                    "\nMinimum claim threshold is " + Percent(_config.MinimumLossRatio) + ".");
                return;
            }

            if (!quote.Recovery && quote.LossValue <= 0)
            {
                SendResponse(steamId, "Nothing covered needs repair.");
                return;
            }

            long balance = 0;
            if (quote.Cost > 0 && (!player.TryGetBalanceInfo(out balance) || balance < quote.Cost))
            {
                SendResponse(steamId, FormatQuote(policy, quote) + "\nInsufficient balance: " + Money(balance) + " SC.");
                return;
            }

            if (quote.Cost > 0) player.RequestChangeBalance(-quote.Cost);
            long repairedLossValue = 0;
            int repairedBlocks = 0;

            if (quote.Recovery)
            {
                if (!RecoverGridGroup(policy, recoveryPose))
                {
                    if (quote.Cost > 0) player.RequestChangeBalance(quote.Cost);
                    SendResponse(steamId, "Group recovery failed. Claim charge was refunded.");
                    return;
                }

                repairedLossValue = policy.BaselineValue;
                repairedBlocks = CountSnapshotBlocks(policy);
            }
            else
            {
                for (int i = 0; i < policy.Grids.Count; i++)
                {
                    IMyCubeGrid repairGrid = FindGrid(policy.Grids[i].GridEntityId);
                    if (repairGrid != null) _repairingGrids.Add(repairGrid.EntityId);
                }
                try
                {
                    for (int i = 0; i < quote.Items.Count; i++)
                    {
                        RepairItem item = quote.Items[i];
                        bool repaired = item.Missing
                            ? RestoreMissingBlock(item.Grid, item.Snapshot)
                            : RestoreDamagedBlock(item);
                        if (!repaired) continue;
                        repairedBlocks++;
                        repairedLossValue = InsuranceMath.Add(repairedLossValue, item.LossValue);
                    }
                }
                finally
                {
                    for (int i = 0; i < policy.Grids.Count; i++)
                        _repairingGrids.Remove(policy.Grids[i].GridEntityId);
                }
            }

            long actualCost = quote.Recovery
                ? quote.Cost
                : InsuranceMath.ClaimFee(repairedLossValue, _config.ClaimValueFraction, _config.MinimumClaimFee);
            if (actualCost > quote.Cost) actualCost = quote.Cost;
            long refund = quote.Cost - actualCost;
            if (refund > 0) player.RequestChangeBalance(refund);

            long transportFee = policy.RecoveryTransportFee;
            policy.LastClaimUtcTicks = DateTime.UtcNow.Ticks;
            ClearRecoveryOrder(policy);
            AddIncident(policy, new IncidentRecord
            {
                UtcTicks = policy.LastClaimUtcTicks,
                EventType = remoteRecovery ? "RemoteRecovery" :
                    quote.TotalLoss ? "TotalLossRecovery" : quote.Recovery ? "Recovery" : "Claim",
                AttackerIdentityId = player.IdentityId,
                AttackerName = player.DisplayName,
                DamageAmount = repairedBlocks
            });
            _dirty = true;
            SaveState();

            SendResponse(steamId, "Claim complete for policy #" + policy.PolicyId + ": restored " + repairedBlocks +
                " blocks, charged " + Money(actualCost) + " SC" +
                (remoteRecovery ? ", plus " + Money(transportFee) + " SC transport paid when ordered" : string.Empty) +
                (refund > 0 ? ", refunded " + Money(refund) + " SC." : "."));
        }

        private void BeginRemoteRecovery(IMyPlayer player, ulong steamId, InsurancePolicy policy,
            IMyFunctionalBlock serviceTerminal, ClaimQuote quote)
        {
            if (serviceTerminal == null)
            {
                SendResponse(steamId, "Services Terminal is unavailable.");
                return;
            }

            double distance = Vector3D.Distance(serviceTerminal.GetPosition(), policy.LastKnownPose.Position);
            long transportFee = InsuranceMath.RemoteRecoveryFee(distance,
                _config.RemoteRecoveryFeePerKilometer);
            long cooldownSeconds = InsuranceMath.RemoteRecoveryCooldownSeconds(distance,
                _config.RemoteRecoverySecondsPerKilometer, _config.RemoteRecoveryMinimumSeconds,
                _config.RemoteRecoveryMaximumSeconds);
            long balance = 0;
            if (transportFee > 0 && (!player.TryGetBalanceInfo(out balance) || balance < transportFee))
            {
                SendResponse(steamId, FormatQuote(policy, quote) + "\nRemote transport costs " +
                    Money(transportFee) + " SC; balance " + Money(balance) + " SC.");
                return;
            }

            if (transportFee > 0) player.RequestChangeBalance(-transportFee);
            long nowUtcTicks = DateTime.UtcNow.Ticks;
            policy.RecoveryTerminalEntityId = serviceTerminal.EntityId;
            policy.RecoveryReadyUtcTicks = InsuranceMath.Add(nowUtcTicks,
                InsuranceMath.Multiply(cooldownSeconds, TimeSpan.TicksPerSecond));
            policy.RecoveryDistanceMeters = distance;
            policy.RecoveryTransportFee = transportFee;
            policy.RecoveryExpedited = false;
            AddIncident(policy, new IncidentRecord
            {
                UtcTicks = nowUtcTicks,
                EventType = "RemoteRecoveryOrdered",
                AttackerIdentityId = player.IdentityId,
                AttackerName = player.DisplayName
            });
            _dirty = true;
            SaveState();

            SendResponse(steamId, FormatQuote(policy, quote) +
                FormatRecoveryOrder(policy, serviceTerminal.EntityId, nowUtcTicks));
        }

        private void ExpediteRecovery(IMyPlayer player, ulong steamId, long policyId,
            long serviceTerminalId)
        {
            InsurancePolicy policy = ResolveOwnedPolicy(player, 0, policyId);
            if (policy == null)
            {
                SendResponse(steamId, "Select a recovery policy at the Services Terminal.");
                return;
            }
            if (!HasRecoveryOrder(policy))
            {
                SendResponse(steamId, "Start remote recovery with Claim before expediting it.");
                return;
            }
            if (policy.RecoveryTerminalEntityId != serviceTerminalId)
            {
                SendResponse(steamId, "Only the Services Terminal that placed this order can expedite it.");
                return;
            }
            if (policy.RecoveryExpedited)
            {
                SendResponse(steamId, "This recovery order has already been expedited once.");
                return;
            }

            long nowUtcTicks = DateTime.UtcNow.Ticks;
            long remainingSeconds = InsuranceMath.RemainingSeconds(policy.RecoveryReadyUtcTicks,
                nowUtcTicks);
            if (remainingSeconds <= 0)
            {
                SendResponse(steamId, "Recovery has arrived. Use Claim to deploy it.");
                return;
            }
            double factor = InsuranceMath.Clamp01(_config.RemoteRecoveryExpediteFactor);
            if (factor >= 1.0)
            {
                SendResponse(steamId, "Remote recovery expedite is disabled by server config.");
                return;
            }

            long expeditePrice = InsuranceMath.ExpediteFee(remainingSeconds,
                _config.RemoteRecoveryExpediteCostPerSecond);
            long balance = 0;
            if (expeditePrice > 0 && (!player.TryGetBalanceInfo(out balance) || balance < expeditePrice))
            {
                SendResponse(steamId, "Expedite costs " + Money(expeditePrice) +
                    " SC; balance " + Money(balance) + " SC.");
                return;
            }

            if (expeditePrice > 0) player.RequestChangeBalance(-expeditePrice);
            policy.RecoveryReadyUtcTicks = InsuranceMath.ExpediteReadyUtcTicks(nowUtcTicks,
                policy.RecoveryReadyUtcTicks, factor);
            policy.RecoveryExpedited = true;
            AddIncident(policy, new IncidentRecord
            {
                UtcTicks = nowUtcTicks,
                EventType = "RemoteRecoveryExpedited",
                AttackerIdentityId = player.IdentityId,
                AttackerName = player.DisplayName
            });
            _dirty = true;
            SaveState();

            SendResponse(steamId, "Recovery expedited for " + Money(expeditePrice) + " SC." +
                FormatRecoveryOrder(policy, serviceTerminalId, nowUtcTicks));
        }

        private ClaimQuote BuildQuote(InsurancePolicy policy)
        {
            ClaimQuote quote = new ClaimQuote
            {
                TotalLoss = !HasLivePolicyGrid(policy),
                BaselineValue = Math.Max(0, policy.BaselineValue)
            };

            bool missingGrid = false;
            for (int gridIndex = 0; gridIndex < policy.Grids.Count; gridIndex++)
            {
                InsuredGridSnapshot insuredGrid = policy.Grids[gridIndex];
                MyObjectBuilder_CubeGrid blueprint = insuredGrid.Blueprint;
                if (blueprint == null || blueprint.CubeBlocks == null) continue;
                IMyCubeGrid grid = FindGrid(insuredGrid.GridEntityId);
                if (grid == null) missingGrid = true;

                for (int i = 0; i < blueprint.CubeBlocks.Count; i++)
                {
                    MyObjectBuilder_CubeBlock snapshot = blueprint.CubeBlocks[i];
                    long fullValue = GetBlockValue(snapshot);
                    double targetRatio = InsuranceMath.Clamp01(snapshot.IntegrityPercent);
                    IMySlimBlock current = grid == null ? null : grid.GetCubeBlock(snapshot.Min);

                    if (current == null)
                    {
                        long loss = InsuranceMath.Scale(fullValue, targetRatio);
                        quote.MissingBlocks++;
                        AddRepairItem(quote, snapshot, null, grid, loss, targetRatio, true);
                        continue;
                    }

                    if (!BlocksMatch(snapshot, current))
                    {
                        quote.ConflictingBlocks++;
                        quote.LossValue = InsuranceMath.Add(quote.LossValue,
                            InsuranceMath.Scale(fullValue, targetRatio));
                        continue;
                    }

                    double currentRatio = current.MaxIntegrity <= 0f ? 0.0 : current.Integrity / current.MaxIntegrity;
                    double delta = Math.Max(0.0, targetRatio - currentRatio);
                    if (delta <= 0.0001) continue;

                    long damageLoss = InsuranceMath.Scale(fullValue, delta);
                    quote.DamagedBlocks++;
                    AddRepairItem(quote, snapshot, current, grid, damageLoss, delta, false);
                }
            }

            quote.Recovery = InsuranceMath.RequiresRecovery(quote.TotalLoss, missingGrid, quote.LossRatio);
            quote.Cost = InsuranceMath.ClaimFee(quote.Recovery ? quote.BaselineValue : quote.LossValue,
                _config.ClaimValueFraction, _config.MinimumClaimFee);
            return quote;
        }

        private void AddRepairItem(ClaimQuote quote, MyObjectBuilder_CubeBlock snapshot, IMySlimBlock current,
            IMyCubeGrid grid,
            long lossValue, double integrityDelta, bool missing)
        {
            quote.LossValue = InsuranceMath.Add(quote.LossValue, lossValue);
            quote.Items.Add(new RepairItem
            {
                Snapshot = snapshot,
                Existing = current,
                Grid = grid,
                LossValue = lossValue,
                IntegrityDelta = integrityDelta,
                Missing = missing
            });
        }

        private static bool BlocksMatch(MyObjectBuilder_CubeBlock snapshot, IMySlimBlock current)
        {
            return current.Min == (Vector3I)snapshot.Min &&
                   current.BlockDefinition.Id == snapshot.GetId() &&
                   current.Orientation.Forward == snapshot.BlockOrientation.Forward &&
                   current.Orientation.Up == snapshot.BlockOrientation.Up;
        }

        private bool RestoreMissingBlock(IMyCubeGrid grid, MyObjectBuilder_CubeBlock snapshot)
        {
            try
            {
                MyObjectBuilder_CubeBlock clone = Clone(snapshot);
                clone.EntityId = 0;
                clone.Name = null;
                return grid.AddBlock(clone, true) != null;
            }
            catch (Exception exception)
            {
                Log("Could not restore block at " + snapshot.Min, exception);
                return false;
            }
        }

        private bool RestoreDamagedBlock(RepairItem item)
        {
            if (item.Existing == null || item.Existing.IsDestroyed) return false;

            try
            {
                float integrityBefore = item.Existing.Integrity;
                MyCubeBlockDefinition definition = item.Existing.BlockDefinition as MyCubeBlockDefinition;
                float mountAmount = float.MaxValue;
                if (definition != null && definition.IntegrityPointsPerSec > 0f)
                    mountAmount = (float)(item.Existing.MaxIntegrity * item.IntegrityDelta / definition.IntegrityPointsPerSec);

                item.Existing.IncreaseMountLevel(mountAmount, item.Snapshot.Owner, null, float.MaxValue, true, item.Snapshot.ShareMode);
                if (item.Existing.HasDeformation) item.Existing.FixBones(0f, float.MaxValue);
                return item.Existing.Integrity > integrityBefore + 0.001f;
            }
            catch (Exception exception)
            {
                Log("Could not repair block at " + item.Snapshot.Min, exception);
                return false;
            }
        }

        private bool RecoverGridGroup(InsurancePolicy policy, MyPositionAndOrientation recoveryPose)
        {
            List<IMyCubeGrid> created = new List<IMyCubeGrid>(policy.Grids.Count);
            List<long> recoveringIds = new List<long>(policy.Grids.Count);
            bool committed = false;
            try
            {
                MatrixD anchorMatrix = recoveryPose.GetMatrix();
                List<MyObjectBuilder_CubeGrid> clones = new List<MyObjectBuilder_CubeGrid>(policy.Grids.Count);
                for (int i = 0; i < policy.Grids.Count; i++)
                {
                    InsuredGridSnapshot snapshot = policy.Grids[i];
                    MyObjectBuilder_CubeGrid clone = Clone(snapshot.Blueprint);
                    clone.PositionAndOrientation = new MyPositionAndOrientation(snapshot.RelativePose.GetMatrix() * anchorMatrix);
                    clone.LinearVelocity = Vector3.Zero;
                    clone.AngularVelocity = Vector3.Zero;
                    for (int blockIndex = 0; blockIndex < clone.CubeBlocks.Count; blockIndex++)
                    {
                        clone.CubeBlocks[blockIndex].BuildPercent = 1f;
                        clone.CubeBlocks[blockIndex].IntegrityPercent = 1f;
                    }
                    clones.Add(clone);
                }

                MyAPIGateway.Entities.RemapObjectBuilderCollection(clones);
                for (int i = 0; i < clones.Count; i++)
                {
                    IMyCubeGrid grid = MyAPIGateway.Entities.CreateFromObjectBuilder(clones[i]) as IMyCubeGrid;
                    if (grid == null) return false;
                    created.Add(grid);
                }

                for (int i = 0; i < policy.Grids.Count; i++)
                {
                    long oldId = policy.Grids[i].GridEntityId;
                    IMyCubeGrid oldGrid = FindGrid(oldId);
                    UnsubscribeGrid(oldId);
                    if (oldGrid == null) continue;
                    _repairingGrids.Add(oldId);
                    recoveringIds.Add(oldId);
                    oldGrid.Close();
                    MyAPIGateway.Entities.RemoveEntity(oldGrid);
                }

                for (int i = 0; i < created.Count; i++)
                {
                    MyAPIGateway.Entities.AddEntity(created[i], true);
                    policy.Grids[i].GridEntityId = created[i].EntityId;
                    SubscribeGrid(created[i]);
                }
                policy.GridEntityId = policy.Grids[0].GridEntityId;
                policy.LastKnownPose = recoveryPose;
                committed = true;
                try
                {
                    List<MyObjectBuilder_EntityBase> spawnedBuilders = new List<MyObjectBuilder_EntityBase>(clones.Count);
                    for (int i = 0; i < clones.Count; i++) spawnedBuilders.Add(clones[i]);
                    MyAPIGateway.Multiplayer.SendEntitiesCreated(spawnedBuilders);
                }
                catch (Exception exception)
                {
                    Log("Could not announce recovered grids", exception);
                }
                return true;
            }
            catch (Exception exception)
            {
                Log("Grid group recovery failed", exception);
                return false;
            }
            finally
            {
                for (int i = 0; i < recoveringIds.Count; i++) _repairingGrids.Remove(recoveringIds[i]);
                if (!committed)
                    for (int i = 0; i < created.Count; i++)
                        if (!created[i].Closed) created[i].Close();
            }
        }

        private bool IsRecoveryAreaClear(InsurancePolicy policy, MyPositionAndOrientation recoveryPose)
        {
            BoundingSphereD sphere = new BoundingSphereD(recoveryPose.Position, Math.Max(5.0, policy.ClearanceRadius));
            List<IMyEntity> entities = MyAPIGateway.Entities.GetEntitiesInSphere(ref sphere);
            for (int i = 0; i < entities.Count; i++)
            {
                IMyCubeGrid grid = entities[i] as IMyCubeGrid;
                if (grid != null && !grid.Closed && FindPolicyByGrid(grid.EntityId) != policy) return false;
            }
            return true;
        }

        private string FormatQuote(InsurancePolicy policy, ClaimQuote quote)
        {
            return "Policy #" + policy.PolicyId + " " + policy.GridName +
                   "\nLoss: " + Percent(quote.LossRatio) + " (" + Money(quote.LossValue) + " / " + Money(quote.BaselineValue) + " SC)" +
                   "\nMissing: " + quote.MissingBlocks + ", damaged: " + quote.DamagedBlocks + ", conflicts: " + quote.ConflictingBlocks +
                   "\nClaim price: " + Money(quote.Cost) + " SC" +
                   (quote.TotalLoss ? " [total-loss recovery]" : quote.Recovery ? " [full group recovery]" : string.Empty);
        }

        private string FormatRecoveryOrder(InsurancePolicy policy, long serviceTerminalId, long nowUtcTicks)
        {
            long remainingSeconds = InsuranceMath.RemainingSeconds(policy.RecoveryReadyUtcTicks,
                nowUtcTicks);
            StringBuilder text = new StringBuilder("\nRemote recovery: ")
                .Append(Distance(policy.RecoveryDistanceMeters)).Append(", transport ")
                .Append(Money(policy.RecoveryTransportFee)).Append(" SC paid, ");
            if (remainingSeconds <= 0)
            {
                text.Append("ready to deploy with Claim");
            }
            else
            {
                text.Append("ETA ").Append(Duration(remainingSeconds));
                if (policy.RecoveryExpedited)
                {
                    text.Append(" [expedited]");
                }
                else if (policy.RecoveryTerminalEntityId == serviceTerminalId &&
                         InsuranceMath.Clamp01(_config.RemoteRecoveryExpediteFactor) < 1.0)
                {
                    long expeditePrice = InsuranceMath.ExpediteFee(remainingSeconds,
                        _config.RemoteRecoveryExpediteCostPerSecond);
                    int reduction = (int)Math.Round(
                        (1.0 - InsuranceMath.Clamp01(_config.RemoteRecoveryExpediteFactor)) * 100.0);
                    text.Append("; Expedite ").Append(Money(expeditePrice)).Append(" SC for ")
                        .Append(reduction).Append("% less remaining time");
                }
            }
            if (policy.RecoveryTerminalEntityId != serviceTerminalId)
                text.Append(" [assigned to another Services Terminal]");
            return text.ToString();
        }

        private static bool HasRecoveryOrder(InsurancePolicy policy)
        {
            return policy != null && policy.RecoveryTerminalEntityId != 0 &&
                   policy.RecoveryReadyUtcTicks > 0;
        }

        private static void ClearRecoveryOrder(InsurancePolicy policy)
        {
            policy.RecoveryTerminalEntityId = 0;
            policy.RecoveryReadyUtcTicks = 0;
            policy.RecoveryDistanceMeters = 0.0;
            policy.RecoveryTransportFee = 0;
            policy.RecoveryExpedited = false;
        }

        private static string Distance(double meters)
        {
            return meters >= 1000.0
                ? (meters / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " km"
                : Math.Max(0.0, meters).ToString("0", CultureInfo.InvariantCulture) + " m";
        }

        private static string Duration(long seconds)
        {
            TimeSpan duration = TimeSpan.FromSeconds(Math.Max(0, seconds));
            return duration.TotalDays >= 1.0
                ? duration.Days + "d " + duration.ToString("hh\\:mm\\:ss", CultureInfo.InvariantCulture)
                : duration.ToString("hh\\:mm\\:ss", CultureInfo.InvariantCulture);
        }

        private MyObjectBuilder_CubeGrid CreateSanitizedBlueprint(IMyCubeGrid grid)
        {
            MyObjectBuilder_CubeGrid blueprint = grid.GetObjectBuilder(true) as MyObjectBuilder_CubeGrid;
            if (blueprint == null || blueprint.CubeBlocks == null) return null;

            List<long> owners = new List<long>(blueprint.CubeBlocks.Count);
            List<MyOwnershipShareModeEnum> shares = new List<MyOwnershipShareModeEnum>(blueprint.CubeBlocks.Count);
            for (int i = 0; i < blueprint.CubeBlocks.Count; i++)
            {
                owners.Add(blueprint.CubeBlocks[i].Owner);
                shares.Add(blueprint.CubeBlocks[i].ShareMode);
            }

            blueprint.SetupForProjector();
            for (int i = 0; i < blueprint.CubeBlocks.Count; i++)
            {
                blueprint.CubeBlocks[i].Owner = owners[i];
                blueprint.CubeBlocks[i].ShareMode = shares[i];
            }
            return blueprint;
        }

        private long CalculateBaselineValue(MyObjectBuilder_CubeGrid blueprint)
        {
            long value = 0;
            for (int i = 0; i < blueprint.CubeBlocks.Count; i++)
            {
                MyObjectBuilder_CubeBlock block = blueprint.CubeBlocks[i];
                value = InsuranceMath.Add(value, InsuranceMath.Scale(GetBlockValue(block), InsuranceMath.Clamp01(block.IntegrityPercent)));
            }
            return value;
        }

        private long GetBlockValue(MyObjectBuilder_CubeBlock block)
        {
            MyDefinitionId id = block.GetId();
            long cached;
            if (_blockValues.TryGetValue(id, out cached)) return cached;

            long value = 0;
            MyCubeBlockDefinition definition;
            if (MyDefinitionManager.Static.TryGetCubeBlockDefinition(id, out definition) && definition.Components != null)
            {
                for (int i = 0; i < definition.Components.Length; i++)
                {
                    MyCubeBlockDefinition.Component component = definition.Components[i];
                    long unitValue = component.Definition == null || component.Definition.MinimalPricePerUnit <= 0
                        ? Math.Max(0, _config.UnknownComponentValue)
                        : component.Definition.MinimalPricePerUnit;
                    value = InsuranceMath.Add(value, InsuranceMath.Scale(unitValue, component.Count));
                }
            }

            if (value <= 0) value = Math.Max(0, _config.UnknownBlockValue);
            _blockValues[id] = value;
            return value;
        }

        private static T Clone<T>(T value)
        {
            return MyAPIGateway.Utilities.SerializeFromBinary<T>(MyAPIGateway.Utilities.SerializeToBinary(value));
        }

        private void OnDamageApplied(object target, MyDamageInformation information)
        {
            if (!_active || !_isServer || information.Amount <= 0f) return;

            IMySlimBlock block = target as IMySlimBlock;
            if (block == null || block.CubeGrid == null || _repairingGrids.Contains(block.CubeGrid.EntityId)) return;

            InsurancePolicy policy = FindPolicyByGrid(block.CubeGrid.EntityId);
            if (policy == null) return;

            long attackerIdentity;
            string attackerName;
            ResolveAttacker(information.AttackerId, out attackerIdentity, out attackerName);
            string damageType = information.Type.String ?? information.Type.ToString();
            long now = DateTime.UtcNow.Ticks;

            Dictionary<Vector3I, DamageAttribution> blocks;
            if (!_lastDamage.TryGetValue(block.CubeGrid.EntityId, out blocks))
            {
                blocks = new Dictionary<Vector3I, DamageAttribution>();
                _lastDamage[block.CubeGrid.EntityId] = blocks;
            }
            blocks[block.Min] = new DamageAttribution
            {
                UtcTicks = now,
                AttackerEntityId = information.AttackerId,
                AttackerIdentityId = attackerIdentity,
                AttackerName = attackerName,
                DamageType = damageType
            };

            IncidentRecord last = policy.Incidents.Count == 0 ? null : policy.Incidents[policy.Incidents.Count - 1];
            if (last != null && last.EventType == "Damage" && last.AttackerEntityId == information.AttackerId &&
                last.DamageType == damageType && now - last.UtcTicks <= TimeSpan.TicksPerSecond * 10)
            {
                last.UtcTicks = now;
                last.DamageAmount += information.Amount;
                last.EventCount++;
            }
            else
            {
                AddIncident(policy, new IncidentRecord
                {
                    UtcTicks = now,
                    EventType = "Damage",
                    X = block.Min.X,
                    Y = block.Min.Y,
                    Z = block.Min.Z,
                    AttackerEntityId = information.AttackerId,
                    AttackerIdentityId = attackerIdentity,
                    AttackerName = attackerName,
                    DamageType = damageType,
                    DamageAmount = information.Amount
                });
            }
            _dirty = true;
        }

        private void OnBlockAdded(IMySlimBlock block)
        {
            if (!_active || block == null || block.CubeGrid == null || _repairingGrids.Contains(block.CubeGrid.EntityId)) return;
            InsurancePolicy policy = FindPolicyByGrid(block.CubeGrid.EntityId);
            if (policy == null) return;

            AddIncident(policy, new IncidentRecord
            {
                UtcTicks = DateTime.UtcNow.Ticks,
                EventType = "BlockAdded",
                X = block.Min.X,
                Y = block.Min.Y,
                Z = block.Min.Z,
                AttackerIdentityId = block.BuiltBy,
                AttackerName = ResolveIdentityName(block.BuiltBy)
            });
            _dirty = true;
        }

        private void OnBlockRemoved(IMySlimBlock block)
        {
            if (!_active || block == null || block.CubeGrid == null || _repairingGrids.Contains(block.CubeGrid.EntityId)) return;
            InsurancePolicy policy = FindPolicyByGrid(block.CubeGrid.EntityId);
            if (policy == null) return;

            DamageAttribution attribution = null;
            Dictionary<Vector3I, DamageAttribution> blocks;
            if (_lastDamage.TryGetValue(block.CubeGrid.EntityId, out blocks))
            {
                blocks.TryGetValue(block.Min, out attribution);
                blocks.Remove(block.Min);
            }

            AddIncident(policy, new IncidentRecord
            {
                UtcTicks = DateTime.UtcNow.Ticks,
                EventType = "BlockRemoved",
                X = block.Min.X,
                Y = block.Min.Y,
                Z = block.Min.Z,
                AttackerEntityId = attribution == null ? 0 : attribution.AttackerEntityId,
                AttackerIdentityId = attribution == null ? 0 : attribution.AttackerIdentityId,
                AttackerName = attribution == null ? null : attribution.AttackerName,
                DamageType = attribution == null ? null : attribution.DamageType
            });
            _dirty = true;
        }

        private void AddIncident(InsurancePolicy policy, IncidentRecord incident)
        {
            if (policy.Incidents == null) policy.Incidents = new List<IncidentRecord>();
            policy.Incidents.Add(incident);
            int maximum = Math.Max(1, _config.MaxIncidentLogEntries);
            while (policy.Incidents.Count > maximum) policy.Incidents.RemoveAt(0);
        }

        private void ResolveAttacker(long entityId, out long identityId, out string name)
        {
            identityId = 0;
            name = null;
            if (entityId == 0) return;

            IMyEntity entity;
            if (!MyAPIGateway.Entities.TryGetEntityById(entityId, out entity) || entity == null) return;

            IMyCharacter character = entity as IMyCharacter;
            if (character != null && character.ControllerInfo != null)
                identityId = character.ControllerInfo.ControllingIdentityId;

            IMyCubeBlock cubeBlock = entity as IMyCubeBlock;
            if (cubeBlock != null) identityId = cubeBlock.OwnerId;

            IMyCubeGrid grid = entity as IMyCubeGrid;
            if (grid != null && grid.BigOwners.Count > 0) identityId = grid.BigOwners[0];

            name = ResolveIdentityName(identityId);
            if (string.IsNullOrEmpty(name)) name = entity.DisplayName;
        }

        private string ResolveIdentityName(long identityId)
        {
            if (identityId == 0) return null;
            string cached;
            if (_identityNames.TryGetValue(identityId, out cached)) return cached;

            List<IMyPlayer> players = new List<IMyPlayer>();
            MyAPIGateway.Players.GetPlayers(players, delegate(IMyPlayer value) { return value.IdentityId == identityId; });
            if (players.Count > 0) cached = players[0].DisplayName;

            if (string.IsNullOrEmpty(cached))
            {
                List<IMyIdentity> identities = new List<IMyIdentity>();
                MyAPIGateway.Players.GetAllIdentites(identities, delegate(IMyIdentity value) { return value.IdentityId == identityId; });
                if (identities.Count > 0) cached = identities[0].DisplayName;
            }

            if (string.IsNullOrEmpty(cached)) cached = "Identity " + identityId;
            _identityNames[identityId] = cached;
            return cached;
        }

        private void RefreshGridSubscriptions()
        {
            List<long> stale = new List<long>();
            foreach (KeyValuePair<long, IMyCubeGrid> pair in _subscribedGrids)
                if (FindGrid(pair.Key) == null) stale.Add(pair.Key);
            for (int i = 0; i < stale.Count; i++) UnsubscribeGrid(stale[i]);

            for (int i = 0; i < _state.Policies.Count; i++)
            {
                InsurancePolicy policy = _state.Policies[i];
                for (int gridIndex = 0; gridIndex < policy.Grids.Count; gridIndex++)
                {
                    IMyCubeGrid grid = FindGrid(policy.Grids[gridIndex].GridEntityId);
                    if (grid != null) SubscribeGrid(grid);
                }
            }
        }

        private void SubscribeGrid(IMyCubeGrid grid)
        {
            if (grid == null || _subscribedGrids.ContainsKey(grid.EntityId)) return;
            grid.OnBlockAdded += OnBlockAdded;
            grid.OnBlockRemoved += OnBlockRemoved;
            _subscribedGrids[grid.EntityId] = grid;
        }

        private void UnsubscribeGrid(long gridId)
        {
            IMyCubeGrid grid;
            if (!_subscribedGrids.TryGetValue(gridId, out grid)) return;
            UnhookGrid(grid);
            _subscribedGrids.Remove(gridId);
            _lastDamage.Remove(gridId);
        }

        private void UnhookGrid(IMyCubeGrid grid)
        {
            if (grid == null) return;
            grid.OnBlockAdded -= OnBlockAdded;
            grid.OnBlockRemoved -= OnBlockRemoved;
        }

        private void UpdateLastKnownPoses()
        {
            for (int i = 0; i < _state.Policies.Count; i++)
            {
                InsurancePolicy policy = _state.Policies[i];
                for (int gridIndex = 0; gridIndex < policy.Grids.Count; gridIndex++)
                {
                    InsuredGridSnapshot snapshot = policy.Grids[gridIndex];
                    IMyCubeGrid grid = FindGrid(snapshot.GridEntityId);
                    if (grid == null) continue;
                    MatrixD anchorMatrix = MatrixD.Invert(snapshot.RelativePose.GetMatrix()) * grid.WorldMatrix;
                    policy.LastKnownPose = new MyPositionAndOrientation(anchorMatrix);
                    _dirty = true;
                    break;
                }
            }
        }

        private InsurancePolicy ResolveOwnedPolicy(IMyPlayer player, long clientGridId, long policyId)
        {
            InsurancePolicy policy = policyId > 0 ? FindPolicyById(policyId) : FindPolicyByGrid(clientGridId);
            return policy != null && policy.OwnerIdentityId == player.IdentityId ? policy : null;
        }

        private InsurancePolicy FindPolicyById(long policyId)
        {
            for (int i = 0; i < _state.Policies.Count; i++)
                if (_state.Policies[i].PolicyId == policyId) return _state.Policies[i];
            return null;
        }

        private InsurancePolicy FindPolicyByGrid(long gridId)
        {
            if (gridId == 0) return null;
            for (int i = 0; i < _state.Policies.Count; i++)
            {
                InsurancePolicy policy = _state.Policies[i];
                for (int gridIndex = 0; gridIndex < policy.Grids.Count; gridIndex++)
                    if (policy.Grids[gridIndex].GridEntityId == gridId) return policy;
            }
            return null;
        }

        private static bool HasLivePolicyGrid(InsurancePolicy policy)
        {
            if (policy == null || policy.Grids == null) return false;
            for (int i = 0; i < policy.Grids.Count; i++)
                if (FindGrid(policy.Grids[i].GridEntityId) != null) return true;
            return false;
        }

        private static int CountSnapshotBlocks(InsurancePolicy policy)
        {
            int count = 0;
            if (policy == null || policy.Grids == null) return count;
            for (int i = 0; i < policy.Grids.Count; i++)
            {
                MyObjectBuilder_CubeGrid blueprint = policy.Grids[i].Blueprint;
                if (blueprint != null && blueprint.CubeBlocks != null) count += blueprint.CubeBlocks.Count;
            }
            return count;
        }

        private static List<IMyCubeGrid> GetMechanicalGroup(IMyCubeGrid grid)
        {
            List<IMyCubeGrid> group = new List<IMyCubeGrid>();
            if (grid != null && !grid.Closed)
                MyAPIGateway.GridGroups.GetGroup(grid, GridLinkTypeEnum.Mechanical, group);
            if (group.Count == 0 && grid != null) group.Add(grid);
            group.RemoveAll(delegate(IMyCubeGrid member) { return member == null || member.Closed; });
            Dictionary<long, int> blockCounts = new Dictionary<long, int>();
            List<IMySlimBlock> blocks = new List<IMySlimBlock>();
            for (int i = 0; i < group.Count; i++)
            {
                blocks.Clear();
                group[i].GetBlocks(blocks);
                blockCounts[group[i].EntityId] = blocks.Count;
            }
            group.Sort(delegate(IMyCubeGrid left, IMyCubeGrid right)
            {
                int countComparison = blockCounts[right.EntityId].CompareTo(blockCounts[left.EntityId]);
                return countComparison != 0 ? countComparison : left.EntityId.CompareTo(right.EntityId);
            });
            return group;
        }

        private static IMyCubeGrid FindGrid(long entityId)
        {
            IMyEntity entity;
            if (entityId == 0 || !MyAPIGateway.Entities.TryGetEntityById(entityId, out entity)) return null;
            IMyCubeGrid grid = entity as IMyCubeGrid;
            return grid == null || grid.Closed ? null : grid;
        }

        private static IMyFunctionalBlock FindServiceTerminal(long entityId)
        {
            IMyEntity entity;
            if (entityId == 0 || !MyAPIGateway.Entities.TryGetEntityById(entityId, out entity)) return null;
            IMyFunctionalBlock terminal = entity as IMyFunctionalBlock;
            return terminal == null || terminal.Closed || !IsServicesTerminal(terminal) ? null : terminal;
        }

        private static bool IsServicesTerminal(IMyTerminalBlock block)
        {
            return block != null && block.BlockDefinition.SubtypeId == "ServicesTerminal";
        }

        private static bool IsPolicyWithinServiceRange(InsurancePolicy policy, IMyFunctionalBlock terminal)
        {
            if (policy == null || terminal == null) return false;
            for (int i = 0; i < policy.Grids.Count; i++)
            {
                IMyCubeGrid grid = FindGrid(policy.Grids[i].GridEntityId);
                if (grid == null) continue;
                double range = ServiceGridDiscoveryRange + grid.WorldAABB.HalfExtents.Length();
                if (Vector3D.DistanceSquared(grid.WorldAABB.Center, terminal.GetPosition()) <= range * range)
                    return true;
            }
            return false;
        }

        private static bool TryGetServiceRecoveryPose(IMyFunctionalBlock terminal, InsurancePolicy policy,
            out MyPositionAndOrientation pose)
        {
            pose = policy.LastKnownPose;
            if (terminal == null) return false;

            float radius = (float)Math.Max(5.0, policy.ClearanceRadius);
            Vector3D start = terminal.GetPosition() + terminal.WorldMatrix.Forward * (radius + 10.0);
            Vector3D? position = MyAPIGateway.Entities.FindFreePlace(start, radius, 100, 5,
                Math.Max(5f, radius));
            if (!position.HasValue) return false;

            pose = new MyPositionAndOrientation(position.Value, terminal.WorldMatrix.Forward, terminal.WorldMatrix.Up);
            return true;
        }

        private static IMyPlayer FindPlayer(ulong steamId)
        {
            List<IMyPlayer> players = new List<IMyPlayer>();
            MyAPIGateway.Players.GetPlayers(players, delegate(IMyPlayer value) { return value.SteamUserId == steamId; });
            return players.Count == 0 ? null : players[0];
        }

        private void LoadConfig()
        {
            try
            {
                if (MyAPIGateway.Utilities.FileExistsInWorldStorage(ConfigFile, typeof(InsuranceSession)))
                {
                    using (TextReader reader = MyAPIGateway.Utilities.ReadFileInWorldStorage(ConfigFile, typeof(InsuranceSession)))
                        _config = MyAPIGateway.Utilities.SerializeFromXML<InsuranceConfig>(reader.ReadToEnd()) ?? new InsuranceConfig();
                }
                else
                {
                    _config = new InsuranceConfig();
                    SaveConfig();
                }
                ValidateConfig();
            }
            catch (Exception exception)
            {
                _config = new InsuranceConfig();
                Log("Config load failed; defaults active", exception);
            }
        }

        private void ValidateConfig()
        {
            _config.EnrollmentFlatFee = Math.Max(0, _config.EnrollmentFlatFee);
            _config.EnrollmentValueFraction = Math.Max(0.0, _config.EnrollmentValueFraction);
            _config.ClaimValueFraction = Math.Max(0.0, _config.ClaimValueFraction);
            _config.MinimumLossRatio = InsuranceMath.Clamp01(_config.MinimumLossRatio);
            _config.MinimumClaimFee = Math.Max(0, _config.MinimumClaimFee);
            _config.UnknownComponentValue = Math.Max(0, _config.UnknownComponentValue);
            _config.UnknownBlockValue = Math.Max(0, _config.UnknownBlockValue);
            _config.MaxPoliciesPerPlayer = Math.Max(1, _config.MaxPoliciesPerPlayer);
            _config.MaxIncidentLogEntries = Math.Max(1, _config.MaxIncidentLogEntries);
            _config.TotalLossExtraClearance = Math.Max(0.0, _config.TotalLossExtraClearance);
            _config.MaximumClaimLinearSpeed = Math.Max(0.0, _config.MaximumClaimLinearSpeed);
            _config.RemoteRecoveryFeePerKilometer = Math.Max(0,
                _config.RemoteRecoveryFeePerKilometer);
            _config.RemoteRecoverySecondsPerKilometer = Math.Max(0.0,
                _config.RemoteRecoverySecondsPerKilometer);
            _config.RemoteRecoveryMinimumSeconds = Math.Max(0,
                _config.RemoteRecoveryMinimumSeconds);
            _config.RemoteRecoveryMaximumSeconds = Math.Max(
                _config.RemoteRecoveryMinimumSeconds, _config.RemoteRecoveryMaximumSeconds);
            _config.RemoteRecoveryExpediteCostPerSecond = Math.Max(0,
                _config.RemoteRecoveryExpediteCostPerSecond);
            _config.RemoteRecoveryExpediteFactor = InsuranceMath.Clamp01(
                _config.RemoteRecoveryExpediteFactor);
        }

        private void SaveConfig()
        {
            using (TextWriter writer = MyAPIGateway.Utilities.WriteFileInWorldStorage(ConfigFile, typeof(InsuranceSession)))
                writer.Write(MyAPIGateway.Utilities.SerializeToXML(_config));
        }

        private void LoadState()
        {
            try
            {
                if (!MyAPIGateway.Utilities.FileExistsInWorldStorage(StateFile, typeof(InsuranceSession)))
                {
                    _state = new InsuranceState();
                    return;
                }

                using (TextReader reader = MyAPIGateway.Utilities.ReadFileInWorldStorage(StateFile, typeof(InsuranceSession)))
                {
                    byte[] bytes = Convert.FromBase64String(reader.ReadToEnd());
                    _state = MyAPIGateway.Utilities.SerializeFromBinary<InsuranceState>(bytes) ?? new InsuranceState();
                }

                if (_state.Policies == null) _state.Policies = new List<InsurancePolicy>();
                for (int i = 0; i < _state.Policies.Count; i++) NormalizePolicy(_state.Policies[i]);
                if (_state.NextPolicyId < 1) _state.NextPolicyId = 1;
            }
            catch (Exception exception)
            {
                _state = new InsuranceState();
                Log("State load failed; starting empty", exception);
            }
        }

        private void NormalizePolicy(InsurancePolicy policy)
        {
            if (policy.Grids == null) policy.Grids = new List<InsuredGridSnapshot>();
            if (policy.Grids.Count == 0 && policy.Blueprint != null)
            {
                policy.Grids.Add(new InsuredGridSnapshot
                {
                    GridEntityId = policy.GridEntityId,
                    Blueprint = policy.Blueprint,
                    RelativePose = new MyPositionAndOrientation(MatrixD.Identity)
                });
                _dirty = true;
            }
            if (policy.Incidents == null) policy.Incidents = new List<IncidentRecord>();
            if (policy.GridEntityId == 0 && policy.Grids.Count > 0)
                policy.GridEntityId = policy.Grids[0].GridEntityId;
        }

        private void SaveState()
        {
            if (!_isServer || _state == null) return;
            try
            {
                string data = Convert.ToBase64String(MyAPIGateway.Utilities.SerializeToBinary(_state));
                using (TextWriter writer = MyAPIGateway.Utilities.WriteFileInWorldStorage(StateFile, typeof(InsuranceSession)))
                    writer.Write(data);
                _dirty = false;
            }
            catch (Exception exception)
            {
                Log("State save failed", exception);
            }
        }

        private void SendResponse(ulong steamId, string text)
        {
            if (!MyAPIGateway.Utilities.IsDedicated && steamId == MyAPIGateway.Multiplayer.MyId)
            {
                ShowClientText(text);
                return;
            }

            NetworkPacket packet = new NetworkPacket { Kind = ResponsePacket, Text = text ?? string.Empty };
            MyAPIGateway.Multiplayer.SendMessageTo(NetworkChannel, MyAPIGateway.Utilities.SerializeToBinary(packet), steamId, true);
        }

        private static void ShowClientText(string text)
        {
            string[] lines = (text ?? string.Empty).Split(new[] { '\n' }, StringSplitOptions.None);
            for (int i = 0; i < lines.Length; i++)
                MyAPIGateway.Utilities.ShowMessage("Insurance", lines[i]);
        }

        private static string Money(long value)
        {
            return Math.Max(0, value).ToString("N0", CultureInfo.InvariantCulture);
        }

        private static string Percent(double value)
        {
            return InsuranceMath.Clamp01(value).ToString("P1", CultureInfo.InvariantCulture);
        }

        private static void Log(string message, Exception exception)
        {
            MyLog.Default.WriteLineAndConsole("[ShipInsurance] " + message + ": " + exception);
        }
    }
}
