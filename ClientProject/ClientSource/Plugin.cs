using Barotrauma.Networking;
// ReSharper disable InconsistentNaming

namespace NoMoreRubberbanding;

public partial class Plugin
{
    private sealed class ClientBody(Character character)
    {
        public readonly Character Character = character;
        public ulong Epoch;
        public AuthorityMode Mode;
        public ushort Writer;
        public double LastControl, PoseReceivedAt, GrantedAt;
        public uint Sequence;
        public MovementSample? PendingControl, RemotePose;
        public bool ConfirmedRagdoll;
    }
    private sealed class ClientActor(Character character)
    {
        public readonly Character Character = character;
        public ulong Generation;
        public uint Sequence, ActionSequence;
        public ushort PreviousButtons;
        public InputIntent? Latest;
        public InteractionState? State, PendingState;
        public double LastControl;
    }
    private Character? _controlled;
    private ulong _nonce;
    private double _nextHello, _nextSnapshot;
    private readonly Dictionary<ushort, ClientBody> _bodies = new();
    private readonly Dictionary<ushort, ClientActor> _actors = new();
    private bool _applyingSelection;

    private void RegisterMessages()
    {
        Network.RegisterHandler<MovementHeader, ControlMessage>(MovementHeader.Control, msg => ReadSafely(() => ReceiveControl(msg)));
        Network.RegisterHandler<MovementHeader, HeartbeatMessage>(MovementHeader.Heartbeat, msg => ReadSafely(() => ReceiveHeartbeat(msg)));
        Network.RegisterHandler<MovementHeader, PoseMessage>(MovementHeader.Pose, msg => ReadSafely(() => ReceivePose(msg)));
        Network.RegisterHandler<MovementHeader, ActorControlMessage>(MovementHeader.ActorControl, msg => ReadSafely(() => ReceiveActorControl(msg)));
        Network.RegisterHandler<MovementHeader, InteractionMessage>(MovementHeader.Interaction, msg => ReadSafely(() => ReceiveInteraction(msg)));
    }
    private void InstallPatches()
    {
        Patch(typeof(GameScreen), "Update", nameof(BeforeWorldUpdate), nameof(AfterWorldUpdate));
        Patch(typeof(Character), "UpdateNetInput", nameof(BeforeNetInput), nameof(AfterLocalInput));
        Patch(typeof(Character), "UpdateAll", postfix: nameof(AfterCharacters));
        Patch(typeof(Character), "UpdateAnimAll", postfix: nameof(AfterAnimations));
        Patch(typeof(Character), "SelectCharacter", nameof(BeforeSelectCharacter));
        Patch(typeof(Ragdoll), "UpdateNetPlayerPositionProjSpecific", nameof(ReconcileOwnerState));
        Patch(typeof(Character), "ResetNetState", postfix: nameof(AfterNetReset));
    }
    private void ResetSessions()
    {
        _controlled = null; _nonce = 0; _bodies.Clear(); _actors.Clear(); _nextHello = _nextSnapshot = 0;
    }
    private void UpdateSessions()
    {
        var c = GameMain.Client is { GameStarted: true } ? GameMain.Client.Character : null;
        if (c == null || c.Removed || c != Character.Controlled) { ResetSessions(); return; }
        if (_controlled != c) { ResetSessions(); _controlled = c; _nonce = NewToken(); }
        if (!c.isSynced) { return; }
        if (_actors.TryGetValue(c.ID, out var self) && MovementPolicy.Expired(Now, self.LastControl, MovementPolicy.ControlTimeout))
        {
            ResetSessions(); _controlled = c; _nonce = NewToken();
            c.MemState.Clear(); c.MemLocalState.Clear(); c.memInput.Clear();
        }
        if (Now < _nextHello) { return; }
        _nextHello = Now + 1;
        Network.Send(MovementHeader.Hello, new HelloMessage { CharacterId = c.ID, Nonce = _nonce });
        foreach (var body in _bodies.Values.Where(b => b.Writer == c.ID)) { Acknowledge(body); }
    }
    private bool ReadIdentity(ushort id, ulong nonce, ulong token, out Character c, out ulong generation)
    {
        c = null!; generation = token;
        if (_disposed || token == 0 || _controlled == null || _controlled != GameMain.Client?.Character ||
            _controlled != Character.Controlled || nonce != _nonce ||
            Entity.FindEntityByID(id) is not Character { Removed: false } character) { return false; }
        c = character; return true;
    }
    private void ReceiveActorControl(ActorControlMessage msg)
    {
        if (!ReadIdentity(msg.CharacterId, msg.Nonce, msg.Generation, out var c, out var generation)) { _stalePackets++; return; }
        if (!_actors.TryGetValue(c.ID, out var actor) || actor.Character != c)
        { actor = new ClientActor(c); _actors[c.ID] = actor; }
        if (generation < actor.Generation) { _stalePackets++; return; }
        actor.LastControl = Now;
        if (generation == actor.Generation) { return; }
        actor.Generation = generation; actor.Sequence = actor.ActionSequence = 0; actor.PreviousButtons = 0;
        actor.Latest = null; actor.State = actor.PendingState = null;
    }
    private void ReceiveInteraction(InteractionMessage msg)
    {
        if (!ReadIdentity(msg.CharacterId, msg.Nonce, msg.Generation, out var c, out var generation) || !_actors.TryGetValue(c.ID, out var actor) ||
            actor.Character != c || actor.Generation != generation) { _stalePackets++; return; }
        var state = msg.State.ToState();
        if (actor.State is { } previous && !MovementPolicy.MoreRecent(state.Revision, previous.Revision)) { return; }
        actor.State = actor.PendingState = state; actor.LastControl = Now;
    }
    private void ReceiveControl(ControlMessage msg)
    {
        if (!ReadIdentity(msg.CharacterId, msg.Nonce, msg.Epoch, out var c, out var epoch)) { _stalePackets++; return; }
        var mode = (AuthorityMode)msg.Mode;
        var reason = (TakeoverReason)msg.Reason;
        var writer = msg.Writer;
        var sample = msg.Sample.ToSample();
        if (mode > AuthorityMode.Client || reason > TakeoverReason.Released ||
            (mode == AuthorityMode.Client && writer == 0) || !ValidPose(c, sample) || !Resolve(sample, out _, out _, out _))
        { _stalePackets++; return; }
        if (!_bodies.TryGetValue(c.ID, out var body) || body.Character != c)
        { body = new ClientBody(c); _bodies[c.ID] = body; }
        if (epoch < body.Epoch) { _stalePackets++; return; }
        body.LastControl = Now;
        if (epoch == body.Epoch) { return; }
        body.Epoch = epoch; body.Mode = mode; body.Writer = writer; body.Sequence = 0; body.GrantedAt = Now;
        body.RemotePose = null; body.PendingControl = sample; body.ConfirmedRagdoll = sample.Ragdolled;
    }
    private void ReceiveHeartbeat(HeartbeatMessage msg)
    {
        if (!ReadIdentity(msg.CharacterId, msg.Nonce, msg.Epoch, out var c, out var epoch)) { return; }
        var mode = (AuthorityMode)msg.Mode; var writer = msg.Writer;
        if (_bodies.TryGetValue(c.ID, out var b) && b.Character == c && b.Epoch == epoch && b.Mode == mode && b.Writer == writer)
        { b.LastControl = Now; }
    }
    private void ReceivePose(PoseMessage msg)
    {
        if (!ReadIdentity(msg.CharacterId, msg.Nonce, msg.Epoch, out var c, out var epoch) || !_bodies.TryGetValue(c.ID, out var body) || body.Character != c ||
            body.Epoch != epoch || body.Mode != AuthorityMode.Client || body.Writer == _controlled!.ID) { _stalePackets++; return; }
        var sample = msg.Sample.ToSample();
        if (!ValidPose(c, sample) || !Resolve(sample, out _, out _, out _) ||
            (body.RemotePose is { } previous && !MovementPolicy.MoreRecent(sample.Sequence, previous.Sequence)))
        { _stalePackets++; return; }
        body.RemotePose = sample; body.PoseReceivedAt = Now; body.ConfirmedRagdoll = sample.Ragdolled;
    }
    private void Acknowledge(ClientBody body)
    {
        if (_controlled == null || body.Writer != _controlled.ID || body.PendingControl.HasValue) { return; }
        Network.Send(MovementHeader.ControlAck, new ControlAckMessage
        {
            CharacterId = body.Character.ID, Nonce = _nonce, Epoch = body.Epoch,
            Ready = CanOwnMovement(body.Character) && (body.Character == _controlled || _controlled.SelectedCharacter == body.Character)
        });
    }
    private bool HasLease(Character c, out ClientBody body)
    {
        body = null!;
        return _controlled != null && _bodies.TryGetValue(c.ID, out body!) && body.Character == c &&
            body.Mode == AuthorityMode.Client && !MovementPolicy.Expired(Now, body.LastControl, MovementPolicy.ControlTimeout) &&
            (body.Writer == _controlled.ID || !MovementPolicy.Expired(Now,
                body.RemotePose.HasValue ? body.PoseReceivedAt : body.GrantedAt, MovementPolicy.SnapshotTimeout));
    }
    
