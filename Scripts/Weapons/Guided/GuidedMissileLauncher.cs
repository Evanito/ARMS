﻿#if DEBUG
#define TRACE
#endif

using System;
using System.Collections.Generic;
using Rynchodon.AntennaRelay;
using Rynchodon.Utility.Network;
using Sandbox.Definitions;
using Sandbox.Game.Gui;
using Sandbox.Game.Weapons;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Interfaces;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRageMath;

namespace Rynchodon.Weapons.Guided
{
	public class GuidedMissileLauncher
	{
		private const ulong checkInventoryInterval = Globals.UpdatesPerSecond;

		private enum InitialTargetStatus : byte { None, Golis, FromWeapon, NoStorage, NotFoundId, NotFoundAny }

		#region Static

		private static ValueSync<bool, GuidedMissileLauncher> termControl_shoot;

		[OnWorldLoad]
		private static void Init()
		{
			MyAPIGateway.Entities.OnEntityAdd += Entities_OnEntityAdd;
			HijackShoot();
		}

		[OnWorldClose]
		private static void Unload()
		{
			MyAPIGateway.Entities.OnEntityAdd -= Entities_OnEntityAdd;
		}

		public static bool IsGuidedMissileLauncher(IMyCubeBlock block)
		{
			return block is IMyUserControllableGun && WeaponDescription.GetFor(block).GuidedMissileLauncher;
		}

		private static void Entities_OnEntityAdd(IMyEntity obj)
		{
			if (obj.IsMissile())
			{
				foreach (GuidedMissileLauncher launcher in Registrar.Scripts<GuidedMissileLauncher>())
					if (launcher.MissileBelongsTo(obj))
						return;
				Logger.TraceLog("No launcher for: " + obj.nameWithId());
			}
		}

		private static void HijackShoot()
		{
			TerminalControlHelper.EnsureTerminalControlCreated<MySmallMissileLauncher>();
			Func<object, bool> False = (o) => false;

			foreach (ITerminalControl control in MyTerminalControlFactory.GetControls(typeof(MyUserControllableGun)))
				if (control.Id == "ShootOnce")
				{
					MyTerminalControlButton<MyUserControllableGun> shootOnce = (MyTerminalControlButton<MyUserControllableGun>)control;
					EventSync<GuidedMissileLauncher> termControl_shootOnce = new EventSync<GuidedMissileLauncher>(shootOnce.Id, ShootOnceEvent, false);

					Action <MyUserControllableGun> originalAction = shootOnce.Action;
					shootOnce.Action = block => {
						if (IsGuidedMissileLauncher(block))
							termControl_shootOnce.RunEvent(block);
						else
							originalAction(block);
					};

					shootOnce.Actions[0].Action = shootOnce.Action;
				}
				else if (control.Id == "Shoot")
				{
					MyTerminalControlOnOffSwitch<MyUserControllableGun> shoot = (MyTerminalControlOnOffSwitch<MyUserControllableGun>)control;
					termControl_shoot = new ValueSync<bool, GuidedMissileLauncher>(shoot.Id, "value_termShoot");

					var originalGetter = shoot.Getter;
					var originalSetter = shoot.Setter;
					shoot.Getter = (block) => {
						if (IsGuidedMissileLauncher(block))
							return termControl_shoot.GetValue(block);
						else
							return originalGetter(block);
					};
					shoot.Setter = (block, value) => {
						if (IsGuidedMissileLauncher(block))
							termControl_shoot.SetValue(block, value);
						else
							originalSetter(block, value);
					};

					shoot.Actions[0].Action = block => shoot.SetValue(block, !shoot.GetValue(block)); // toggle
					shoot.Actions[1].Action = block => shoot.SetValue(block, true); // on
					shoot.Actions[2].Action = block => shoot.SetValue(block, false); // off
					break;
				}
		}

		private static void ShootOnceEvent(GuidedMissileLauncher launcher)
		{
			launcher.LockAndShoot();
		}

		#endregion

		private readonly Logger myLogger;
		public readonly WeaponTargeting m_weaponTarget;
		public IMyUserControllableGun CubeBlock { get { return m_weaponTarget.CubeBlock; } }
		/// <summary>Local position where the magic happens (hopefully).</summary>
		private readonly BoundingBox MissileSpawnBox;
		private readonly MyInventoryBase myInventory;
		public readonly IRelayPart m_relayPart;

