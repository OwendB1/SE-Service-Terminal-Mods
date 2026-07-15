using System;
using System.Collections.Generic;
using System.Globalization;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Interfaces.Terminal;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRage.Utils;
using VRageMath;

namespace ShipInsurance
{
    public sealed partial class InsuranceSession
    {
        private const double ServiceGridDiscoveryRange = 250.0;
        private const double ServiceTerminalUseDistance = 15.0;
        private readonly List<IMyTerminalControl> _serviceTerminalControls = new List<IMyTerminalControl>();
        private readonly List<PolicySummary> _servicePolicies = new List<PolicySummary>();
        private readonly Dictionary<long, long> _serviceChoicePolicies = new Dictionary<long, long>();
        private readonly Dictionary<long, long> _serviceChoiceGrids = new Dictionary<long, long>();
        private long _selectedServiceGridId;
        private long _selectedPolicyId;
        private long _selectedServiceChoice;
        private long _servicePolicyTerminalId;
        private long _lastServicePolicyRequestTicks;
        private IMyTerminalControlCombobox _serviceGridSelector;
        private IMyTerminalControlButton _serviceExpediteButton;
        private bool _serviceTerminalControlsRegistered;

        private void RegisterServiceTerminalControls()
        {
            if (_serviceTerminalControlsRegistered || MyAPIGateway.TerminalControls == null) return;

            IMyTerminalControlSeparator separator =
                MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSeparator, IMyFunctionalBlock>(
                    "ShipInsurance_Separator");
            AddServiceTerminalControl(separator);

            IMyTerminalControlLabel label =
                MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlLabel, IMyFunctionalBlock>(
                    "ShipInsurance_Label");
            label.Label = MyStringId.GetOrCompute("Ship Insurance");
            AddServiceTerminalControl(label);

            _serviceGridSelector =
                MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlCombobox, IMyFunctionalBlock>(
                    "ShipInsurance_Grid");
            _serviceGridSelector.Title = MyStringId.GetOrCompute("Insurance target");
            _serviceGridSelector.Tooltip = MyStringId.GetOrCompute(
                "Select an existing policy or a nearby uninsured mechanical group.");
            _serviceGridSelector.ComboBoxContent = FillServiceChoices;
            _serviceGridSelector.Getter = GetSelectedServiceChoice;
            _serviceGridSelector.Setter = SetSelectedServiceChoice;
            _serviceGridSelector.Enabled = CanAccessServiceTerminal;
            AddServiceTerminalControl(_serviceGridSelector);

            IMyTerminalControlButton refresh = CreateServiceButton("Refresh", "Refresh insurance targets",
                "Refresh policies and rescan nearby uninsured groups.", RefreshServiceChoices);
            refresh.Enabled = CanAccessServiceTerminal;
            AddServiceTerminalControl(refresh);
            IMyTerminalControlButton status = CreateServiceButton("Status", "Policy status / quote",
                "Show loss, claim price, and any remote recovery countdown or expedite quote.", delegate(IMyTerminalBlock block)
                {
                    SendServiceTerminalCommand(block, "status");
                });
            status.Enabled = CanUseSelectedPolicy;
            AddServiceTerminalControl(status);
            IMyTerminalControlButton insure = CreateServiceButton("Insure", "Insure snapshot",
                "Pay enrollment and snapshot the selected mechanical group as its truth state.", delegate(IMyTerminalBlock block)
                {
                    SendServiceTerminalCommand(block, "insure");
                });
            insure.Enabled = CanUseSelectedGroup;
            AddServiceTerminalControl(insure);
            IMyTerminalControlButton claim = CreateServiceButton("Claim", "Claim / recover",
                "Repair locally, order remote recovery, or deploy an arrived recovery.", delegate(IMyTerminalBlock block)
                {
                    SendServiceTerminalCommand(block, "claim");
                });
            claim.Enabled = CanUseSelectedPolicy;
            AddServiceTerminalControl(claim);
            _serviceExpediteButton = CreateServiceButton("Expedite", "Expedite remote recovery",
                "Select an en-route recovery to see its current expedite price.", delegate(IMyTerminalBlock block)
                {
                    SendServiceTerminalCommand(block, "expedite");
                });
            _serviceExpediteButton.Enabled = CanExpediteSelectedPolicy;
            AddServiceTerminalControl(_serviceExpediteButton);
            IMyTerminalControlButton history = CreateServiceButton("History", "Damage history",
                "Show recent damage/removal attribution for selected group.", delegate(IMyTerminalBlock block)
                {
                    SendServiceTerminalCommand(block, "history");
                });
            history.Enabled = CanUseSelectedPolicy;
            AddServiceTerminalControl(history);
            IMyTerminalControlButton cancel = CreateServiceButton("Cancel", "Cancel selected policy",
                "Cancel selected group's policy without an enrollment refund.", delegate(IMyTerminalBlock block)
                {
                    SendServiceTerminalCommand(block, "cancel");
                });
            cancel.Enabled = CanUseSelectedPolicy;
            AddServiceTerminalControl(cancel);

            _serviceTerminalControlsRegistered = true;
        }

