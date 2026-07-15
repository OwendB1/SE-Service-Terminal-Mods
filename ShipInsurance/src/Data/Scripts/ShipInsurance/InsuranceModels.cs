using System.Collections.Generic;
using ProtoBuf;
using VRage;
using VRage.Game;

namespace ShipInsurance
{
    public sealed class InsuranceConfig
    {
        public long EnrollmentFlatFee = 0;
        public double EnrollmentValueFraction = 0.50;
        public double ClaimValueFraction = 1.00;
        public double MinimumLossRatio = 0.25;
        public long MinimumClaimFee = 0;
        public long UnknownComponentValue = 100;
        public long UnknownBlockValue = 1000;
        public int MaxPoliciesPerPlayer = 5;
        public int MaxIncidentLogEntries = 100;
        public double TotalLossExtraClearance = 10.0;
        public bool AllowTotalLossRespawn = true;
        public bool RequireGridOwner = true;
        public bool RequireStationaryGridForClaim = true;
        public double MaximumClaimLinearSpeed = 1.0;
        public long RemoteRecoveryFeePerKilometer = 1000;
        public double RemoteRecoverySecondsPerKilometer = 1.0;
        public int RemoteRecoveryMinimumSeconds = 60;
        public int RemoteRecoveryMaximumSeconds = 3600;
        public long RemoteRecoveryExpediteCostPerSecond = 1000;
        public double RemoteRecoveryExpediteFactor = 0.5;
    }

    [ProtoContract]
    public sealed class InsuranceState
    {
        [ProtoMember(1)] public long NextPolicyId = 1;
        [ProtoMember(2)] public List<InsurancePolicy> Policies = new List<InsurancePolicy>();
    }

    [ProtoContract]
    public sealed class InsurancePolicy
    {
        [ProtoMember(1)] public long PolicyId;
        [ProtoMember(2)] public long GridEntityId;
        [ProtoMember(3)] public long OwnerIdentityId;
        [ProtoMember(4)] public ulong OwnerSteamId;
        [ProtoMember(5)] public string GridName;
        [ProtoMember(6)] public long CreatedUtcTicks;
        [ProtoMember(7)] public long BaselineValue;
        [ProtoMember(8)] public MyObjectBuilder_CubeGrid Blueprint;
        [ProtoMember(9)] public MyPositionAndOrientation LastKnownPose;
        [ProtoMember(10)] public double ClearanceRadius;
        [ProtoMember(11)] public List<IncidentRecord> Incidents = new List<IncidentRecord>();
        [ProtoMember(12)] public long LastClaimUtcTicks;
        [ProtoMember(13)] public List<InsuredGridSnapshot> Grids = new List<InsuredGridSnapshot>();
        [ProtoMember(14)] public long RecoveryTerminalEntityId;
        [ProtoMember(15)] public long RecoveryReadyUtcTicks;
        [ProtoMember(16)] public double RecoveryDistanceMeters;
        [ProtoMember(17)] public long RecoveryTransportFee;
        [ProtoMember(18)] public bool RecoveryExpedited;
    }

    [ProtoContract]
    public sealed class InsuredGridSnapshot
    {
        [ProtoMember(1)] public long GridEntityId;
        [ProtoMember(2)] public MyObjectBuilder_CubeGrid Blueprint;
        [ProtoMember(3)] public MyPositionAndOrientation RelativePose;
    }

    [ProtoContract]
    public sealed class IncidentRecord
    {
        [ProtoMember(1)] public long UtcTicks;
        [ProtoMember(2)] public string EventType;
        [ProtoMember(3)] public int X;
        [ProtoMember(4)] public int Y;
        [ProtoMember(5)] public int Z;
        [ProtoMember(6)] public long AttackerEntityId;
        [ProtoMember(7)] public long AttackerIdentityId;
        [ProtoMember(8)] public string AttackerName;
        [ProtoMember(9)] public string DamageType;
        [ProtoMember(10)] public float DamageAmount;
        [ProtoMember(11)] public int EventCount = 1;
    }

    [ProtoContract]
    internal sealed class NetworkPacket
    {
        [ProtoMember(1)] public int Kind;
        [ProtoMember(2)] public string Command;
        [ProtoMember(3)] public long ControlledGridId;
        [ProtoMember(4)] public string Text;
        [ProtoMember(5)] public long ServiceTerminalId;
        [ProtoMember(6)] public List<PolicySummary> Policies;
    }

    [ProtoContract]
    internal sealed class PolicySummary
    {
        [ProtoMember(1)] public long PolicyId;
        [ProtoMember(2)] public string GridName;
        [ProtoMember(3)] public long SelectionGridId;
        [ProtoMember(4)] public bool TotalLoss;
        [ProtoMember(5)] public bool Remote;
        [ProtoMember(6)] public long RecoveryReadyUtcTicks;
        [ProtoMember(7)] public long RecoveryTerminalEntityId;
        [ProtoMember(8)] public double RecoveryDistanceMeters;
        [ProtoMember(9)] public long ExpeditePrice;
        [ProtoMember(10)] public int ExpediteReductionPercent;
        [ProtoMember(11)] public bool RecoveryExpedited;
    }

    internal sealed class ClaimQuote
    {
        public bool TotalLoss;
        public bool Recovery;
        public long BaselineValue;
        public long LossValue;
        public long Cost;
        public int MissingBlocks;
        public int DamagedBlocks;
        public int ConflictingBlocks;
        public readonly List<RepairItem> Items = new List<RepairItem>();

        public double LossRatio
        {
            get { return InsuranceMath.Ratio(LossValue, BaselineValue); }
        }
    }

    internal sealed class RepairItem
    {
        public MyObjectBuilder_CubeBlock Snapshot;
        public VRage.Game.ModAPI.IMySlimBlock Existing;
        public VRage.Game.ModAPI.IMyCubeGrid Grid;
        public long LossValue;
        public double IntegrityDelta;
        public bool Missing;
    }

    internal sealed class DamageAttribution
    {
        public long UtcTicks;
        public long AttackerEntityId;
        public long AttackerIdentityId;
        public string AttackerName;
        public string DamageType;
    }
}