		private List<IMyEntity> m_cluster = new List<IMyEntity>();

		private bool m_onCooldown, m_onGameCooldown;
		private TimeSpan m_gameCooldownTime;
		private TimeSpan cooldownUntil;

		private InitialTargetStatus _initial;
		private Target _initialTarget;
		/// <summary>A shot was recently fired, a guided missile might be in the box.</summary>
		private bool _isShooting;
		/// <summary>Keep firing until cluster is complete.</summary>
		private bool _shootCluster;

#pragma warning disable CS0649
		private bool value_termShoot;
		private bool _termShoot
		{
			get { return value_termShoot; }
			set { termControl_shoot.SetValue(CubeBlock, value); }
		}
#pragma warning restore CS0649

		public Ammo loadedAmmo { get { return m_weaponTarget.LoadedAmmo; } }

		public GuidedMissileLauncher(WeaponTargeting weapon)
		{
			m_weaponTarget = weapon;
			myLogger = new Logger(CubeBlock);
			m_relayPart = RelayClient.GetOrCreateRelayPart(m_weaponTarget.CubeBlock);
			this._initialTarget = NoTarget.Instance;

			MyWeaponBlockDefinition defn = (MyWeaponBlockDefinition)CubeBlock.GetCubeBlockDefinition();

			Vector3[] points = new Vector3[3];
			Vector3 forwardAdjust = Vector3.Forward * WeaponDescription.GetFor(CubeBlock).MissileSpawnForward;
			points[0] = CubeBlock.LocalAABB.Min + forwardAdjust;
			points[1] = CubeBlock.LocalAABB.Max + forwardAdjust;
			points[2] = CubeBlock.LocalAABB.Min + Vector3.Up * defn.Size.Y * CubeBlock.CubeGrid.GridSize + forwardAdjust;

			MissileSpawnBox = BoundingBox.CreateFromPoints(points);
			if (m_weaponTarget.myTurret != null)
			{
				myLogger.traceLog("original box: " + MissileSpawnBox);
				MissileSpawnBox.Inflate(CubeBlock.CubeGrid.GridSize * 2f);
			}

			myLogger.traceLog("MissileSpawnBox: " + MissileSpawnBox);

			myInventory = ((MyEntity)CubeBlock).GetInventoryBase(0);

			Registrar.Add(weapon.CubeBlock, this);
			m_weaponTarget.GuidedLauncher = true;

			m_gameCooldownTime = TimeSpan.FromSeconds(60d / MyDefinitionManager.Static.GetWeaponDefinition(defn.WeaponDefinitionId).WeaponAmmoDatas[(int)MyAmmoType.Missile].RateOfFire);
			myLogger.traceLog("m_gameCooldownTime: " + m_gameCooldownTime);

			CubeBlock.AppendingCustomInfo += CubeBlock_AppendingCustomInfo;
		}

		public void Update1()
		{
			CheckCooldown();
			if (!m_onCooldown && !m_onGameCooldown && (_termShoot || _shootCluster))
				LockAndShoot();
		}

