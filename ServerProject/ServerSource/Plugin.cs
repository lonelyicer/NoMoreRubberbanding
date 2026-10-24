using Barotrauma.Networking;
// ReSharper disable InconsistentNaming

namespace NoMoreRubberbanding;

public partial class Plugin
{
    private sealed class PeerSession(Client client, Character character, ulong nonce, ulong generation)
    {
        public readonly Client Client = client;
        public readonly Character Character = character;
        public readonly ulong Nonce = nonce;
        public ulong Generation = generation;
        public double LastHello = Now;
        public readonly IntentInbox Intents = new();
        public InteractionState? PublishedState;
        public uint StateRevision;
    }

    private sealed class BodyAuthority(Character character, PeerSession writer)
    {
        public readonly Character Character = character;
        public PeerSession? Writer = writer;
        public ulong Epoch;
        public AuthorityMode Mode;
        public TakeoverReason Reason;
        public bool Acknowledged, Ready;
        public readonly MovementInbox Inbox = new();
        public MovementSample Seed;
        public double ReceivedAt = Now, NextHeartbeat, TeleportHoldUntil;
        public uint RelayedSequence;
    }

    private readonly Dictionary<Client, PeerSession> _peers = new();
    private readonly Dictionary<Character, BodyAuthority> _authorities = new();
    [ThreadStatic] private static int _relocationDepth;
    private ulong _nextEpoch = NewToken() & 0x7FFF_FFFF_0000_0000UL;

    private void RegisterMessages()
    {
        Network.RegisterHandler<MovementHeader, HelloMessage>(MovementHeader.Hello, (msg, c) => ReadSafely(() => ReceiveHello(msg, c)));
        Network.RegisterHandler<MovementHeader, ControlAckMessage>(MovementHeader.ControlAck, (msg, c) => ReadSafely(() => ReceiveControlAck(msg, c)));
        Network.RegisterHandler<MovementHeader, SnapshotMessage>(MovementHeader.Snapshot, (msg, c) => ReadSafely(() => ReceiveSnapshot(msg, c)));
        Network.RegisterHandler<MovementHeader, IntentMessage>(MovementHeader.Intent, (msg, c) => ReadSafely(() => ReceiveIntent(msg, c)));
        Network.RegisterHandler<MovementHeader, ActionMessage>(MovementHeader.Action, (msg, c) => ReadSafely(() => ReceiveAction(msg, c)));
    }
    private void InstallPatches()
    {
        Patch(typeof(GameScreen), "Update", nameof(BeforeWorldUpdate), nameof(AfterWorldUpdate));
        Patch(typeof(Character), "Update", nameof(BeforeCharacterUpdate));
        Patch(typeof(Character), "UpdateAll", postfix: nameof(AfterCharacters));
        Patch(typeof(Character), "UpdateNetInput", nameof(ReadAuthoritativeInput));
        Patch(typeof(Character), "ServerReadInput", postfix: nameof(AfterNativeInput));
        Patch(typeof(Character), "UpdateAnimAll", postfix: nameof(AfterAnimations));
        Patch(typeof(Character), "SelectCharacter", nameof(BeforeSelectCharacter));
        Patch(typeof(Character), "TeleportTo", nameof(EnterRelocation), nameof(AfterTeleport), nameof(ExitRelocation));
        Patch(typeof(Ragdoll), "Teleport", nameof(EnterRelocation), nameof(AfterRagdollTeleport), nameof(ExitRelocation));
        Patch(typeof(Ragdoll), "FindHull", nameof(EnterRelocation), finalizer: nameof(ExitRelocation));
        Patch(typeof(Character), "ResetNetState", postfix: nameof(AfterNetReset));
    }
    private void ResetSessions()
    {
        foreach (var peer in _peers.Values) { StopInput(peer.Character); }
        _peers.Clear(); _authorities.Clear();
    }
    private bool PeerValid(PeerSession peer) => GameMain.Server is { GameStarted: true } &&
        peer.Client.Character == peer.Character && !peer.Character.Removed &&
        GameMain.Server.ConnectedClients.Contains(peer.Client) &&
        !MovementPolicy.Expired(Now, peer.LastHello, MovementPolicy.ControlTimeout);
    private PeerSession? Actor(Character c) => _peers.Values.FirstOrDefault(p => p.Character == c && PeerValid(p));