        private void AddServiceTerminalControl(IMyTerminalControl control)
        {
            control.SupportsMultipleBlocks = false;
            control.Visible = IsServicesTerminal;
            MyAPIGateway.TerminalControls.AddControl<IMyFunctionalBlock>(control);
            _serviceTerminalControls.Add(control);
        }

        private IMyTerminalControlButton CreateServiceButton(string id, string title, string tooltip,
            Action<IMyTerminalBlock> action)
        {
            IMyTerminalControlButton button =
                MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlButton, IMyFunctionalBlock>(
                    "ShipInsurance_" + id);
            button.Title = MyStringId.GetOrCompute(title);
            button.Tooltip = MyStringId.GetOrCompute(tooltip);
            button.Action = action;
            button.Enabled = CanAccessServiceTerminal;
            return button;
        }

        private void UnregisterServiceTerminalControls()
        {
            if (!_serviceTerminalControlsRegistered || MyAPIGateway.TerminalControls == null) return;

            for (int i = 0; i < _serviceTerminalControls.Count; i++)
                MyAPIGateway.TerminalControls.RemoveControl<IMyFunctionalBlock>(_serviceTerminalControls[i]);

            _serviceTerminalControls.Clear();
            _servicePolicies.Clear();
            _serviceChoicePolicies.Clear();
            _serviceChoiceGrids.Clear();
            _serviceGridSelector = null;
            _serviceExpediteButton = null;
            _serviceTerminalControlsRegistered = false;
        }

        private void FillServiceChoices(List<MyTerminalControlComboBoxItem> items)
        {
            RebuildServiceChoices(items);
        }

        private long GetSelectedServiceChoice(IMyTerminalBlock block)
        {
            RequestServicePolicyList(block, false);
            RebuildServiceChoices(new List<MyTerminalControlComboBoxItem>());
            return _selectedServiceChoice;
        }

        private void SetSelectedServiceChoice(IMyTerminalBlock block, long choice)
        {
            _selectedServiceChoice = choice;
            long value;
            _selectedPolicyId = _serviceChoicePolicies.TryGetValue(choice, out value) ? value : 0;
            _selectedServiceGridId = _serviceChoiceGrids.TryGetValue(choice, out value) ? value : 0;
            UpdateExpeditePresentation();
        }

        private void RebuildServiceChoices(List<MyTerminalControlComboBoxItem> items)
        {
            _serviceChoicePolicies.Clear();
            _serviceChoiceGrids.Clear();
            long nextChoice = 1;
            long selectedChoice = 0;

            for (int i = 0; i < _servicePolicies.Count; i++)
            {
                PolicySummary policy = _servicePolicies[i];
                long choice = nextChoice++;
                _serviceChoicePolicies[choice] = policy.PolicyId;
                if (policy.PolicyId == _selectedPolicyId) selectedChoice = choice;
                long remaining = InsuranceMath.RemainingSeconds(policy.RecoveryReadyUtcTicks,
                    DateTime.UtcNow.Ticks);
                string state = policy.RecoveryReadyUtcTicks > 0
                    ? remaining > 0 ? "recovery " + Duration(remaining) : "recovery ready"
                    : policy.TotalLoss ? "total loss" : policy.Remote ? "remote recovery" : "active";
                items.Add(new MyTerminalControlComboBoxItem
                {
                    Key = choice,
                    Value = MyStringId.GetOrCompute("Policy #" + policy.PolicyId + " " + policy.GridName +
                        " [" + state + "]")
                });
            }

            List<IMyCubeGrid> grids = FindNearbyOwnedGrids();
            IMyPlayer player = MyAPIGateway.Session == null ? null : MyAPIGateway.Session.Player;
            IMyFunctionalBlock terminal = FindServiceTerminal(_servicePolicyTerminalId);
            Vector3D servicePosition = terminal != null
                ? terminal.GetPosition()
                : player == null ? Vector3D.Zero : player.GetPosition();
            for (int i = 0; i < grids.Count; i++)
            {
                IMyCubeGrid grid = grids[i];
                long choice = nextChoice++;
                _serviceChoiceGrids[choice] = grid.EntityId;
                if (grid.EntityId == _selectedServiceGridId) selectedChoice = choice;
                string name = string.IsNullOrWhiteSpace(grid.CustomName) ? grid.DisplayName : grid.CustomName;
                double distance = Vector3D.Distance(servicePosition, grid.WorldAABB.Center);
                items.Add(new MyTerminalControlComboBoxItem
                {
                    Key = choice,
                    Value = MyStringId.GetOrCompute("New policy: " + name + " (" +
                        distance.ToString("0", CultureInfo.InvariantCulture) + " m)")
                });
            }

            if (nextChoice == 1)
            {
                items.Add(new MyTerminalControlComboBoxItem
                {
                    Key = 0,
                    Value = MyStringId.GetOrCompute("No policies or owned groups available")
                });
                _selectedServiceChoice = 0;
                _selectedPolicyId = 0;
                _selectedServiceGridId = 0;
                return;
            }

            if (selectedChoice == 0) selectedChoice = 1;
            SetSelectedServiceChoice(null, selectedChoice);
        }