    private static void BeforeWorldUpdate()
    {
        if (_instance is not { } p) { return; }
        p.UpdateSessions();
        if (GameMain.World.IsLocked) { return; }
        p.ReconcileInteractions();
        foreach (var body in p._bodies.Values)
        {
            var c = body.Character;
            if (c.Removed || body.PendingControl is not { } sample || !Apply(c, sample)) { continue; }
            body.PendingControl = null; c.MemState.Clear(); c.MemLocalState.Clear();
            if (c == p._controlled) { c.memInput.Clear(); }
            c.isSynced = true; c.AnimController.Frozen = false; p.Acknowledge(body);
        }
        p.RestoreReplicas();
    }
    private void ReconcileInteractions()
    {
        foreach (var actor in _actors.Values)
        {
            if (actor.Character.Removed || actor.PendingState is not { } state) { continue; }
            if (actor.Character == _controlled && !MovementPolicy.CanReconcile(actor.ActionSequence, state.ProcessedAction)) { continue; }
            if ((state.SelectedCharacter != 0 && Entity.FindEntityByID(state.SelectedCharacter) is not Character) ||
                (state.SelectedItem != 0 && Entity.FindEntityByID(state.SelectedItem) is not Item) ||
                (state.SelectedSecondaryItem != 0 && Entity.FindEntityByID(state.SelectedSecondaryItem) is not Item)) { continue; }
            _applyingSelection = true;
            try { ApplySelection(actor.Character, state); actor.PendingState = null; }
            finally { _applyingSelection = false; }
        }
    }
    private static bool BeforeNetInput(Character __instance)
    {
        if (_instance is not { } p || __instance == p._controlled || !p.HasLease(__instance, out var body)) { return true; }
        var ragdoll = p._actors.TryGetValue(__instance.ID, out var actor) && actor.State is { } state ? state.Ragdolled : body.ConfirmedRagdoll;
        __instance.SetInput(InputType.Ragdoll, false, ragdoll);
        __instance.IsRagdolled = ragdoll || __instance.IsForceRagdolled;
        __instance.AnimController.Frozen = false;
        return false;
    }
    private static void AfterLocalInput(Character __instance)
    {
        if (_instance is not { } p || p._controlled != __instance || __instance.memInput.Count == 0 ||
            !p._actors.TryGetValue(__instance.ID, out var actor) || actor.Character != __instance ||
            MovementPolicy.Expired(Now, actor.LastControl, MovementPolicy.ControlTimeout)) { return; }
        var input = __instance.memInput[0];
        var postureFlags = (ushort)(((ushort)input.states & ~MovementPolicy.RagdollKey) |
            (__instance.IsRagdolled ? MovementPolicy.RagdollKey : 0));
        var intent = new InputIntent(++actor.Sequence, unchecked((ushort)(__instance.LastNetworkUpdateID - 1)),
            postureFlags, input.intAim, input.interact);
        var canAct = CanPerformActions(__instance) && (intent.Flags & MovementPolicy.RagdollKey) == 0;
        var buttons = MovementPolicy.AllowedActions((ushort)(intent.Flags & MovementPolicy.ActionMask), canAct,
            __instance.SelectedBy != null && intent.Interact == __instance.SelectedBy.ID);
        intent = intent with { Flags = (ushort)((intent.Flags & ~MovementPolicy.ActionMask) | buttons) };
        actor.Latest = intent;
        var held = (ushort)(buttons & MovementPolicy.HeldActionMask);
        var reliable = (ushort)(held | (intent.Flags & MovementPolicy.RagdollKey));
        if ((buttons & MovementPolicy.EdgeMask) != 0 || reliable != actor.PreviousButtons)
        {
            Network.Send(MovementHeader.Action, new ActionMessage
            {
                CharacterId = __instance.ID, Nonce = p._nonce, Generation = actor.Generation,
                ActionId = ++actor.ActionSequence, Intent = InputIntentMessage.From(intent)
            });
            p._actions++; actor.PreviousButtons = reliable;
        }
        if (__instance.MemLocalState.Count > 120) { __instance.MemLocalState.RemoveRange(0, __instance.MemLocalState.Count - 120); }
    }
    private static bool BeforeSelectCharacter(Character __instance, Character? __0)
    {
        if (_instance is not { } p || p._applyingSelection || __instance != p._controlled || !p._actors.ContainsKey(__instance.ID)) { return true; }
        return __0 == null || (CanPerformActions(__instance) && __0 != __instance.SelectedBy);
    }
    private static void AfterCharacters()
    {
        if (_instance is not { } p) { return; }
        if (p._controlled is { } c && p._actors.TryGetValue(c.ID, out var actor) && actor.Latest is { } latest)
        {
            var posture = c.IsRagdolled ? MovementPolicy.RagdollKey : (ushort)0;
            if ((latest.Flags & MovementPolicy.RagdollKey) != posture)
            {
                var intent = latest with { Sequence = ++actor.Sequence,
                    Flags = (ushort)((latest.Flags & ~(MovementPolicy.RagdollKey | MovementPolicy.EdgeMask)) | posture) };
                if (posture != 0) { intent = intent with { Flags = (ushort)(intent.Flags & ~MovementPolicy.ActionMask) }; }
                actor.Latest = intent;
                actor.PreviousButtons = (ushort)(intent.Flags & (MovementPolicy.HeldActionMask | MovementPolicy.RagdollKey));
                Network.Send(MovementHeader.Action, new ActionMessage
                {
                    CharacterId = c.ID, Nonce = p._nonce, Generation = actor.Generation,
                    ActionId = ++actor.ActionSequence, Intent = InputIntentMessage.From(intent)
                });
                p._actions++;
            }
        }
        p.RestoreReplicas();
    }
    private static void AfterAnimations() => _instance?.RestoreReplicas();
    private static void AfterWorldUpdate()
    {
        if (_instance is not { } p) { return; }
        p._simulationSteps++; p.RestoreReplicas();
        if (p._controlled == null || Now < p._nextSnapshot) { return; }
        p._nextSnapshot = Now + MovementPolicy.SendInterval;
        if (!p._controlled.IsDead && p._actors.TryGetValue(p._controlled.ID, out var actor) && actor.Latest is { } intent)
        {
            Network.Send(MovementHeader.Intent, new IntentMessage
            {
                CharacterId = actor.Character.ID, Nonce = p._nonce, Generation = actor.Generation,
                Intent = InputIntentMessage.From(intent)
            });
        }
        foreach (var body in p._bodies.Values)
        {
            var c = body.Character;
            if (c.Removed || body.Mode != AuthorityMode.Client || body.Writer != p._controlled.ID || body.PendingControl.HasValue ||
                MovementPolicy.Expired(Now, body.LastControl, MovementPolicy.ControlTimeout)) { continue; }
            var sample = Capture(c, ++body.Sequence);
            sample = sample with { Eligible = sample.Eligible && (c == p._controlled || p._controlled.SelectedCharacter == c) };
            if (!sample.IsValid) { continue; }
            Network.Send(MovementHeader.Snapshot, new SnapshotMessage
            {
                CharacterId = c.ID, Nonce = p._nonce, Epoch = body.Epoch,
                Sample = MovementSampleMessage.From(sample)
            });
            p._snapshots++;
        }
    }
    private static bool ReconcileOwnerState(Ragdoll __instance)
    {
        if (_instance is not { } p || !p.HasLease(__instance.Character, out _)) { return true; }
        var c = __instance.Character;
        p._suppressedCorrections += c.MemState.Count; c.MemState.Clear(); c.MemLocalState.Clear();
        __instance.overrideTargetMovement = null; __instance.Frozen = false;
        return false;
    }
    private void RestoreReplicas()
    {
        if (GameMain.World.IsLocked) { return; }
        foreach (var body in _bodies.Values)
        {
            if (body.Character.Removed || !HasLease(body.Character, out _) || body.Writer == _controlled!.ID ||
                body.PendingControl.HasValue || body.RemotePose is not { } sample ||
                MovementPolicy.Expired(Now, body.PoseReceivedAt, MovementPolicy.SnapshotTimeout)) { continue; }
            Apply(body.Character, sample);
        }
    }
    private static void AfterNetReset(Character __instance)
    {
        if (_instance is not { } p) { return; }
        if (p._controlled == __instance) { p.ResetSessions(); }
        else { p._bodies.Remove(__instance.ID); p._actors.Remove(__instance.ID); }
    }
}
