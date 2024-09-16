using System.Linq;
using System.Text.RegularExpressions;
using Content.Server.Actions;
using Content.Server.Body.Systems;
using Content.Server.Chat;
using Content.Server.Chat.Systems;
using Content.Server.Emoting.Systems;
using Content.Server.Pinpointer;
using Content.Server.Speech.EntitySystems;
using Content.Shared.Bed.Sleep;
using Content.Shared.Cloning;
using Content.Shared.Damage;
using Content.Shared.Humanoid;
using Content.Shared.Inventory;
using Content.Shared.Mind;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.NameModifier.EntitySystems;
using Content.Shared.Popups;
using Content.Shared.Stunnable;
using Content.Shared.Throwing;
using Content.Shared.Weapons.Melee.Events;
using Content.Shared.Zombies;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Server.Zombies
{
    public sealed partial class ZombieSystem : SharedZombieSystem
    {
        [Dependency] private readonly IGameTiming _timing = default!;
        [Dependency] private readonly IPrototypeManager _protoManager = default!;
        [Dependency] private readonly IRobustRandom _random = default!;
        [Dependency] private readonly BloodstreamSystem _bloodstream = default!;
        [Dependency] private readonly DamageableSystem _damageable = default!;
        [Dependency] private readonly ChatSystem _chat = default!;
        [Dependency] private readonly ActionsSystem _actions = default!;
        [Dependency] private readonly AutoEmoteSystem _autoEmote = default!;
        [Dependency] private readonly EmoteOnDamageSystem _emoteOnDamage = default!;
        [Dependency] private readonly MobStateSystem _mobState = default!;
        [Dependency] private readonly SharedPopupSystem _popup = default!;
        [Dependency] private readonly NameModifierSystem _nameMod = default!;
        [Dependency] private readonly ChatSystem _chatSystem = default!;
        [Dependency] private readonly ThrowingSystem _throwing = default!;
        [Dependency] private readonly ActionsSystem _action = default!;
        [Dependency] private readonly SharedStunSystem _stun = default!;
        [Dependency] private readonly NavMapSystem _navMap = default!; // Sunrise-Zombies
        [Dependency] private readonly SharedTransformSystem _transform = default!;

        public const SlotFlags ProtectiveSlots =
            SlotFlags.FEET |
            SlotFlags.HEAD |
            SlotFlags.EYES |
            SlotFlags.GLOVES |
            SlotFlags.MASK |
            SlotFlags.NECK |
            SlotFlags.INNERCLOTHING |
            SlotFlags.OUTERCLOTHING;

        public override void Initialize()
        {
            base.Initialize();

            SubscribeLocalEvent<ZombieComponent, ComponentStartup>(OnStartup);
            SubscribeLocalEvent<ZombieComponent, EmoteEvent>(OnEmote, before:
                new[] { typeof(VocalSystem), typeof(BodyEmotesSystem) });

            SubscribeLocalEvent<ZombieComponent, MeleeHitEvent>(OnMeleeHit);
            SubscribeLocalEvent<ZombieComponent, MobStateChangedEvent>(OnMobState);
            SubscribeLocalEvent<ZombieComponent, CloningEvent>(OnZombieCloning);
            SubscribeLocalEvent<ZombieComponent, TryingToSleepEvent>(OnSleepAttempt);
            SubscribeLocalEvent<ZombieComponent, GetCharactedDeadIcEvent>(OnGetCharacterDeadIC);

            SubscribeLocalEvent<PendingZombieComponent, MapInitEvent>(OnPendingMapInit);

            SubscribeLocalEvent<IncurableZombieComponent, MapInitEvent>(OnPendingMapInit);

            SubscribeLocalEvent<ZombifyOnDeathComponent, MobStateChangedEvent>(OnDamageChanged);

            // Sunnrise-Start
            SubscribeLocalEvent<ZombieComponent, ZombieJumpActionEvent>(OnJump);
            SubscribeLocalEvent<ZombieComponent, ZombieFlairActionEvent>(OnFlair);
            SubscribeLocalEvent<ZombieComponent, ThrowDoHitEvent>(OnThrowDoHit);
            // Sunnrise-End
        }

        // Sunnrise-Start
        private void OnThrowDoHit(EntityUid uid, ZombieComponent component, ThrowDoHitEvent args)
        {
            if (_mobState.IsDead(uid))
                return;
            if (HasComp<ZombieComponent>(args.Target) || HasComp<PendingZombieComponent>(args.Target))
                return;
            if (!_mobState.IsAlive(args.Target))
                return;

            _stun.TryParalyze(args.Target, TimeSpan.FromSeconds(component.ParalyzeTime), false);
            _damageable.TryChangeDamage(args.Target, component.Damage, origin: args.Thrown);
        }

        private void OnFlair(EntityUid uid, ZombieComponent component, ZombieFlairActionEvent args)
        {
            if (args.Handled)
                return;

            var zombieXform = Transform(uid);
            EntityUid? nearestUid = default!;
            TransformComponent? nearestXform = default!;
            float? minDistance = null;
            var query = AllEntityQuery<HumanoidAppearanceComponent>();
            while (query.MoveNext(out var targetUid, out var humanoidAppearanceComponent))
            {
                // Зомби не должны чувствовать тех, у кого иммунитет к ним.
                if (HasComp<ZombieComponent>(targetUid) || HasComp<ZombieImmuneComponent>(targetUid))
                    continue;
                var xform = Transform(targetUid);

                // Почему бы и нет, оптимизация наху
                var distance = Math.Abs(zombieXform.Coordinates.X - xform.Coordinates.X) +
                               Math.Abs(zombieXform.Coordinates.Y - xform.Coordinates.Y);

                if (distance > component.MaxFlairDistance)
                    continue;

                if (minDistance == null || nearestUid == null || minDistance > distance)
                {
                    nearestUid = targetUid;
                    minDistance = distance;
                }
            }

            if (nearestUid == null || nearestUid == default!)
            {
                _popup.PopupEntity($"Ближайших выживших не найдено.", uid, uid, PopupType.LargeCaution);
            }
            else
            {
                _popup.PopupEntity($"Ближайший выживший находится {RemoveColorTags(_navMap.GetNearestBeaconString(nearestUid.Value))}", uid, uid, PopupType.LargeCaution);
            }

            args.Handled = true;
        }

        private string RemoveColorTags(string input)
        {
            // Регулярное выражение для поиска тэгов [color=...] и [/color]
            var pattern = @"\[\s*\/?\s*color(?:=[^\]]*)?\]";
            // Заменяем найденные тэги на пустую строку
            var result = Regex.Replace(input, pattern, string.Empty, RegexOptions.IgnoreCase);
            return result;
        }

        private void OnJump(EntityUid uid, ZombieComponent component, ZombieJumpActionEvent args)
        {
            if (args.Handled)
                return;

            // TODO: Проверка?
            // if ()
            // {
            //     _popup.PopupEntity(Loc.GetString("ни магу"),
            //         uid, uid, PopupType.LargeCaution);
            //     return;
            // }

            args.Handled = true;
            var xform = Transform(uid);
            var mapCoords = args.Target.ToMap(EntityManager, _transform);
            var direction = mapCoords.Position - xform.MapPosition.Position;

            if (direction.Length() > component.MaxThrow)
            {
                direction = direction.Normalized() * component.MaxThrow;
            }

            _throwing.TryThrow(uid, direction, 7F, uid, 10F);
            _chatSystem.TryEmoteWithChat(uid, "ZombieGroan");
        }
        // Sunnrise-End

        private void OnPendingMapInit(EntityUid uid, IncurableZombieComponent component, MapInitEvent args)
        {
            _actions.AddAction(uid, ref component.Action, component.ZombifySelfActionPrototype);
        }

        private void OnPendingMapInit(EntityUid uid, PendingZombieComponent component, MapInitEvent args)
        {
            if (_mobState.IsDead(uid))
            {
                ZombifyEntity(uid);
                return;
            }

            component.NextTick = _timing.CurTime + TimeSpan.FromSeconds(1f);
            component.GracePeriod = _random.Next(component.MinInitialInfectedGrace, component.MaxInitialInfectedGrace);
        }

        public override void Update(float frameTime)
        {
            base.Update(frameTime);
            var curTime = _timing.CurTime;

            // Hurt the living infected
            var query = EntityQueryEnumerator<PendingZombieComponent, DamageableComponent, MobStateComponent>();
            while (query.MoveNext(out var uid, out var comp, out var damage, out var mobState))
            {
                // Process only once per second
                if (comp.NextTick > curTime)
                    continue;

                comp.NextTick = curTime + TimeSpan.FromSeconds(1f);

                comp.GracePeriod -= TimeSpan.FromSeconds(1f);
                if (comp.GracePeriod > TimeSpan.Zero)
                    continue;

                if (_random.Prob(comp.InfectionWarningChance))
                    _popup.PopupEntity(Loc.GetString(_random.Pick(comp.InfectionWarnings)), uid, uid);

                var multiplier = _mobState.IsCritical(uid, mobState)
                    ? comp.CritDamageMultiplier
                    : 1f;

                _damageable.TryChangeDamage(uid, comp.Damage * multiplier, true, false, damage);
            }

            // Heal the zombified
            var zombQuery = EntityQueryEnumerator<ZombieComponent, DamageableComponent, MobStateComponent>();
            while (zombQuery.MoveNext(out var uid, out var comp, out var damage, out var mobState))
            {
                // Process only once per second
                if (comp.NextTick + TimeSpan.FromSeconds(1) > curTime)
                    continue;

                comp.NextTick = curTime;

                if (_mobState.IsDead(uid, mobState))
                    continue;

                var multiplier = _mobState.IsCritical(uid, mobState)
                    ? comp.PassiveHealingCritMultiplier
                    : 1f;

                // Gradual healing for living zombies.
                _damageable.TryChangeDamage(uid, comp.PassiveHealing * multiplier, true, false, damage);
            }
        }

        private void OnSleepAttempt(EntityUid uid, ZombieComponent component, ref TryingToSleepEvent args)
        {
            args.Cancelled = true;
        }

        private void OnGetCharacterDeadIC(EntityUid uid, ZombieComponent component, ref GetCharactedDeadIcEvent args)
        {
            args.Dead = true;
        }

        private void OnStartup(EntityUid uid, ZombieComponent component, ComponentStartup args)
        {
            if (component.EmoteSoundsId == null)
                return;
            _protoManager.TryIndex(component.EmoteSoundsId, out component.EmoteSounds);

            // Sunnrise-Start
            _action.AddAction(uid, component.ActionJumpId);
            _action.AddAction(uid, component.ActionFlairId);
            // Sunnrise-End
        }

        private void OnEmote(EntityUid uid, ZombieComponent component, ref EmoteEvent args)
        {
            // always play zombie emote sounds and ignore others
            if (args.Handled)
                return;
            args.Handled = _chat.TryPlayEmoteSound(uid, component.EmoteSounds, args.Emote);
        }

        private void OnMobState(EntityUid uid, ZombieComponent component, MobStateChangedEvent args)
        {
            if (args.NewMobState == MobState.Alive)
            {
                // Groaning when damaged
                EnsureComp<EmoteOnDamageComponent>(uid);
                _emoteOnDamage.AddEmote(uid, "Scream");

                // Random groaning
                EnsureComp<AutoEmoteComponent>(uid);
                _autoEmote.AddEmote(uid, "ZombieGroan");
            }
            else
            {
                // Stop groaning when damaged
                _emoteOnDamage.RemoveEmote(uid, "Scream");

                // Stop random groaning
                _autoEmote.RemoveEmote(uid, "ZombieGroan");
            }
        }

        private float GetZombieInfectionChance(EntityUid uid, ZombieComponent component)
        {
            var max = component.MaxZombieInfectionChance;

            if (!_inventory.TryGetContainerSlotEnumerator(uid, out var enumerator, ProtectiveSlots))
                return max;

            var items = 0f;
            var total = 0f;
            while (enumerator.MoveNext(out var con))
            {
                total++;
                if (con.ContainedEntity != null)
                    items++;
            }

            if (total == 0)
                return max;

            // Everyone knows that when it comes to zombies, socks & sandals provide just as much protection as an
            // armored vest. Maybe these should be weighted per-item. I.e. some kind of coverage/protection component.
            // Or at the very least different weights per slot.

            var min = component.MinZombieInfectionChance;
            //gets a value between the max and min based on how many items the entity is wearing
            var chance = (max - min) * ((total - items) / total) + min;
            return chance;
        }

        private void OnMeleeHit(EntityUid uid, ZombieComponent component, MeleeHitEvent args)
        {
            if (!TryComp<ZombieComponent>(args.User, out _))
                return;

            if (!args.HitEntities.Any())
                return;

            foreach (var entity in args.HitEntities)
            {
                if (args.User == entity)
                    continue;

                if (!TryComp<MobStateComponent>(entity, out var mobState))
                    continue;

                if (HasComp<ZombieComponent>(entity))
                {
                    args.BonusDamage = -args.BaseDamage;
                }
                else
                {
                    if (!HasComp<ZombieImmuneComponent>(entity) && !HasComp<NonSpreaderZombieComponent>(args.User) && _random.Prob(GetZombieInfectionChance(entity, component)))
                    {
                        EnsureComp<PendingZombieComponent>(entity);
                        EnsureComp<ZombifyOnDeathComponent>(entity);
                    }
                }

                if (_mobState.IsIncapacitated(entity, mobState) && !HasComp<ZombieComponent>(entity) && !HasComp<ZombieImmuneComponent>(entity))
                {
                    ZombifyEntity(entity);
                    args.BonusDamage = -args.BaseDamage;
                }
                else if (mobState.CurrentState == MobState.Alive) //heals when zombies bite live entities
                {
                    _damageable.TryChangeDamage(uid, component.HealingOnBite, true, false);
                }
            }
        }

        /// <summary>
        ///     This is the function to call if you want to unzombify an entity.
        /// </summary>
        /// <param name="source">the entity having the ZombieComponent</param>
        /// <param name="target">the entity you want to unzombify (different from source in case of cloning, for example)</param>
        /// <param name="zombiecomp"></param>
        /// <remarks>
        ///     this currently only restore the name and skin/eye color from before zombified
        ///     TODO: completely rethink how zombies are done to allow reversal.
        /// </remarks>
        public bool UnZombify(EntityUid source, EntityUid target, ZombieComponent? zombiecomp)
        {
            if (!Resolve(source, ref zombiecomp))
                return false;

            foreach (var (layer, info) in zombiecomp.BeforeZombifiedCustomBaseLayers)
            {
                _humanoidAppearance.SetBaseLayerColor(target, layer, info.Color);
                _humanoidAppearance.SetBaseLayerId(target, layer, info.Id);
            }
            if (TryComp<HumanoidAppearanceComponent>(target, out var appcomp))
            {
                appcomp.EyeColor = zombiecomp.BeforeZombifiedEyeColor;
            }
            _humanoidAppearance.SetSkinColor(target, zombiecomp.BeforeZombifiedSkinColor, false);
            _bloodstream.ChangeBloodReagent(target, zombiecomp.BeforeZombifiedBloodReagent);

            _nameMod.RefreshNameModifiers(target);
            return true;
        }

        private void OnZombieCloning(EntityUid uid, ZombieComponent zombiecomp, ref CloningEvent args)
        {
            if (UnZombify(args.Source, args.Target, zombiecomp))
                args.NameHandled = true;
        }
    }
}
