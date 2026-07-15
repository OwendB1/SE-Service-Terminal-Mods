using Sandbox.ModAPI;
using VRage.Game.Components;

namespace ShipInsurance
{
    [MySessionComponentDescriptor(MyUpdateOrder.AfterSimulation)]
    public sealed class InsuranceSession : MySessionComponentBase
    {
        private InsuranceRuntime _runtime;
        private InsuranceCommands _commands;
        private InsuranceTerminalControls _terminalControls;

        public override void BeforeStart()
        {
#if DEBUG
            InsuranceMath.SelfTest();
#endif
            bool isServer = MyAPIGateway.Multiplayer.IsServer;
            _runtime = new InsuranceRuntime();
            _commands = new InsuranceCommands(_runtime);
            _runtime.Start(isServer, _commands.SendResponse);
            _commands.Start(isServer);

            if (!MyAPIGateway.Utilities.IsDedicated)
            {
                _terminalControls = new InsuranceTerminalControls(_commands);
                _terminalControls.Register();
            }
        }

        public override void UpdateAfterSimulation()
        {
            if (_runtime != null) _runtime.Update();
        }

        public override void SaveData()
        {
            if (_runtime != null) _runtime.Save();
        }

        protected override void UnloadData()
        {
            if (_terminalControls != null) _terminalControls.Stop();
            if (_commands != null) _commands.Stop();
            if (_runtime != null) _runtime.Stop();
            _terminalControls = null;
            _commands = null;
            _runtime = null;
            base.UnloadData();
        }
    }
}
