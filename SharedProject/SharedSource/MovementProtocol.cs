using Barotrauma.Networking;

namespace NoMoreRubberbanding;

internal enum AuthorityMode : byte { Server, Client }
internal enum TakeoverReason : byte { Initial, Unavailable, Timeout, Teleport, Recovery, Dragged, Released }

internal enum MovementHeader : byte
{
    Hello = 0,
    Control = 1,
    Heartbeat = 2,
    Pose = 3,
    ActorControl = 4,
    Interaction = 5,
    Action = 6,
    ControlAck = 7,
    Snapshot = 8,
    Intent = 9
}

internal static class MovementPolicy
{
    public const byte Version = 3;
    public const int MaxPoseLimbs = 64;
    public const float MaxLimbOffset = 32;
    public const float MaxAngularVelocity = 64;
    public const double SendInterval = 1.0 / 30.0;
    public const double SnapshotTimeout = 2.0;
    public const double ControlTimeout = 5.0;
    public const double InputTimeout = 0.5;
    public const double TeleportHold = 0.5;
    public const int MaxPendingActions = 128;
    public const ushort Facing = 0x10, RagdollKey = 0x800;
    public const ushort EdgeMask = 0x80 | 0x1000 | 0x2000 | 0x4000;
    public const ushort HeldActionMask = 0x100 | 0x400 | 0x8000;
    public const ushort ActionMask = EdgeMask | HeldActionMask;

    public static ushort ChooseWriter(ushort self, ushort dragger, bool beingDragged, bool selfReady, bool draggerReady) =>
        beingDragged ? (draggerReady ? dragger : (ushort)0) : (selfReady ? self : (ushort)0);
    public static bool CanWriteBody(ushort confirmedWriter, ushort sender, ushort actualWriter) =>
        confirmedWriter != 0 && confirmedWriter == sender && confirmedWriter == actualWriter;
    public static bool CanWriteActions(ushort body, ushort sender) => body != 0 && body == sender;
    public static bool MatchesIdentity(ushort expectedCharacter, ulong expectedNonce, ulong expectedEpoch,
        ushort character, ulong nonce, ulong epoch) =>
        expectedCharacter == character && expectedNonce == nonce && expectedEpoch == epoch;
    public static bool MoreRecent(uint value, uint previous) => unchecked((int)(value - previous)) > 0;
    public static bool Expired(double now, double receivedAt, double timeout) => now - receivedAt > timeout;
    public static bool RecoveryAllowed(double now, double holdUntil) => now >= holdUntil;
    public static bool Valid(float value, float limit) => float.IsFinite(value) && Math.Abs(value) <= limit;
    public static ushort AllowedActions(ushort flags, bool canAct, bool reverseDrag) =>
        !canAct ? (ushort)0 : reverseDrag ? (ushort)(flags & ~(0x80 | 0x2000)) : flags;
    public static bool CanReconcile(uint pendingAction, uint processedAction) => pendingAction == processedAction;
}

internal readonly record struct LimbPose(ushort Index, Vector2 Offset, Vector2 Velocity, float Rotation, float AngularVelocity)
{
    public bool IsValid => MovementPolicy.Valid(Offset.X, MovementPolicy.MaxLimbOffset) &&
        MovementPolicy.Valid(Offset.Y, MovementPolicy.MaxLimbOffset) &&
        MovementPolicy.Valid(Velocity.X, NetConfig.MaxPhysicsBodyVelocity) &&
        MovementPolicy.Valid(Velocity.Y, NetConfig.MaxPhysicsBodyVelocity) &&
        MovementPolicy.Valid(Rotation, MathHelper.Pi) && MovementPolicy.Valid(AngularVelocity, MovementPolicy.MaxAngularVelocity);
}