		private bool MissileBelongsTo(IMyEntity missile)
		{
			if (!_isShooting)
			{
				myLogger.traceLog("Not mine, not shooting");
				return false;
			}
			Vector3D local = Vector3D.Transform(missile.GetPosition(), CubeBlock.WorldMatrixNormalizedInv);
			if (MissileSpawnBox.Contains(local) != ContainmentType.Contains)
			{
				myLogger.traceLog("Not in my box: " + missile + ", position: " + local);
				return false;
			}
			if (m_weaponTarget.myTurret == null)
			{
				if (Vector3D.RectangularDistance(CubeBlock.WorldMatrix.Forward, missile.WorldMatrix.Forward) > 0.01)
				{
					myLogger.traceLog("Facing the wrong way: " + missile + ", missile direction: " + missile.WorldMatrix.Forward + ", block direction: " + CubeBlock.WorldMatrix.Forward
						+ ", RectangularDistance: " + Vector3D.RectangularDistance(CubeBlock.WorldMatrix.Forward, missile.WorldMatrix.Forward));
					return false;
				}
			}
			else
			{
				Vector3 turretDirection;
				Vector3.CreateFromAzimuthAndElevation(m_weaponTarget.myTurret.Azimuth, m_weaponTarget.myTurret.Elevation, out turretDirection);
				turretDirection = Vector3.Transform(turretDirection, CubeBlock.WorldMatrix.GetOrientation());
				if (Vector3D.RectangularDistance(turretDirection, missile.WorldMatrix.Forward) > 0.01)
				{
					myLogger.traceLog("Facing the wrong way: " + missile + ", missile direction: " + missile.WorldMatrix.Forward + ", turret direction: " + turretDirection
						+ ", RectangularDistance: " + Vector3D.RectangularDistance(CubeBlock.WorldMatrix.Forward, missile.WorldMatrix.Forward));
					return false;
				}
			}

			_isShooting = false;

			if (loadedAmmo == null)
			{
				myLogger.debugLog("Mine but no loaded ammo!", Logger.severity.INFO);
				return true;
			}

			if (loadedAmmo.Description == null || loadedAmmo.Description.GuidanceSeconds < 1f)
			{
				myLogger.debugLog("Mine but not a guided missile!", Logger.severity.INFO);
				return true;
			}

			//myLogger.debugLog("Opts: " + m_weaponTarget.Options);
			try
			{
				if (loadedAmmo.IsCluster)
				{
					m_cluster.Add(missile);
					if (m_cluster.Count >= loadedAmmo.MagazineDefinition.Capacity)
					{
						myLogger.traceLog("Final missile in cluster: " + missile, Logger.severity.DEBUG);
						_shootCluster = false;
					}
					else
					{
						_shootCluster = true;
						myLogger.traceLog("Added to cluster: " + missile + ", count: " + m_cluster.Count, Logger.severity.DEBUG);
						return true;
					}
				}

				myLogger.traceLog("creating new guided missile");
				if (m_cluster.Count != 0)
				{
					myLogger.traceLog("Creating cluster guided missile");
					Cluster cluster = new Cluster(m_cluster, CubeBlock);
					if (cluster.Master != null)
						new GuidedMissile(new Cluster(m_cluster, CubeBlock), this, _initialTarget);
					else
						myLogger.alwaysLog("Failed to create cluster, all missiles closed", Logger.severity.WARNING);
					StartCooldown();
					m_cluster.Clear();
				}
				else
				{
					myLogger.traceLog("Creating standard guided missile");
					new GuidedMissile(missile, this, _initialTarget);
					StartCooldown(true);
				}

				//// display target in custom info
				//if (m_weaponTarget.CurrentControl == WeaponTargeting.Control.Off)
				//	m_weaponTarget.CurrentTarget = initialTarget;
			}
			catch (Exception ex)
			{
				myLogger.alwaysLog("failed to create GuidedMissile", Logger.severity.ERROR);
				myLogger.alwaysLog("Exception: " + ex, Logger.severity.ERROR);
			}

			return true;
		}

		private void StartCooldown(bool gameCooldown = false)
		{
			if (gameCooldown)
			{
				m_onGameCooldown = true;
				m_weaponTarget.SuppressTargeting = true;
				cooldownUntil = Globals.ElapsedTime + m_gameCooldownTime;
				myLogger.debugLog("started game cooldown, suppressing targeting until " + cooldownUntil);
			}
			else
			{
				m_onCooldown = true;
				cooldownUntil = Globals.ElapsedTime + TimeSpan.FromSeconds(loadedAmmo.Description.ClusterCooldown);
			}
		}

		private void CheckCooldown()
		{
			if (!m_onCooldown && !m_onGameCooldown)
				return;

			if (cooldownUntil < Globals.ElapsedTime)
			{
				myLogger.debugLog("off cooldown");
				if (m_onGameCooldown)
					m_weaponTarget.SuppressTargeting = false;
				m_onCooldown = false;
				m_onGameCooldown = false;
			}
		}

