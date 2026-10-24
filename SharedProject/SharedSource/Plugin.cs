using System.Diagnostics;
using System.Security.Cryptography;
using Barotrauma.Networking;
using Barotrauma.Plugins;
using FarseerPhysics;
using HarmonyLib;

namespace NoMoreRubberbanding;

public partial class Plugin : IBarotraumaPlugin
{
    public static readonly IDebugConsole DebugConsole = PluginServiceProvider.GetService<IDebugConsole>();
    public static readonly ISimpleHookService Hooks = PluginServiceProvider.GetService<ISimpleHookService>();
    public static readonly IHarmonyProvider HarmonyProvider = PluginServiceProvider.GetService<IHarmonyProvider>();
    public static readonly IGameNetwork Network = PluginServiceProvider.GetService<IGameNetwork>();
    private static Plugin? _instance;
    private Harmony? _harmony;
    private bool _disposed;
    private double _nextStatistics;
    private double _statisticsStarted = Now;
    private long _simulationSteps;
    private long _snapshots, _stalePackets, _actions, _takeovers, _suppressedCorrections;
    private static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    public void Init()
    {
        _instance = this;
        try
        {
            Network.RegisterNetworkHeaders<MovementHeader>();
            RegisterMessages();
            _harmony = HarmonyProvider.GetHarmony();
            InstallPatches();
            Hooks.RegisterHook<PluginOnPreUpdateDelegate>(OnUpdate);
            Hooks.RegisterHook<PluginRoundStartedDelegate>(OnRoundStart);
            Hooks.RegisterHook<PluginRoundEndedDelegate>(OnRoundEnd);
            DebugConsole.NewMessage($"[NoMoreRubberbanding] Movement authority loaded (protocol {MovementPolicy.Version}).");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void OnContentLoaded() { }

    private void OnRoundStart(GameSession gameSession) => ResetSessions();
    private void OnRoundEnd(GameSession gameSession) => ResetSessions();

    private void OnUpdate(float deltaTime)
    {
        if (_disposed) { return; }
        UpdateSessions();
        if (Now < _nextStatistics) { return; }
        _nextStatistics = Now + 30;
        var elapsed = Now - _statisticsStarted;
        var simulationRate = _simulationSteps / Math.Max(elapsed, 0.001);
        _simulationSteps = 0;
        _statisticsStarted = Now;
        if (_snapshots + _stalePackets + _actions + _takeovers + _suppressedCorrections == 0) { return; }
        DebugConsole.NewMessage($"[NoMoreRubberbanding] simulation steps/s={simulationRate:F1}, snapshots={_snapshots}, stale={_stalePackets}, " +
                                $"actions={_actions}, takeovers={_takeovers}, owner corrections bypassed={_suppressedCorrections}");
        _snapshots = _stalePackets = _actions = _takeovers = _suppressedCorrections = 0;
    }

    public void Dispose()
    {
        if (_disposed) { return; }
        _disposed = true;
        _harmony?.UnpatchSelf();
        ResetSessions();
        if (_instance == this) { _instance = null; }
    }

    private void Patch(Type type, string name, string? prefix = null, string? postfix = null, string? finalizer = null)
    {
        var target = AccessTools.DeclaredMethod(type, name)
            ?? throw new MissingMethodException(type.FullName, name);
        _harmony!.Patch(target,
            prefix == null ? null : new HarmonyMethod(typeof(Plugin), prefix),
            postfix == null ? null : new HarmonyMethod(typeof(Plugin), postfix),
            finalizer: finalizer == null ? null : new HarmonyMethod(typeof(Plugin), finalizer));
    }

    private void ReadSafely(Action read)
    {
        try { read(); }
        catch (Exception e) when (e is FormatException or System.IO.EndOfStreamException or IndexOutOfRangeException or ArgumentException)
        { _stalePackets++; }
    }

    private static ulong NewToken() => BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8)) | 1UL;

    private static bool CanOwnMovement(Character c) =>
        c is { Removed: false, Enabled: true } && !IsAttachedToAnyController(c) && c.Latchers.Count == 0 &&
        !c.AnimController.IsDraggedWithRope && !c.AnimController.IsHangingWithRope &&
        !c.AnimController.IsHoldingToRope && !c.AnimController.Invalid &&
        c.AnimController.Limbs.Length <= MovementPolicy.MaxPoseLimbs && c.AnimController.ColliderIndex <= byte.MaxValue &&
        c.AnimController.Collider is { Removed: false };

    private static bool IsAttachedToAnyController(Character c) =>
        // Vanilla IsAttachedToController only checks SelectedItem. Chairs are secondary
        // controllers, so a force-attached chair would otherwise remain client-authoritative.
        c.IsAttachedToController() ||
        (c.SelectedSecondaryItem?.GetComponent<Barotrauma.Items.Components.Controller>() is { } controller &&
         controller.IsAttachedUser(c));

    private static MovementSample Capture(Character c, uint sequence)
    {
        var anim = c.AnimController;
        var origin = c.Submarine == null ? Vector2.Zero : ConvertUnits.ToSimUnits(c.Submarine.HiddenSubPosition);
        var limbs = anim.Limbs.Select((limb, index) => (limb, index))
            .Where(p => !p.limb.IsSevered && !p.limb.body.Removed)
            .Select(p => new LimbPose((ushort)p.index, p.limb.SimPosition - anim.Collider.SimPosition, p.limb.LinearVelocity,
                MathHelper.WrapAngle(p.limb.Rotation), p.limb.body.AngularVelocity)).ToArray();
        return new MovementSample(sequence, c.Submarine?.ID ?? 0, anim.CurrentHull?.ID ?? 0,
            anim.Collider.SimPosition - origin, anim.Collider.LinearVelocity, anim.TargetMovement,
            MathHelper.WrapAngle(anim.Collider.Rotation), anim.Collider.AngularVelocity,
            anim.Dir < 0, anim.IgnorePlatforms, CanOwnMovement(c), limbs,
            anim.Collider.Enabled, anim.Collider.FarseerBody.FixedRotation, c.IsRagdolled, (byte)anim.ColliderIndex);
    }