        private List<IMyCubeGrid> FindNearbyOwnedGrids()
        {
            List<IMyCubeGrid> grids = new List<IMyCubeGrid>();
            IMyPlayer player = MyAPIGateway.Session == null ? null : MyAPIGateway.Session.Player;
            if (player == null) return grids;

            IMyFunctionalBlock terminal = FindServiceTerminal(_servicePolicyTerminalId);
            Vector3D center = terminal == null ? player.GetPosition() : terminal.GetPosition();
            BoundingSphereD sphere = new BoundingSphereD(center, ServiceGridDiscoveryRange);
            List<IMyEntity> entities = MyAPIGateway.Entities.GetEntitiesInSphere(ref sphere);
            HashSet<long> seenGroups = new HashSet<long>();
            for (int i = 0; i < entities.Count; i++)
            {
                IMyCubeGrid grid = entities[i] as IMyCubeGrid;
                if (grid == null || grid.Closed) continue;
                List<IMyCubeGrid> group = GetMechanicalGroup(grid);
                if (group.Count == 0) continue;
                IMyCubeGrid anchor = group[0];
                if (!anchor.BigOwners.Contains(player.IdentityId) || !seenGroups.Add(anchor.EntityId) ||
                    IsInsuredServiceGroup(group)) continue;
                grids.Add(anchor);
            }

            grids.Sort(delegate(IMyCubeGrid left, IMyCubeGrid right)
            {
                string leftName = string.IsNullOrWhiteSpace(left.CustomName) ? left.DisplayName : left.CustomName;
                string rightName = string.IsNullOrWhiteSpace(right.CustomName) ? right.DisplayName : right.CustomName;
                return string.Compare(leftName, rightName, StringComparison.OrdinalIgnoreCase);
            });
            return grids;
        }

        private bool IsInsuredServiceGroup(List<IMyCubeGrid> group)
        {
            for (int policyIndex = 0; policyIndex < _servicePolicies.Count; policyIndex++)
            {
                long insuredGridId = _servicePolicies[policyIndex].SelectionGridId;
                if (insuredGridId == 0) continue;
                for (int gridIndex = 0; gridIndex < group.Count; gridIndex++)
                    if (group[gridIndex].EntityId == insuredGridId) return true;
            }

            return false;
        }

        private bool CanUseSelectedGroup(IMyTerminalBlock block)
        {
            if (!CanAccessServiceTerminal(block)) return false;
            GetSelectedServiceChoice(block);
            return _selectedServiceGridId != 0;
        }

        private bool CanUseSelectedPolicy(IMyTerminalBlock block)
        {
            if (!CanAccessServiceTerminal(block)) return false;
            GetSelectedServiceChoice(block);
            return _selectedPolicyId != 0;
        }

        private bool CanExpediteSelectedPolicy(IMyTerminalBlock block)
        {
            if (!CanUseSelectedPolicy(block)) return false;
            PolicySummary policy = FindSelectedServicePolicy();
            return policy != null && policy.RecoveryReadyUtcTicks > DateTime.UtcNow.Ticks &&
                   policy.RecoveryTerminalEntityId == block.EntityId && !policy.RecoveryExpedited &&
                   policy.ExpediteReductionPercent > 0;
        }

        private PolicySummary FindSelectedServicePolicy()
        {
            for (int i = 0; i < _servicePolicies.Count; i++)
                if (_servicePolicies[i].PolicyId == _selectedPolicyId) return _servicePolicies[i];
            return null;
        }

        private void UpdateExpeditePresentation()
        {
            if (_serviceExpediteButton == null) return;
            PolicySummary policy = FindSelectedServicePolicy();
            if (policy != null && policy.RecoveryReadyUtcTicks > DateTime.UtcNow.Ticks &&
                policy.RecoveryTerminalEntityId == _servicePolicyTerminalId &&
                !policy.RecoveryExpedited && policy.ExpediteReductionPercent > 0)
            {
                _serviceExpediteButton.Tooltip = MyStringId.GetOrCompute("Pay " +
                    Money(policy.ExpeditePrice) + " SC to reduce current remaining time by " +
                    policy.ExpediteReductionPercent + "% (one use per recovery).");
            }
            else
            {
                _serviceExpediteButton.Tooltip = MyStringId.GetOrCompute(
                    "Available once while a remote recovery is en route from this terminal.");
            }
        }