    private PeerSession? DesiredWriter(Character c)
    {
        if (!CanOwnMovement(c)) { return null; }
        var self = Actor(c);
        var dragger = c.SelectedBy == null ? null : Actor(c.SelectedBy);
        var validDragger = dragger != null && dragger.Character.SelectedCharacter == c &&
            dragger.Character.SelectedBy == null && CanPerformActions(dragger.Character) && CanOwnMovement(dragger.Character);
        var id = MovementPolicy.ChooseWriter(c.ID, c.SelectedBy?.ID ?? 0, c.SelectedBy != null, self != null && !c.IsDead, validDragger);
        return id == 0 ? null : dragger?.Character.ID == id ? dragger : self;
    }
    private void RefreshAuthorities()
    {
        foreach (var peer in _peers.Values.ToArray())
        {
            if (PeerValid(peer)) { continue; }
            StopInput(peer.Character); _peers.Remove(peer.Client);
        }
        var candidates = _peers.Values.Select(p => p.Character)
            .Concat(_peers.Values.Select(p => p.Character.SelectedCharacter).OfType<Character>())
            .Concat(_authorities.Keys).Distinct().ToArray();
        foreach (var c in candidates)
        {
            if (c.Removed) { _authorities.Remove(c); continue; }
            var desired = DesiredWriter(c);
            if (!_authorities.TryGetValue(c, out var a))
            {
                if (desired == null) { continue; }
                a = new BodyAuthority(c, desired); _authorities[c] = a;
                ChangeAuthority(a, AuthorityMode.Client, desired.Character == c ? TakeoverReason.Initial : TakeoverReason.Dragged);
            }
            else if (a.Writer != desired)
            {
                var wasDragged = a.Writer != null && a.Writer.Character != c;
                a.Writer = desired;
                ChangeAuthority(a, desired == null ? AuthorityMode.Server : AuthorityMode.Client,
                    desired != null && desired.Character != c ? TakeoverReason.Dragged :
                    wasDragged ? TakeoverReason.Released : TakeoverReason.Unavailable);
            }
            else switch (a.Mode)
            {
                case AuthorityMode.Client when MovementPolicy.Expired(Now, a.ReceivedAt, MovementPolicy.SnapshotTimeout):
                    ChangeAuthority(a, AuthorityMode.Server, TakeoverReason.Timeout);
                    break;
                case AuthorityMode.Server when desired != null && a.Acknowledged && a.Ready &&
                    MovementPolicy.RecoveryAllowed(Now, a.TeleportHoldUntil):
                    ChangeAuthority(a, AuthorityMode.Client, TakeoverReason.Recovery);
                    break;
            }
        }
    }
    private void UpdateSessions()
    {
        if (GameMain.Server is not { GameStarted: true }) { ResetSessions(); return; }
        RefreshAuthorities();
        foreach (var a in _authorities.Values.Where(a => Now >= a.NextHeartbeat))
        {
            a.NextHeartbeat = Now + 0.5;
            foreach (var peer in _peers.Values)
            {
                Network.SendToClient(peer.Client, MovementHeader.Heartbeat, new HeartbeatMessage
                {
                    CharacterId = a.Character.ID, Nonce = peer.Nonce, Epoch = a.Epoch,
                    Mode = (byte)a.Mode, Writer = a.Writer?.Character.ID ?? 0
                });
            }
        }
    }
    private void ReceiveHello(HelloMessage msg, Client client)
    {
        var id = msg.CharacterId; var nonce = msg.Nonce;
        if (_disposed || nonce == 0 ||
            GameMain.Server is not { GameStarted: true } || !GameMain.Server.ConnectedClients.Contains(client) ||
            client.Character is not { Removed: false } c || c.ID != id) { return; }
        var fresh = !_peers.TryGetValue(client, out var peer) || peer.Character != c || peer.Nonce != nonce;
        if (fresh)
        {
            peer = new PeerSession(client, c, nonce, ++_nextEpoch); _peers[client] = peer;
            StopInput(c);
            foreach (var observer in _peers.Values) { SendActorControl(peer, observer); }
        }
        peer!.LastHello = Now;
        foreach (var actor in _peers.Values)
        {
            SendActorControl(actor, peer);
            if (actor.PublishedState is { } state) { SendInteraction(actor, peer, state); }
        }
        RefreshAuthorities();
        foreach (var a in _authorities.Values.Where(a => fresh || a.Writer == peer || a.Character == c)) { SendControl(a, peer); }
    }
    private static void SendActorControl(PeerSession actor, PeerSession recipient)
    {
        Network.SendToClient(recipient.Client, MovementHeader.ActorControl, new ActorControlMessage
        {
            CharacterId = actor.Character.ID, Nonce = recipient.Nonce, Generation = actor.Generation
        });
    }
    private bool ReadActor(ushort id, ulong nonce, ulong generation, Client client, out PeerSession peer)
    {
        peer = null!;
        if (_disposed ||
            !_peers.TryGetValue(client, out var found) || !PeerValid(found) ||
            !MovementPolicy.CanWriteActions(found.Character.ID, id) ||
            !MovementPolicy.MatchesIdentity(found.Character.ID, found.Nonce, found.Generation, id, nonce, generation))
        { _stalePackets++; return false; }
        peer = found; return true;
    }
    private void ReceiveIntent(IntentMessage msg, Client client)
    {
        if (!ReadActor(msg.CharacterId, msg.Nonce, msg.Generation, client, out var peer)) { return; }
        if (!peer.Intents.AcceptLatest(msg.Intent.ToIntent(), Now)) { _stalePackets++; }
    }
    private void ReceiveAction(ActionMessage msg, Client client)
    {
        if (!ReadActor(msg.CharacterId, msg.Nonce, msg.Generation, client, out var peer)) { return; }
        var result = peer.Intents.EnqueueAction(msg.ActionId, msg.Intent.ToIntent(), Now);
        if (result == ActionResult.Accepted) { _actions++; return; }
        _stalePackets++;
        if (result != ActionResult.Overflow) { return; }
        peer.Generation = ++_nextEpoch; peer.Intents.Reset(); peer.PublishedState = null; peer.StateRevision = 0;
        StopInput(peer.Character);
        foreach (var observer in _peers.Values) { SendActorControl(peer, observer); }
    }
    private bool ReadIdentity(ushort id, ulong nonce, ulong epoch, Client client, out BodyAuthority authority, out PeerSession peer)
    {
        authority = null!; peer = null!;
        if (_disposed ||
            !_peers.TryGetValue(client, out var foundPeer) || !PeerValid(foundPeer) ||
            Entity.FindEntityByID(id) is not Character c || !_authorities.TryGetValue(c, out var found) ||
            !MovementPolicy.MatchesIdentity(c.ID, foundPeer.Nonce, found.Epoch, id, nonce, epoch))
        { _stalePackets++; return false; }
        authority = found; peer = foundPeer; return true;
    }
    private void ReceiveControlAck(ControlAckMessage msg, Client client)
    {
        if (!ReadIdentity(msg.CharacterId, msg.Nonce, msg.Epoch, client, out var a, out var peer) || a.Writer != peer || DesiredWriter(a.Character) != peer) { return; }
        a.Ready = msg.Ready; a.Acknowledged = true;
    }
    private void ReceiveSnapshot(SnapshotMessage msg, Client client)
    {
        if (!ReadIdentity(msg.CharacterId, msg.Nonce, msg.Epoch, client, out var a, out var peer) || a.Mode != AuthorityMode.Client || !a.Acknowledged || !a.Ready ||
            !MovementPolicy.CanWriteBody(a.Writer?.Character.ID ?? 0, peer.Character.ID, DesiredWriter(a.Character)?.Character.ID ?? 0))
        { _stalePackets++; return; }
        var sample = msg.Sample.ToSample();
        if (!sample.IsValid || !ValidPose(a.Character, sample) || !Resolve(sample, out _, out _, out _))
        { a.Character.healthUpdateTimer = 0; _stalePackets++; return; }
        if (a.Inbox.Latest is { } previous && !MovementPolicy.MoreRecent(sample.Sequence, previous.Sequence))
        { _stalePackets++; return; }
        if (!sample.Eligible) { ChangeAuthority(a, AuthorityMode.Server, TakeoverReason.Unavailable); return; }
        if (!a.Inbox.AcceptLatest(sample)) { _stalePackets++; return; }
        a.ReceivedAt = Now; _snapshots++;
    }
    private void ChangeAuthority(BodyAuthority a, AuthorityMode mode, TakeoverReason reason)
    {
        if (reason != TakeoverReason.Teleport && a.Inbox.Latest is { } latest) { Apply(a.Character, latest); }
        a.Seed = Capture(a.Character, 0);
        a.Epoch = ++_nextEpoch; a.Mode = mode; a.Reason = reason;
        a.Acknowledged = a.Ready = false; a.Inbox.Reset(); a.RelayedSequence = 0; a.ReceivedAt = Now;
        if (reason != TakeoverReason.Teleport) { a.TeleportHoldUntil = 0; }
        a.Character.memInput.Clear();
        foreach (var peer in _peers.Values) { SendControl(a, peer); }
        DebugConsole.NewMessage($"[NoMoreRubberbanding] body={a.Character.ID}, writer={a.Writer?.Character.ID ?? 0}, authority={mode}, reason={reason}, epoch={a.Epoch}");
        if (mode == AuthorityMode.Server) { _takeovers++; }
    }
    private static void SendControl(BodyAuthority a, PeerSession peer)
    {
        if (!a.Seed.IsValid) { return; }
        Network.SendToClient(peer.Client, MovementHeader.Control, new ControlMessage
        {
            CharacterId = a.Character.ID, Nonce = peer.Nonce, Epoch = a.Epoch,
            Mode = (byte)a.Mode, Reason = (byte)a.Reason, Writer = a.Writer?.Character.ID ?? 0,
            Sample = MovementSampleMessage.From(a.Seed)
        });
    }
    private static void StopInput(Character c)
    {
        c.memInput.Clear(); c.prevDequeuedInput = c.dequeuedInput;
        c.dequeuedInput &= Character.InputNetFlags.FacingLeft | Character.InputNetFlags.Ragdoll;
        c.LastInputTime = Timing.TotalTime - 1;
    }
    private bool Active(Character c, out BodyAuthority a) =>
        _authorities.TryGetValue(c, out a!) && a is { Mode: AuthorityMode.Client, Acknowledged: true, Ready: true, Inbox.Latest: not null } &&
        a.Writer != null && a.Writer == DesiredWriter(c) && !MovementPolicy.Expired(Now, a.ReceivedAt, MovementPolicy.SnapshotTimeout);
    private static void BeforeWorldUpdate() { _instance?.UpdateSessions(); _instance?.RestoreLatest(); }
    private static void BeforeCharacterUpdate(Character __instance)
    {
        if (_instance is { } p && p.Active(__instance, out var a)) { Apply(__instance, a.Inbox.Latest!.Value); }
    }
    private static void AfterCharacters()
    {
        _instance?.RefreshAuthorities(); _instance?.RestoreLatest(); _instance?.PublishInteractions();
    }
    private static bool ReadAuthoritativeInput(Character __instance)
    {
        if (_instance is not { } p) { return true; }
        var actor = p.Actor(__instance);
        if (actor == null) { return true; }
        var intent = actor.Intents.Consume(Now, CanPerformActions(__instance));
        var flags = actor.Intents.Latest == null && __instance.IsRagdolled ? MovementPolicy.RagdollKey : intent.Flags;
        var target = Entity.FindEntityByID(intent.Interact);
        var canAct = CanPerformActions(__instance) && (flags & MovementPolicy.RagdollKey) == 0;
        flags = (ushort)((flags & ~MovementPolicy.ActionMask) | MovementPolicy.AllowedActions(
            (ushort)(flags & MovementPolicy.ActionMask), canAct, target != null && target == __instance.SelectedBy));
        __instance.memInput.Clear(); __instance.AnimController.Frozen = false;
        __instance.prevDequeuedInput = __instance.dequeuedInput;
        __instance.dequeuedInput = (Character.InputNetFlags)flags;
        __instance.LastProcessedID = intent.InputId; __instance.LastInputTime = Timing.TotalTime;
        var angle = intent.Aim / 65535.0 * MathHelper.TwoPi;
        __instance.CursorPosition = __instance.AimRefPosition + new Vector2((float)Math.Cos(angle), (float)Math.Sin(angle)) * 500;
        __instance.focusedItem = null; __instance.FocusedCharacter = null;
        if (!canAct) { return false; }
        switch (target)
        {
            case Item item when __instance.CanInteractWith(item):
                __instance.focusedItem = item; break;
            case Item item:
                item.PositionUpdateInterval = 0;
                var holdable = item.GetComponent<Barotrauma.Items.Components.Holdable>();
                if (holdable != null) { item.CreateServerEvent(holdable); }
                break;
            case Character other when __instance.CanInteractWith(other, maxDist: 250):
                __instance.FocusedCharacter = other; break;
        }
        return false;
    }
    private static bool BeforeSelectCharacter(Character __instance, Character? __0) =>
        _instance?.Actor(__instance) == null || __0 == null || __0 != __instance.SelectedBy;
    private static void AfterNativeInput(Character __instance)
    {
        if (_instance?.Actor(__instance) != null) { __instance.memInput.Clear(); }
    }
    private static void AfterAnimations() => _instance?.RestoreLatest();
    private static void AfterWorldUpdate()
    {
        if (_instance is not { } p) { return; }
        p._simulationSteps++; p.RefreshAuthorities(); p.RestoreLatest(); p.RelayPoses(); p.PublishInteractions();
    }
    private void RestoreLatest()
    {
        foreach (var a in _authorities.Values)
        { if (Active(a.Character, out _) && a.Inbox.Latest is { } sample) { Apply(a.Character, sample); } }
    }
    private void RelayPoses()
    {
        foreach (var a in _authorities.Values)
        {
            if (!Active(a.Character, out _) || a.Inbox.Latest is not { } sample || a.RelayedSequence == sample.Sequence) { continue; }
            a.RelayedSequence = sample.Sequence;
            sample = sample with { Ragdolled = a.Character.IsRagdolled };
            foreach (var peer in _peers.Values)
            {
                if (peer == a.Writer) { continue; }
                Network.SendToClient(peer.Client, MovementHeader.Pose, new PoseMessage
                {
                    CharacterId = a.Character.ID, Nonce = peer.Nonce, Epoch = a.Epoch,
                    Sample = MovementSampleMessage.From(sample)
                });
            }
        }
    }
    private void PublishInteractions()
    {
        foreach (var actor in _peers.Values)
        {
            var c = actor.Character;
            var state = new InteractionState(actor.StateRevision, actor.Intents.ProcessedAction, c.SelectedCharacter?.ID ?? 0,
                c.SelectedItem?.ID ?? 0, c.SelectedSecondaryItem?.ID ?? 0, c.AnimController.Anim == AnimController.Animation.CPR, c.IsRagdolled);
            if (actor.PublishedState == state) { continue; }
            state = state with { Revision = ++actor.StateRevision }; actor.PublishedState = state;
            foreach (var recipient in _peers.Values) { SendInteraction(actor, recipient, state); }
        }
    }
    private static void SendInteraction(PeerSession actor, PeerSession recipient, InteractionState state)
    {
        Network.SendToClient(recipient.Client, MovementHeader.Interaction, new InteractionMessage
        {
            CharacterId = actor.Character.ID, Nonce = recipient.Nonce, Generation = actor.Generation,
            State = InteractionStateMessage.From(state)
        });
    }
    private static void AfterTeleport(Character __instance)
    {
        if (_instance is not { } p || !p._authorities.TryGetValue(__instance, out var a)) { return; }
        a.TeleportHoldUntil = Now + MovementPolicy.TeleportHold;
        if (a.Mode == AuthorityMode.Server) { return; }
        if (p.Actor(__instance) is { } actor)
        {
            actor.Generation = ++p._nextEpoch; actor.Intents.Reset(); actor.PublishedState = null; actor.StateRevision = 0;
            StopInput(__instance);
            foreach (var peer in p._peers.Values) { SendActorControl(actor, peer); }
        }
        p.ChangeAuthority(a, AuthorityMode.Server, TakeoverReason.Teleport);
    }
    private static void AfterNetReset(Character __instance)
    {
        if (_instance is not { } p) { return; }
        foreach (var peer in p._peers.Values.Where(s => s.Character == __instance).ToArray()) { p._peers.Remove(peer.Client); }
        p.RefreshAuthorities();
    }
    private static void EnterRelocation(out int __state) { __state = _relocationDepth; _relocationDepth++; }
    private static void ExitRelocation(int __state) => _relocationDepth = __state;
    private static void AfterRagdollTeleport(Ragdoll __instance, int __state)
    { if (__state == 0) { AfterTeleport(__instance.Character); } }
}