internal readonly record struct MovementSample(
    uint Sequence, ushort SubmarineId, ushort HullId, Vector2 Position, Vector2 Velocity, Vector2 TargetMovement,
    float Rotation, float AngularVelocity, bool FacingLeft, bool IgnorePlatforms, bool Eligible,
    LimbPose[]? Limbs = null, bool ColliderEnabled = true, bool FixedRotation = false, bool Ragdolled = false, byte ColliderIndex = 0)
{
    public bool IsValid =>
        MovementPolicy.Valid(Position.X, 1_000_000) && MovementPolicy.Valid(Position.Y, 1_000_000) &&
        MovementPolicy.Valid(Velocity.X, NetConfig.MaxPhysicsBodyVelocity) &&
        MovementPolicy.Valid(Velocity.Y, NetConfig.MaxPhysicsBodyVelocity) &&
        MovementPolicy.Valid(TargetMovement.X, Ragdoll.MAX_SPEED) && MovementPolicy.Valid(TargetMovement.Y, Ragdoll.MAX_SPEED) &&
        MovementPolicy.Valid(Rotation, MathHelper.Pi) && MovementPolicy.Valid(AngularVelocity, MovementPolicy.MaxAngularVelocity) &&
        Limbs is { Length: > 0 and <= MovementPolicy.MaxPoseLimbs } && Limbs.All(l => l.IsValid) &&
        Limbs.Select(l => l.Index).Distinct().Count() == Limbs.Length;
}

internal readonly record struct InputIntent(uint Sequence, ushort InputId, ushort Flags, ushort Aim, ushort Interact);

internal readonly record struct InteractionState(uint Revision, uint ProcessedAction, ushort SelectedCharacter,
    ushort SelectedItem, ushort SelectedSecondaryItem, bool Cpr, bool Ragdolled);

internal enum ActionResult { Accepted, Duplicate, Overflow }
internal sealed class MovementInbox
{
    public MovementSample? Latest { get; private set; }
    public bool AcceptLatest(MovementSample sample)
    {
        if (!sample.IsValid || (Latest is { } previous && !MovementPolicy.MoreRecent(sample.Sequence, previous.Sequence))) { return false; }
        Latest = sample; return true;
    }
    public void Reset() => Latest = null;
}

internal sealed class IntentInbox
{
    private readonly Queue<(uint Id, InputIntent Intent, double Time)> _pending = new();
    public InputIntent? Latest { get; private set; }
    public uint LastAction { get; private set; }
    public uint ProcessedAction { get; private set; }
    public ushort HeldActions { get; private set; }
    public int PendingCount => _pending.Count;
    public double ReceivedAt { get; private set; }
    public bool AcceptLatest(InputIntent intent, double now)
    {
        if (Latest is { } previous && !MovementPolicy.MoreRecent(intent.Sequence, previous.Sequence)) { return false; }
        Latest = intent; ReceivedAt = now; return true;
    }
    public ActionResult EnqueueAction(uint id, InputIntent intent, double now)
    {
        if (!MovementPolicy.MoreRecent(id, LastAction)) { return ActionResult.Duplicate; }
        if (_pending.Count >= MovementPolicy.MaxPendingActions) { return ActionResult.Overflow; }
        LastAction = id; _pending.Enqueue((id, intent, now)); AcceptLatest(intent, now); return ActionResult.Accepted;
    }
    public InputIntent Consume(double now, bool canAct)
    {
        var current = Latest ?? default;
        if (Latest == null || MovementPolicy.Expired(now, ReceivedAt, MovementPolicy.InputTimeout))
        {
            DiscardActions();
            return current with { Flags = (ushort)(current.Flags & (MovementPolicy.Facing | MovementPolicy.RagdollKey)) };
        }
        if (!canAct) { DiscardActions(); }
        while (_pending.TryPeek(out var head) && MovementPolicy.Expired(now, head.Time, MovementPolicy.InputTimeout))
        {
            _pending.Dequeue(); ProcessedAction = head.Id; HeldActions = 0;
        }
        var buttons = HeldActions;
        if (!canAct || !_pending.TryDequeue(out var action))
            return current with { Flags = (ushort)((current.Flags & ~MovementPolicy.ActionMask) | buttons) };
        ProcessedAction = action.Id;
        HeldActions = (ushort)(action.Intent.Flags & MovementPolicy.HeldActionMask);
        buttons = (ushort)(action.Intent.Flags & MovementPolicy.ActionMask);
        current = current with { Aim = action.Intent.Aim, Interact = action.Intent.Interact };
        return current with { Flags = (ushort)((current.Flags & ~MovementPolicy.ActionMask) | buttons) };
    }
    private void DiscardActions()
    {
        _pending.Clear(); HeldActions = 0; ProcessedAction = LastAction;
    }
    public void Reset()
    {
        Latest = null; LastAction = ProcessedAction = 0; ReceivedAt = 0; _pending.Clear(); HeldActions = 0;
    }
}