        private bool CanAccessServiceTerminal(IMyTerminalBlock block)
        {
            IMyPlayer player = MyAPIGateway.Session == null ? null : MyAPIGateway.Session.Player;
            IMyFunctionalBlock functional = block as IMyFunctionalBlock;
            if (player == null || functional == null || !IsServicesTerminal(block) ||
                !functional.IsFunctional || !functional.Enabled) return false;
            if (block.GetUserRelationToOwner(player.IdentityId) == MyRelationsBetweenPlayerAndBlock.Enemies) return false;
            if (Vector3D.DistanceSquared(player.GetPosition(), block.GetPosition()) >
                ServiceTerminalUseDistance * ServiceTerminalUseDistance) return false;
            return true;
        }

        private void RefreshServiceChoices(IMyTerminalBlock block)
        {
            _lastServicePolicyRequestTicks = 0;
            RequestServicePolicyList(block, true);
            if (_serviceGridSelector != null)
            {
                _serviceGridSelector.RedrawControl();
                _serviceGridSelector.UpdateVisual();
            }
        }

        private void RequestServicePolicyList(IMyTerminalBlock terminal, bool force)
        {
            if (terminal == null || !CanAccessServiceTerminal(terminal)) return;

            long now = DateTime.UtcNow.Ticks;
            if (!force && _servicePolicyTerminalId == terminal.EntityId &&
                now - _lastServicePolicyRequestTicks < TimeSpan.TicksPerSecond * 2) return;

            _servicePolicyTerminalId = terminal.EntityId;
            _lastServicePolicyRequestTicks = now;
            NetworkPacket packet = new NetworkPacket
            {
                Kind = PolicyListRequestPacket,
                ServiceTerminalId = terminal.EntityId
            };

            try
            {
                if (_isServer)
                    HandlePolicyListRequest(MyAPIGateway.Multiplayer.MyId, terminal.EntityId);
                else
                    MyAPIGateway.Multiplayer.SendMessageToServer(NetworkChannel,
                        MyAPIGateway.Utilities.SerializeToBinary(packet), true);
            }
            catch (Exception exception)
            {
                ShowClientText("Insurance target refresh failed: " + exception.Message);
                Log("Insurance target refresh failed", exception);
            }
        }

        private void ApplyServicePolicyList(NetworkPacket packet)
        {
            if (packet == null || packet.ServiceTerminalId != _servicePolicyTerminalId) return;

            _servicePolicies.Clear();
            if (packet.Policies != null) _servicePolicies.AddRange(packet.Policies);

            bool selectedPolicyExists = false;
            for (int i = 0; i < _servicePolicies.Count; i++)
                if (_servicePolicies[i].PolicyId == _selectedPolicyId) selectedPolicyExists = true;
            if (!selectedPolicyExists)
            {
                _selectedPolicyId = 0;
                _selectedServiceChoice = 0;
            }

            if (_serviceGridSelector != null)
            {
                _serviceGridSelector.RedrawControl();
                _serviceGridSelector.UpdateVisual();
            }
            UpdateExpeditePresentation();
        }

        private void SendServiceTerminalCommand(IMyTerminalBlock terminal, string command)
        {
            GetSelectedServiceChoice(terminal);
            bool insuring = command == "insure";
            long policyId = insuring ? 0 : _selectedPolicyId;
            long gridId = insuring ? _selectedServiceGridId : 0;
            if (insuring && gridId == 0)
            {
                ShowClientText("Select a 'New policy' mechanical group first.");
                return;
            }

            if (!insuring && policyId == 0)
            {
                ShowClientText("Select an existing policy first.");
                return;
            }

            NetworkPacket packet = new NetworkPacket
            {
                Kind = CommandPacket,
                Command = policyId > 0 ? command + " " + policyId.ToString(CultureInfo.InvariantCulture) : command,
                ControlledGridId = gridId,
                ServiceTerminalId = terminal.EntityId
            };

            try
            {
                if (_isServer)
                    HandleCommand(MyAPIGateway.Multiplayer.MyId, packet);
                else
                    MyAPIGateway.Multiplayer.SendMessageToServer(NetworkChannel,
                        MyAPIGateway.Utilities.SerializeToBinary(packet), true);
            }
            catch (Exception exception)
            {
                ShowClientText("Service terminal action failed: " + exception.Message);
                Log("Service terminal action failed", exception);
            }
        }
    }
}
