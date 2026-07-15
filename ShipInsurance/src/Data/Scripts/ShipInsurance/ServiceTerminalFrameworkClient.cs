using System;
using Sandbox.ModAPI;
using VRage;
using VRage.Game.ModAPI;
using VRage.ModAPI;

namespace ShipInsurance
{
    using Registration = MyTuple<string, string,
        Action<IMyTerminalBlock, IMyEntity, bool, Action>>;

    internal sealed class ServiceTerminalFrameworkClient
    {
        private const long RegistrationChannel = 0x5354465245470001L;
        private const long ReadyChannel = 0x5354465244590001L;
        private const string ServiceId = "OwendB1.ShipInsurance";

        private readonly Action<IMyTerminalBlock, IMyEntity, bool, Action> _setActive;

        internal ServiceTerminalFrameworkClient()
        {
            _setActive = SetActive;
        }

        internal void Start()
        {
            MyAPIGateway.Utilities.RegisterMessageHandler(ReadyChannel, OnFrameworkReady);
            Register();
        }

        internal void Stop()
        {
            MyAPIGateway.Utilities.UnregisterMessageHandler(ReadyChannel, OnFrameworkReady);
            MyAPIGateway.Utilities.SendModMessage(RegistrationChannel, ServiceId);
        }

        private void OnFrameworkReady(object message)
        {
            if (message is bool && (bool)message) Register();
        }

        private void Register()
        {
            MyAPIGateway.Utilities.SendModMessage(RegistrationChannel,
                new Registration(ServiceId, "Ship Insurance", _setActive));
            InsuranceRuntime.Log("Service Terminal Framework registration requested");
        }

        private static void SetActive(IMyTerminalBlock terminal, IMyEntity user,
            bool active, Action closed)
        {
            if (active)
                InsuranceRichHud.Open(terminal, closed);
            else
                InsuranceRichHud.Close();
        }
    }
}
