using System.Collections.Generic;
using System.Xml.Serialization;
using ProtoBuf;
using VRage;
using VRage.Game;

namespace ShipInsurance
{
    public enum InsuranceDamageCause
    {
        Unknown,
        Grinding,
        Ramming,
        Weapon,
        Environment,
        Other
    }

    public enum InsuranceDamageRelationship
    {
        Unknown,
        Owner,
        Faction,
        Other,
        Environment
    }

    public sealed class InsuranceConfig
    {
        public long EnrollmentFlatFee = 0;
        public double EnrollmentValueFraction = 0.50;
        public double CancellationRefundFraction = 0.50;
        public double ClaimValueFraction = 1.00;
        public double OwnerDamageClaimValueFraction = 1.00;
        public double FactionDamageClaimValueFraction = 1.00;
        public double OtherDamageClaimValueFraction = 0.75;
        public double EnvironmentDamageClaimValueFraction = 0.75;
        public double UnknownDamageClaimValueFraction = 0.75;
        public double GrindingDamageClaimMultiplier = 1.00;
        public double RammingDamageClaimMultiplier = 1.00;
        public double WeaponDamageClaimMultiplier = 1.00;
        public double EnvironmentDamageClaimMultiplier = 1.00;
        public double OtherDamageClaimMultiplier = 1.00;
        public double UnknownDamageClaimMultiplier = 1.00;
        public double MinimumLossRatio = 0.25;
        public long MinimumClaimFee = 0;
        public long DefaultComponentPrice = 100;
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
        public bool UseEconomyFactionPricing = false;
        public bool UseDynamicRecoveryPricing = false;
        public int EconomyFriendlyReputationMin = 500;
        public int EconomyFriendlyReputationMax = 1500;
        public double EconomyMaximumFactionDiscount = 0.10;
        public long InsuranceCooldownCreditsPerSecond = 1000;
        public int InsuranceCooldownMinimumSeconds = 60;
        public int InsuranceCooldownMaximumSeconds = 86400;
        [XmlArrayItem("Component")]
        public List<InsuranceComponentPrice> ComponentPrices =
            new List<InsuranceComponentPrice>();
    }

    public sealed class InsuranceComponentPrice
    {
        public string SubtypeId;
        public long Price;
    }

    [ProtoContract]
    public sealed class InsuranceState
    {
        [ProtoMember(1)] public long NextPolicyId = 1;
        [ProtoMember(2)] public List<InsurancePolicy> Policies = new List<InsurancePolicy>();
        [ProtoMember(3)] public List<InsuranceCooldown> Cooldowns = new List<InsuranceCooldown>();
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
        [ProtoMember(19)] public long RecoveryClaimCost;
        [ProtoMember(20)] public bool RecoveryClaimCostLocked;
        [ProtoMember(21)] public bool RecoveryDynamicPrice;
        [ProtoMember(22)] public string RecoveryPricingFactionTag;
        [ProtoMember(23)] public int RecoveryPricingReputation;
        [ProtoMember(24)] public double RecoveryPricingDiscount;
        [ProtoMember(25)] public bool Consumed;
        [ProtoMember(26)] public long EnrollmentCost;
    }

    [ProtoContract]
    public sealed class InsuranceCooldown
    {
        [ProtoMember(1)] public long OwnerIdentityId;
        [ProtoMember(2)] public long ReadyUtcTicks;
        [ProtoMember(3)] public long ServiceCost;
    }

    [ProtoContract]
    public sealed class InsuredGridSnapshot
    {
        [ProtoMember(1)] public long GridEntityId;
        [ProtoMember(2)] public MyObjectBuilder_CubeGrid Blueprint;
        [ProtoMember(3)] public MyPositionAndOrientation RelativePose;
        [ProtoMember(4)] public string PersistentId;
        [ProtoMember(5)] public List<BlockLossAttribution> DamageAttributions =
            new List<BlockLossAttribution>();
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
        [ProtoMember(12)] public InsuranceDamageCause DamageCause;
        [ProtoMember(13)] public InsuranceDamageRelationship DamageRelationship;
    }

