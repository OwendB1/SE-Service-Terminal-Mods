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
        private InsuranceRichHud _richHud;
        private readonly InsuranceTerminalModel _terminalModel = new InsuranceTerminalModel();

        public override void LoadData()
        {
            _terminalModel.Apply(ModContext.ModPath);
        }

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
                _richHud = new InsuranceRichHud(_terminalControls);
                _richHud.Start();
            }

            InsuranceRuntime.Log("Initialized (server=" + isServer + ", terminalControls=" +
                (_terminalControls != null) + ", richHud=" + (_richHud != null) + ")");
        }

        public override void UpdateAfterSimulation()
        {
            if (_runtime != null) _runtime.Update();
            if (_richHud != null) _richHud.Update();
        }

        public override void SaveData()
        {
            if (_runtime != null) _runtime.Save();
        }

        protected override void UnloadData()
        {
            if (_richHud != null) _richHud.Stop();
            if (_terminalControls != null) _terminalControls.Stop();
            if (_commands != null) _commands.Stop();
            if (_runtime != null) _runtime.Stop();
            _terminalControls = null;
            _richHud = null;
            _commands = null;
            _runtime = null;
            _terminalModel.Restore();
            base.UnloadData();
        }
    }
}