#pragma warning disable CS0649
internal struct LimbPoseMessage : INetSerializableStruct
{
    [NetworkSerialize] public ushort Index;
    [NetworkSerialize(MinValueFloat = -MovementPolicy.MaxLimbOffset, MaxValueFloat = MovementPolicy.MaxLimbOffset, NumberOfBits = 16)]
    public float OffsetX;
    [NetworkSerialize(MinValueFloat = -MovementPolicy.MaxLimbOffset, MaxValueFloat = MovementPolicy.MaxLimbOffset, NumberOfBits = 16)]
    public float OffsetY;
    [NetworkSerialize(MinValueFloat = -NetConfig.MaxPhysicsBodyVelocity, MaxValueFloat = NetConfig.MaxPhysicsBodyVelocity, NumberOfBits = 12)]
    public float VelocityX;
    [NetworkSerialize(MinValueFloat = -NetConfig.MaxPhysicsBodyVelocity, MaxValueFloat = NetConfig.MaxPhysicsBodyVelocity, NumberOfBits = 12)]
    public float VelocityY;
    [NetworkSerialize(MinValueFloat = -MathHelper.Pi, MaxValueFloat = MathHelper.Pi, NumberOfBits = 16)]
    public float Rotation;
    [NetworkSerialize(MinValueFloat = -MovementPolicy.MaxAngularVelocity, MaxValueFloat = MovementPolicy.MaxAngularVelocity, NumberOfBits = 12)]
    public float AngularVelocity;

    public static LimbPoseMessage From(LimbPose pose) => new()
    {
        Index = pose.Index,
        OffsetX = pose.Offset.X, OffsetY = pose.Offset.Y,
        VelocityX = pose.Velocity.X, VelocityY = pose.Velocity.Y,
        Rotation = pose.Rotation, AngularVelocity = pose.AngularVelocity
    };

    public readonly LimbPose ToPose() =>
        new(Index, new Vector2(OffsetX, OffsetY), new Vector2(VelocityX, VelocityY), Rotation, AngularVelocity);
}

internal struct MovementSampleMessage : INetSerializableStruct
{
    [NetworkSerialize] public uint Sequence;
    [NetworkSerialize] public ushort SubmarineId;
    [NetworkSerialize] public ushort HullId;
    [NetworkSerialize] public Vector2 Position;
    [NetworkSerialize] public Vector2 Velocity;
    [NetworkSerialize] public Vector2 TargetMovement;
    [NetworkSerialize] public float Rotation;
    [NetworkSerialize] public float AngularVelocity;
    [NetworkSerialize] public bool FacingLeft;
    [NetworkSerialize] public bool IgnorePlatforms;
    [NetworkSerialize] public bool Eligible;
    [NetworkSerialize] public bool ColliderEnabled;
    [NetworkSerialize] public bool FixedRotation;
    [NetworkSerialize] public bool Ragdolled;
    [NetworkSerialize] public byte ColliderIndex;
    [NetworkSerialize(ArrayMaxSize = MovementPolicy.MaxPoseLimbs)] public LimbPoseMessage[] Limbs;

    public static MovementSampleMessage From(MovementSample sample) => new()
    {
        Sequence = sample.Sequence, SubmarineId = sample.SubmarineId, HullId = sample.HullId,
        Position = sample.Position, Velocity = sample.Velocity, TargetMovement = sample.TargetMovement,
        Rotation = sample.Rotation, AngularVelocity = sample.AngularVelocity,
        FacingLeft = sample.FacingLeft, IgnorePlatforms = sample.IgnorePlatforms, Eligible = sample.Eligible,
        ColliderEnabled = sample.ColliderEnabled, FixedRotation = sample.FixedRotation, Ragdolled = sample.Ragdolled,
        ColliderIndex = sample.ColliderIndex,
        Limbs = sample.Limbs is { } limbs ? limbs.Select(LimbPoseMessage.From).ToArray() : Array.Empty<LimbPoseMessage>()
    };

    public readonly MovementSample ToSample() => new(Sequence, SubmarineId, HullId, Position, Velocity, TargetMovement,
        Rotation, AngularVelocity, FacingLeft, IgnorePlatforms, Eligible,
        Limbs.Select(l => l.ToPose()).ToArray(), ColliderEnabled, FixedRotation, Ragdolled, ColliderIndex);
}

internal struct InputIntentMessage : INetSerializableStruct
{
    [NetworkSerialize] public uint Sequence;
    [NetworkSerialize] public ushort InputId;
    [NetworkSerialize] public ushort Flags;
    [NetworkSerialize] public ushort Aim;
    [NetworkSerialize] public ushort Interact;