    [ProtoContract]
    public sealed class BlockLossAttribution
    {
        [ProtoMember(1)] public int X;
        [ProtoMember(2)] public int Y;
        [ProtoMember(3)] public int Z;
        [ProtoMember(4)] public List<LossAttributionBucket> Buckets =
            new List<LossAttributionBucket>();
    }

    [ProtoContract]
    public sealed class LossAttributionBucket
    {
        [ProtoMember(1)] public InsuranceDamageCause Cause;
        [ProtoMember(2)] public InsuranceDamageRelationship Relationship;
        [ProtoMember(3)] public double IntegrityLossRatio;
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
        [ProtoMember(7)] public ClaimLedger Ledger;
    }

    [ProtoContract]
    internal sealed class ClaimLedger
    {
        [ProtoMember(1)] public long PolicyId;
        [ProtoMember(2)] public string GridName;
        [ProtoMember(3)] public bool Recovery;
        [ProtoMember(4)] public long RepairValue;
        [ProtoMember(5)] public long UnrepairableValue;
        [ProtoMember(6)] public long AttributedSubtotal;
        [ProtoMember(7)] public long FinalCost;
        [ProtoMember(8)] public string FactionTag;
        [ProtoMember(9)] public int FactionReputation;
        [ProtoMember(10)] public bool DynamicRecoveryPrice;
        [ProtoMember(11)] public bool RecoveryPriceLocked;
        [ProtoMember(12)] public List<ClaimLedgerEntry> Entries =
            new List<ClaimLedgerEntry>();
        [ProtoMember(13)] public List<ClaimLedgerAdjustment> Adjustments =
            new List<ClaimLedgerAdjustment>();
        [ProtoMember(14)] public string Error;
    }

    [ProtoContract]
    internal sealed class ClaimLedgerEntry
    {
        [ProtoMember(1)] public InsuranceDamageCause Cause;
        [ProtoMember(2)] public InsuranceDamageRelationship Relationship;
        [ProtoMember(3)] public long RepairValue;
        [ProtoMember(4)] public double Rate;
        [ProtoMember(5)] public long Cost;
    }

    [ProtoContract]
    internal sealed class ClaimLedgerAdjustment
    {
        [ProtoMember(1)] public string Label;
        [ProtoMember(2)] public long Amount;
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
        [ProtoMember(12)] public long ClaimCost;
        [ProtoMember(13)] public bool Recovery;
        [ProtoMember(14)] public long TransportCost;
        [ProtoMember(15)] public long EnrollmentCost;
        [ProtoMember(16)] public double DistanceMeters;
        [ProtoMember(17)] public double LossRatio;
        [ProtoMember(18)] public string FactionTag;
        [ProtoMember(19)] public int FactionReputation;
        [ProtoMember(20)] public int FactionDiscountPercent;
        [ProtoMember(21)] public bool DynamicRecoveryPrice;
        [ProtoMember(22)] public bool RecoveryPriceLocked;
        [ProtoMember(23)] public long InsuranceCooldownReadyUtcTicks;
        [ProtoMember(24)] public long ShipValue;
    }

    internal sealed class ClaimQuote
    {
        public bool TotalLoss;
        public bool Recovery;
        public long BaselineValue;
        public long LossValue;
        public long RecoveryValue;
        public long Cost;
        public string FactionTag;
        public int FactionReputation;
        public double FactionDiscount;
        public bool DynamicRecoveryPrice;
        public bool RecoveryPriceLocked;
        public int MissingBlocks;
        public int DamagedBlocks;
        public int ConflictingBlocks;
        public long AttributedCost;
        public readonly List<RepairItem> Items = new List<RepairItem>();

        public double LossRatio => InsuranceMath.Ratio(LossValue, BaselineValue);
    }

    internal sealed class RepairItem
    {
        public InsuredGridSnapshot InsuredGrid;
        public MyObjectBuilder_CubeBlock Snapshot;
        public VRage.Game.ModAPI.IMySlimBlock Existing;
        public VRage.Game.ModAPI.IMyCubeGrid Grid;
        public long LossValue;
        public long ClaimCost;
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
        public InsuranceDamageCause DamageCause;
        public InsuranceDamageRelationship DamageRelationship;
    }
}
