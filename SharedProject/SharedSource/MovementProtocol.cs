using Barotrauma.Networking;

namespace NoMoreRubberbanding;

internal enum AuthorityMode : byte { Server, Client }
internal enum TakeoverReason : byte { Initial, Unavailable, Timeout, Teleport, Recovery, Dragged, Released }

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
    public void Write(IWriteMessage msg)
    {
        msg.WriteUInt16(Index);
        msg.WriteRangedSingle(Offset.X, -MovementPolicy.MaxLimbOffset, MovementPolicy.MaxLimbOffset, 16);
        msg.WriteRangedSingle(Offset.Y, -MovementPolicy.MaxLimbOffset, MovementPolicy.MaxLimbOffset, 16);
        msg.WriteRangedSingle(Velocity.X, -NetConfig.MaxPhysicsBodyVelocity, NetConfig.MaxPhysicsBodyVelocity, 12);
        msg.WriteRangedSingle(Velocity.Y, -NetConfig.MaxPhysicsBodyVelocity, NetConfig.MaxPhysicsBodyVelocity, 12);
        msg.WriteRangedSingle(Rotation, -MathHelper.Pi, MathHelper.Pi, 16);
        msg.WriteRangedSingle(AngularVelocity, -MovementPolicy.MaxAngularVelocity, MovementPolicy.MaxAngularVelocity, 12);
    }
    public static LimbPose Read(IReadMessage msg) => new(msg.ReadUInt16(),
        new Vector2(msg.ReadRangedSingle(-MovementPolicy.MaxLimbOffset, MovementPolicy.MaxLimbOffset, 16),
            msg.ReadRangedSingle(-MovementPolicy.MaxLimbOffset, MovementPolicy.MaxLimbOffset, 16)),
        new Vector2(msg.ReadRangedSingle(-NetConfig.MaxPhysicsBodyVelocity, NetConfig.MaxPhysicsBodyVelocity, 12),
            msg.ReadRangedSingle(-NetConfig.MaxPhysicsBodyVelocity, NetConfig.MaxPhysicsBodyVelocity, 12)),
        msg.ReadRangedSingle(-MathHelper.Pi, MathHelper.Pi, 16),
        msg.ReadRangedSingle(-MovementPolicy.MaxAngularVelocity, MovementPolicy.MaxAngularVelocity, 12));
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
    public void Write(IWriteMessage msg)
    {
        msg.WriteUInt32(Sequence);
        msg.WriteUInt16(SubmarineId);
        msg.WriteUInt16(HullId);
        WriteVector(msg, Position);
        WriteVector(msg, Velocity);
        WriteVector(msg, TargetMovement);
        msg.WriteSingle(Rotation);
        msg.WriteSingle(AngularVelocity);
        msg.WriteBoolean(FacingLeft);
        msg.WriteBoolean(IgnorePlatforms);
        msg.WriteBoolean(Eligible);
        msg.WriteBoolean(ColliderEnabled);
        msg.WriteBoolean(FixedRotation);
        msg.WriteBoolean(Ragdolled);
        msg.WritePadBits();
        msg.WriteByte(ColliderIndex);
        msg.WriteByte((byte)(Limbs?.Length ?? 0));
        if (Limbs != null) { foreach (var limb in Limbs) { limb.Write(msg); } }
        msg.WritePadBits();
    }
    public static MovementSample Read(IReadMessage msg)
    {
        var sample = new MovementSample(msg.ReadUInt32(), msg.ReadUInt16(), msg.ReadUInt16(),
            ReadVector(msg), ReadVector(msg), ReadVector(msg), msg.ReadSingle(), msg.ReadSingle(),
            msg.ReadBoolean(), msg.ReadBoolean(), msg.ReadBoolean(),
            ColliderEnabled: msg.ReadBoolean(), FixedRotation: msg.ReadBoolean(), Ragdolled: msg.ReadBoolean());
        msg.ReadPadBits();
        var colliderIndex = msg.ReadByte();
        int count = msg.ReadByte();
        if (count is 0 or > MovementPolicy.MaxPoseLimbs) { throw new FormatException("Invalid full-body pose length."); }
        var limbs = new LimbPose[count];
        for (var i = 0; i < count; i++) { limbs[i] = LimbPose.Read(msg); }
        msg.ReadPadBits();
        return sample with { Limbs = limbs, ColliderIndex = colliderIndex };
    }
    private static void WriteVector(IWriteMessage msg, Vector2 value) { msg.WriteSingle(value.X); msg.WriteSingle(value.Y); }
    private static Vector2 ReadVector(IReadMessage msg) => new(msg.ReadSingle(), msg.ReadSingle());
}

internal readonly record struct InputIntent(uint Sequence, ushort InputId, ushort Flags, ushort Aim, ushort Interact)
{
    public void Write(IWriteMessage msg)
    {
        msg.WriteUInt32(Sequence); msg.WriteUInt16(InputId); msg.WriteUInt16(Flags); msg.WriteUInt16(Aim); msg.WriteUInt16(Interact);
    }
    public static InputIntent Read(IReadMessage msg) => new(msg.ReadUInt32(), msg.ReadUInt16(), msg.ReadUInt16(), msg.ReadUInt16(), msg.ReadUInt16());
}

internal readonly record struct InteractionState(uint Revision, uint ProcessedAction, ushort SelectedCharacter,
    ushort SelectedItem, ushort SelectedSecondaryItem, bool Cpr, bool Ragdolled)
{
    public void Write(IWriteMessage msg)
    {
        msg.WriteUInt32(Revision); msg.WriteUInt32(ProcessedAction);
        msg.WriteUInt16(SelectedCharacter); msg.WriteUInt16(SelectedItem); msg.WriteUInt16(SelectedSecondaryItem);
        msg.WriteBoolean(Cpr); msg.WriteBoolean(Ragdolled); msg.WritePadBits();
    }
    public static InteractionState Read(IReadMessage msg)
    {
        var state = new InteractionState(msg.ReadUInt32(), msg.ReadUInt32(), msg.ReadUInt16(), msg.ReadUInt16(), msg.ReadUInt16(), msg.ReadBoolean(), msg.ReadBoolean());
        msg.ReadPadBits(); return state;
    }
}

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

internal static class MovementProtocol
{
    public const string Hello = "NoMoreRubberbanding.Hello.v3";
    public const string Snapshot = "NoMoreRubberbanding.Snapshot.v3";
    public const string Action = "NoMoreRubberbanding.Action.v3";
    public const string Intent = "NoMoreRubberbanding.Intent.v3";
    public const string ActorControl = "NoMoreRubberbanding.Actor.v3";
    public const string Interaction = "NoMoreRubberbanding.Interaction.v3";
    public const string Control = "NoMoreRubberbanding.Control.v3";
    public const string ControlAck = "NoMoreRubberbanding.ControlAck.v3";
    public const string Heartbeat = "NoMoreRubberbanding.Heartbeat.v3";
    public const string Pose = "NoMoreRubberbanding.Pose.v3";
    public static void WriteIdentity(IWriteMessage msg, ushort characterId, ulong nonce, ulong epoch)
    {
        msg.WriteByte(MovementPolicy.Version); msg.WriteUInt16(characterId); msg.WriteUInt64(nonce); msg.WriteUInt64(epoch);
    }
    public static bool ReadIdentity(IReadMessage msg, out ushort characterId, out ulong nonce, out ulong epoch)
    {
        var version = msg.ReadByte(); characterId = msg.ReadUInt16(); nonce = msg.ReadUInt64(); epoch = msg.ReadUInt64();
        return version == MovementPolicy.Version;
    }
}