    public static InputIntentMessage From(InputIntent intent) => new()
    {
        Sequence = intent.Sequence, InputId = intent.InputId, Flags = intent.Flags, Aim = intent.Aim, Interact = intent.Interact
    };

    public readonly InputIntent ToIntent() => new(Sequence, InputId, Flags, Aim, Interact);
}

internal struct InteractionStateMessage : INetSerializableStruct
{
    [NetworkSerialize] public uint Revision;
    [NetworkSerialize] public uint ProcessedAction;
    [NetworkSerialize] public ushort SelectedCharacter;
    [NetworkSerialize] public ushort SelectedItem;
    [NetworkSerialize] public ushort SelectedSecondaryItem;
    [NetworkSerialize] public bool Cpr;
    [NetworkSerialize] public bool Ragdolled;

    public static InteractionStateMessage From(InteractionState state) => new()
    {
        Revision = state.Revision, ProcessedAction = state.ProcessedAction,
        SelectedCharacter = state.SelectedCharacter, SelectedItem = state.SelectedItem,
        SelectedSecondaryItem = state.SelectedSecondaryItem, Cpr = state.Cpr, Ragdolled = state.Ragdolled
    };

    public readonly InteractionState ToState() => new(Revision, ProcessedAction, SelectedCharacter,
        SelectedItem, SelectedSecondaryItem, Cpr, Ragdolled);
}

internal struct HelloMessage : INetSerializableStruct
{
    [NetworkSerialize] public ushort CharacterId;
    [NetworkSerialize] public ulong Nonce;
}

internal struct ControlAckMessage : INetSerializableStruct
{
    [NetworkSerialize] public ushort CharacterId;
    [NetworkSerialize] public ulong Nonce;
    [NetworkSerialize] public ulong Epoch;
    [NetworkSerialize] public bool Ready;
}

internal struct SnapshotMessage : INetSerializableStruct
{
    [NetworkSerialize] public ushort CharacterId;
    [NetworkSerialize] public ulong Nonce;
    [NetworkSerialize] public ulong Epoch;
    [NetworkSerialize] public MovementSampleMessage Sample;
}

internal struct ControlMessage : INetSerializableStruct
{
    [NetworkSerialize] public ushort CharacterId;
    [NetworkSerialize] public ulong Nonce;
    [NetworkSerialize] public ulong Epoch;
    [NetworkSerialize] public byte Mode;
    [NetworkSerialize] public byte Reason;
    [NetworkSerialize] public ushort Writer;
    [NetworkSerialize] public MovementSampleMessage Sample;
}

internal struct HeartbeatMessage : INetSerializableStruct
{
    [NetworkSerialize] public ushort CharacterId;
    [NetworkSerialize] public ulong Nonce;
    [NetworkSerialize] public ulong Epoch;
    [NetworkSerialize] public byte Mode;
    [NetworkSerialize] public ushort Writer;
}

internal struct PoseMessage : INetSerializableStruct
{
    [NetworkSerialize] public ushort CharacterId;
    [NetworkSerialize] public ulong Nonce;
    [NetworkSerialize] public ulong Epoch;
    [NetworkSerialize] public MovementSampleMessage Sample;
}

internal struct ActorControlMessage : INetSerializableStruct
{
    [NetworkSerialize] public ushort CharacterId;
    [NetworkSerialize] public ulong Nonce;
    [NetworkSerialize] public ulong Generation;
}

internal struct InteractionMessage : INetSerializableStruct
{
    [NetworkSerialize] public ushort CharacterId;
    [NetworkSerialize] public ulong Nonce;
    [NetworkSerialize] public ulong Generation;
    [NetworkSerialize] public InteractionStateMessage State;
}

internal struct ActionMessage : INetSerializableStruct
{
    [NetworkSerialize] public ushort CharacterId;
    [NetworkSerialize] public ulong Nonce;
    [NetworkSerialize] public ulong Generation;
    [NetworkSerialize] public uint ActionId;
    [NetworkSerialize] public InputIntentMessage Intent;
}

internal struct IntentMessage : INetSerializableStruct
{
    [NetworkSerialize] public ushort CharacterId;
    [NetworkSerialize] public ulong Nonce;
    [NetworkSerialize] public ulong Generation;
    [NetworkSerialize] public InputIntentMessage Intent;
}
#pragma warning restore CS0649
