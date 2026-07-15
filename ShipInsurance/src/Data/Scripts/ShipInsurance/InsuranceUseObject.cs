using Sandbox.Game;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.Entity.UseObject;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRage.Utils;

namespace ShipInsurance
{
    [MyUseObject("shipinsurance")]
    public sealed class InsuranceUseObject : MyUseObjectBase
    {
        private readonly IMyEntity _owner;

        public InsuranceUseObject(IMyEntity owner, string dummyName, IMyModelDummy dummyData, uint key)
            : base(owner, dummyData)
        {
            _owner = owner;
            InsuranceRuntime.Log("Insurance detector initialized (terminal=" + owner.EntityId +
                ", dummy=" + dummyName + ")");
        }

        public override UseActionEnum PrimaryAction => UseActionEnum.Manipulate;
        public override UseActionEnum SecondaryAction => UseActionEnum.None;
        public override UseActionEnum SupportedActions => PrimaryAction;

        public override MyActionDescription GetActionInfo(UseActionEnum actionEnum)
        {
            if (actionEnum != UseActionEnum.Manipulate) return default(MyActionDescription);
            return new MyActionDescription
            {
                Text = MyStringId.GetOrCompute("Press {0} to open Ship Insurance"),
                FormatParams = new[]
                {
                    "[" + MyAPIGateway.Input.GetGameControl(MyControlsSpace.USE) + "]"
                },
                IsTextControlHint = true
            };
        }

        public override void Use(UseActionEnum actionEnum, IMyEntity user)
        {
            if (actionEnum != UseActionEnum.Manipulate) return;
            InsuranceRuntime.Log("Insurance detector used (terminal=" + _owner.EntityId + ")");
            InsuranceRichHud.Open(_owner as IMyTerminalBlock);
        }
    }
}