		private void LockAndShoot()
		{
			if (m_onCooldown || m_onGameCooldown)
				return;

			if (!_shootCluster)
			{
				InitialTargetStatus initial = GetInitialTarget();
				if (initial != _initial || _initialTarget != m_weaponTarget.CurrentTarget)
				{
					myLogger.traceLog("Updating custom info");
					_initial = initial;
					_initialTarget = m_weaponTarget.CurrentTarget;
					CubeBlock.RefreshCustomInfo();
				}

				if (m_weaponTarget.CurrentTarget is NoTarget && !m_weaponTarget.FireWithoutLock)
				{
					myLogger.traceLog("Cannot fire, no target found", Logger.severity.TRACE);
					return;
				}
			}

			myLogger.traceLog("Shooting from terminal");
			_isShooting = true;
			((MyUserControllableGun)CubeBlock).ShootFromTerminal(m_weaponTarget.Facing());
			myLogger.traceLog("Back from shoot");
		}

		private InitialTargetStatus GetInitialTarget()
		{
			if (m_weaponTarget.Options.TargetGolis.IsValid())
			{
				m_weaponTarget.SetTarget(new GolisTarget(CubeBlock, m_weaponTarget.Options.TargetGolis));
				return InitialTargetStatus.Golis;
			}

			if (m_weaponTarget.CurrentControl != WeaponTargeting.Control.Off && !(m_weaponTarget.CurrentTarget is NoTarget))
				return InitialTargetStatus.FromWeapon;

			RelayStorage storage = m_relayPart.GetStorage();
			if (storage == null)
			{
				myLogger.debugLog("Failed to get storage for launcher", Logger.severity.WARNING);
				return InitialTargetStatus.NoStorage;
			}
			else
			{
				ITerminalProperty<float> rangeProperty = CubeBlock.GetProperty("Range") as ITerminalProperty<float>;
				if (rangeProperty == null)
				{
					Logger.AlwaysLog("rangeProperty == null", Logger.severity.FATAL);
					return InitialTargetStatus.None;
				}
				float range = rangeProperty.GetValue(CubeBlock);
				if (range < 1f)
					range = loadedAmmo.MissileDefinition.MaxTrajectory;
				m_weaponTarget.GetLastSeenTarget(storage, range);
				if (!(m_weaponTarget.CurrentTarget is NoTarget))
					return InitialTargetStatus.FromWeapon;
				else if (m_weaponTarget.Options.TargetEntityId != 0)
					return InitialTargetStatus.NotFoundId;
				else
					return InitialTargetStatus.NotFoundAny;
			}
		}

		private void CubeBlock_AppendingCustomInfo(IMyTerminalBlock block, System.Text.StringBuilder customInfo)
		{
			switch (_initial)
			{
				case InitialTargetStatus.None:
					return;
				case InitialTargetStatus.Golis:
					customInfo.Append(AmmoName());
					customInfo.Append(" fired at position: ");
					customInfo.AppendLine(_initialTarget.GetPosition().ToPretty());
					return;
				case InitialTargetStatus.FromWeapon:
					customInfo.Append(AmmoName());
					customInfo.Append(" fired at ");
					LastSeenTarget lst = _initialTarget as LastSeenTarget;
					if (lst != null)
					{
						if (lst.Block != null)
						{
							customInfo.Append(lst.Block.DefinitionDisplayNameText);
							customInfo.Append(" on ");
						}
						customInfo.AppendLine(lst.LastSeen.HostileName());
					}
					else
						customInfo.AppendLine(_initialTarget.Entity.GetNameForDisplay(CubeBlock.OwnerId));
					return;
				case InitialTargetStatus.NoStorage:
					customInfo.AppendLine("Cannot fire: Not connected to an antenna or radar");
					return;
				case InitialTargetStatus.NotFoundId:
					customInfo.Append("Cannot fire: No entity found with ID: ");
					customInfo.AppendLine(m_weaponTarget.Options.TargetEntityId.ToString());
					return;
				case InitialTargetStatus.NotFoundAny:
					customInfo.AppendLine("Cannot fire: No targets");
					return;
				default:
					Logger.AlwaysLog("Not implemented: " + _initial, Logger.severity.ERROR);
					return;
			}
		}

		private string AmmoName()
		{
			Ammo la = m_weaponTarget. LoadedAmmo;
			if (la != null && !string.IsNullOrEmpty(la.AmmoDefinition.DisplayNameString))
				return la.AmmoDefinition.DisplayNameString;
			else
				return "Guided Missile";
		}

	}
}
