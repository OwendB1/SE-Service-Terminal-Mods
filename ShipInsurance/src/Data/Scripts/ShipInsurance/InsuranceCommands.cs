using System;
using System.Globalization;
using Sandbox.ModAPI;
using VRage.Game.ModAPI;

namespace ShipInsurance
{
    internal sealed class InsuranceCommands
    {
        private const ushort NetworkChannel = 49171;
        private const string CommandPrefix = "/insurance";
        private const int CommandPacket = 1;
        private const int ResponsePacket = 2;
        private const int PolicyListRequestPacket = 3;
        private const int PolicyListResponsePacket = 4;

        private readonly InsuranceRuntime _runtime;
        private bool _active;
        private bool _isServer;
        private bool _networkRegistered;
        private bool _chatRegistered;

        internal event Action<NetworkPacket> PolicyListReceived;

        internal InsuranceCommands(InsuranceRuntime runtime)
        {
            _runtime = runtime;
        }

        internal void Start(bool isServer)
        {
            _active = true;
            _isServer = isServer;
            MyAPIGateway.Multiplayer.RegisterSecureMessageHandler(NetworkChannel, OnNetworkMessage);
            _networkRegistered = true;

            if (!MyAPIGateway.Utilities.IsDedicated)
            {
                MyAPIGateway.Utilities.MessageEnteredSender += OnChatMessage;
                _chatRegistered = true;
            }
        }

        internal void Stop()
        {
            _active = false;
            if (_chatRegistered && MyAPIGateway.Utilities != null)
                MyAPIGateway.Utilities.MessageEnteredSender -= OnChatMessage;
            if (_networkRegistered && MyAPIGateway.Multiplayer != null)
                MyAPIGateway.Multiplayer.UnregisterSecureMessageHandler(NetworkChannel, OnNetworkMessage);
            PolicyListReceived = null;
        }

        internal void RequestPolicyList(long serviceTerminalId)
        {
            NetworkPacket packet = new NetworkPacket
            {
                Kind = PolicyListRequestPacket,
                ServiceTerminalId = serviceTerminalId
            };
            if (_isServer)
                HandlePolicyListRequest(MyAPIGateway.Multiplayer.MyId, serviceTerminalId);
            else
                MyAPIGateway.Multiplayer.SendMessageToServer(NetworkChannel,
                    MyAPIGateway.Utilities.SerializeToBinary(packet), true);
        }

        internal void ExecuteTerminalCommand(string command, long policyId, long gridId,
            long serviceTerminalId)
        {
            NetworkPacket packet = new NetworkPacket
            {
                Kind = CommandPacket,
                Command = policyId > 0
                    ? command + " " + policyId.ToString(CultureInfo.InvariantCulture)
                    : command,
                ControlledGridId = gridId,
                ServiceTerminalId = serviceTerminalId
            };
            if (_isServer)
                HandleCommand(MyAPIGateway.Multiplayer.MyId, packet);
            else
                MyAPIGateway.Multiplayer.SendMessageToServer(NetworkChannel,
                    MyAPIGateway.Utilities.SerializeToBinary(packet), true);
        }

        private void OnChatMessage(ulong sender, string text, ref bool sendToOthers)
        {
            if (string.IsNullOrWhiteSpace(text) || !IsInsuranceCommand(text)) return;

            sendToOthers = false;
            string command = text.Substring(CommandPrefix.Length).Trim();
            string[] commandParts = command.Split(new[] { ' ', '\t' },
                StringSplitOptions.RemoveEmptyEntries);
            string action = commandParts.Length == 0 ? string.Empty : commandParts[0];
            if (!string.Equals(action, "reload", StringComparison.OrdinalIgnoreCase))
            {
                ShowClientText("Use a Services Terminal's Ship Insurance controls. " +
                    "Chat supports only /insurance reload.");
                return;
            }

            NetworkPacket packet = new NetworkPacket
            {
                Kind = CommandPacket,
                Command = command
            };

            try
            {
                if (_isServer)
                    HandleCommand(sender, packet);
                else
                    MyAPIGateway.Multiplayer.SendMessageToServer(NetworkChannel,
                        MyAPIGateway.Utilities.SerializeToBinary(packet), true);
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
                    else if (packet.Kind == PolicyListResponsePacket) PublishPolicyList(packet);
                }
            }
            catch (Exception exception)
            {
                Log("Network packet failed", exception);
            }
        }