    private static bool ValidPose(Character c, MovementSample sample)
    {
        if (!sample.IsValid || !c.AnimController.TryGetCollider(sample.ColliderIndex, out var collider) ||
            collider is not { Removed: false }) { return false; }
        var expected = c.AnimController.Limbs.Select((limb, index) => (limb, index))
            .Where(p => !p.limb.IsSevered && !p.limb.body.Removed).Select(p => (ushort)p.index).ToHashSet();
        return sample.Limbs!.Length == expected.Count && sample.Limbs.All(l => expected.Contains(l.Index));
    }

    private static bool CanPerformActions(Character c) => !c.IsDead && c.AllowInput && c.CanMove && !c.IsKnockedDownOrRagdolled;

    private static void ApplySelection(Character c, InteractionState state)
    {
        if (Entity.FindEntityByID(state.SelectedCharacter) is Character { Removed: false } target && target != c && target != c.SelectedBy)
        { if (c.SelectedCharacter != target) { c.SelectCharacter(target); } }
        else if (c.SelectedCharacter != null) { c.DeselectCharacter(); }
        var item = Entity.FindEntityByID(state.SelectedItem) as Item;
        var secondary = Entity.FindEntityByID(state.SelectedSecondaryItem) as Item;
        if (item?.Removed == true) { item = null; }
        if (secondary?.Removed == true) { secondary = null; }
        if (c.SelectedItem != item) { c.SelectedItem = item; }
        if (c.SelectedSecondaryItem != secondary) { c.SelectedSecondaryItem = secondary; }
        if (state.Cpr) { c.AnimController.Anim = AnimController.Animation.CPR; }
        else if (c.AnimController.Anim == AnimController.Animation.CPR) { c.AnimController.Anim = AnimController.Animation.None; }
    }

    private static bool Resolve(MovementSample sample, out Submarine? sub, out Hull? hull, out Vector2 position)
    {
        sub = sample.SubmarineId == 0 ? null : Entity.FindEntityByID(sample.SubmarineId) as Submarine;
        hull = sample.HullId == 0 ? null : Entity.FindEntityByID(sample.HullId) as Hull;
        position = sample.Position;
        if (!sample.IsValid || (sample.SubmarineId != 0 && (sub == null || sub.Removed || !Submarine.Loaded.Contains(sub))) ||
            (sample.HullId != 0 && (hull == null || hull.Removed || hull.Submarine != sub))) { return false; }
        if (sub != null) { position += ConvertUnits.ToSimUnits(sub.HiddenSubPosition); }
        return true;
    }


    private static bool Apply(Character c, MovementSample sample)
    {
        if (GameMain.World.IsLocked || !Resolve(sample, out var sub, out var hull, out var position) || !ValidPose(c, sample)) { return false; }
        var anim = c.AnimController;
        if (anim.ColliderIndex != sample.ColliderIndex)
        {
            anim.Collider.PhysEnabled = false;
            anim.colliderIndex = sample.ColliderIndex;
            anim.Collider.PhysEnabled = true;
        }
        var frameChanged = c.Submarine != sub;
        c.Submarine = sub;
        c.CurrentHull = anim.CurrentHull = hull;
        anim.Collider.Submarine = sub;
        anim.Collider.FarseerBody.FixedRotation = sample.FixedRotation;
        anim.Collider.SetTransformIgnoreContacts(position, sample.Rotation);
        anim.Collider.Enabled = sample.ColliderEnabled;
        anim.Collider.LinearVelocity = sample.Velocity;
        anim.Collider.AngularVelocity = sample.AngularVelocity;
        anim.Collider.TargetPosition = null;
        anim.Collider.TargetRotation = null;
        anim.TargetMovement = sample.TargetMovement;
        anim.IgnorePlatforms = sample.IgnorePlatforms;
        anim.TargetDir = sample.FacingLeft ? Direction.Left : Direction.Right;
        for (var i = 0; i < anim.Limbs.Length; i++)
        {
            var limb = anim.Limbs[i];
            if (limb.IsSevered || limb.body.Removed) { continue; }
            limb.body.Submarine = sub;
            var anchor = limb.PullJointWorldAnchorB;
            var previous = limb.SimPosition;
            var pose = sample.Limbs!.First(l => l.Index == i);
            var limbPosition = position + pose.Offset;
            limb.body.SetTransformIgnoreContacts(limbPosition, pose.Rotation);
            limb.body.LinearVelocity = pose.Velocity;
            limb.body.AngularVelocity = pose.AngularVelocity;
            limb.body.TargetPosition = null;
            limb.body.TargetRotation = null;
            if (limb.PullJointEnabled) { limb.PullJointWorldAnchorB = anchor + (limbPosition - previous); }
            foreach (var item in from projectile in c.AttachedProjectiles where projectile.IsAttachedTo(limb.body) select projectile.Item)
            {
                item.CurrentHull = hull;
                item.Submarine = sub;
                item.SetTransform(item.SimPosition + limbPosition - previous, item.body.Rotation, findNewHull: false);
            }
        }

        if (!frameChanged) return true;

        c.MemLocalState.Clear();
        anim.Collider.UpdateDrawPosition();
        foreach (var limb in anim.Limbs) { limb.body.UpdateDrawPosition(); }

        return true;
    }
}