        private void HandleCommand(ulong steamId, NetworkPacket packet)
        {
            IMyPlayer player = InsuranceRuntime.FindPlayer(steamId);
            if (player == null)
            {
                SendResponse(steamId, "Player identity unavailable.");
                return;
            }

            string[] parts = (packet.Command ?? string.Empty).Split(new[] { ' ', '\t' },
                StringSplitOptions.RemoveEmptyEntries);
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
                if (!_runtime.TryValidateServiceTerminalRequest(player, packet.ServiceTerminalId,
                    packet.ControlledGridId, requireGrid, out serviceGrid, out serviceError))
                {
                    SendResponse(steamId, serviceError);
                    return;
                }
            }
            else if (action != "reload")
            {
                SendResponse(steamId, "Use a Services Terminal's Ship Insurance controls. " +
                    "Chat supports only /insurance reload.");
                return;
            }

            try
            {
                switch (action)
                {
                    case "insure":
                        _runtime.InsureGrid(player, steamId, packet.ControlledGridId, serviceGrid,
                            packet.ServiceTerminalId);
                        break;
                    case "status":
                        _runtime.ShowStatus(player, steamId, packet.ControlledGridId,
                            requestedPolicyId, packet.ServiceTerminalId);
                        break;
                    case "claim":
                        _runtime.Claim(player, steamId, packet.ControlledGridId,
                            requestedPolicyId, packet.ServiceTerminalId);
                        break;
                    case "expedite":
                        _runtime.ExpediteRecovery(player, steamId, requestedPolicyId,
                            packet.ServiceTerminalId);
                        break;
                    case "history":
                        _runtime.ShowHistory(player, steamId, packet.ControlledGridId,
                            requestedPolicyId);
                        break;
                    case "cancel":
                        _runtime.CancelPolicy(player, steamId, packet.ControlledGridId,
                            requestedPolicyId);
                        break;
                    case "reload":
                        _runtime.ReloadConfig(player, steamId);
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
            IMyPlayer player = InsuranceRuntime.FindPlayer(steamId);
            if (player == null) return;

            IMyCubeGrid ignoredGrid;
            string error;
            if (!_runtime.TryValidateServiceTerminalRequest(player, serviceTerminalId, 0, false,
                out ignoredGrid, out error))
            {
                SendResponse(steamId, error);
                return;
            }

            NetworkPacket response = new NetworkPacket
            {
                Kind = PolicyListResponsePacket,
                ServiceTerminalId = serviceTerminalId,
                Policies = _runtime.BuildPolicySummaries(player, serviceTerminalId)
            };

            if (!MyAPIGateway.Utilities.IsDedicated && steamId == MyAPIGateway.Multiplayer.MyId)
                MyAPIGateway.Utilities.InvokeOnGameThread(delegate { PublishPolicyList(response); });
            else
                MyAPIGateway.Multiplayer.SendMessageTo(NetworkChannel,
                    MyAPIGateway.Utilities.SerializeToBinary(response), steamId, true);
        }

        private void PublishPolicyList(NetworkPacket packet)
        {
            Action<NetworkPacket> handler = PolicyListReceived;
            if (handler != null) handler(packet);
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

        internal void SendResponse(ulong steamId, string text)
        {
            if (!MyAPIGateway.Utilities.IsDedicated && steamId == MyAPIGateway.Multiplayer.MyId)
            {
                ShowClientText(text);
                return;
            }

            NetworkPacket packet = new NetworkPacket
            {
                Kind = ResponsePacket,
                Text = text ?? string.Empty
            };
            MyAPIGateway.Multiplayer.SendMessageTo(NetworkChannel,
                MyAPIGateway.Utilities.SerializeToBinary(packet), steamId, true);
        }

        internal static void ShowClientText(string text)
        {
            string[] lines = (text ?? string.Empty).Split(new[] { '\n' },
                StringSplitOptions.None);
            for (int i = 0; i < lines.Length; i++)
                MyAPIGateway.Utilities.ShowMessage("Insurance", lines[i]);
        }

        internal static void Log(string message, Exception exception)
        {
            InsuranceRuntime.Log(message, exception);
        }
    }
}
