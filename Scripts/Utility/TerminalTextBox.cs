﻿using System;
using System.Collections.Generic;
using System.Text;
using Rynchodon.Utility.Network;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Interfaces.Terminal;
using VRage.ModAPI;

namespace Rynchodon
{
	[Obsolete]
	public class TerminalTextBox<T>
		where T : IConvertible
	{

		private class EntityVariables
		{
			//public StringBuilder Builder;
			public string Text; // SE likes to reuse the StringBuilder it gives us, so we always ToString
			public byte SinceChanged;
		}

		private readonly Dictionary<IMyEntity, EntityVariables> m_active = new Dictionary<IMyEntity, EntityVariables>();

		private readonly T m_invalidValue;
		private readonly byte m_valueId;
		private readonly Action<EntityValue<T>> m_onValueChanged;

		public TerminalTextBox(IMyTerminalControlTextbox terminalControl, byte valueId, Action<EntityValue<T>> onValueChanged, T invalidValue = default(T))
		{
			m_invalidValue = invalidValue;
			m_valueId = valueId;
			m_onValueChanged = onValueChanged;

			terminalControl.Getter = TC_Getter;
			terminalControl.Setter = TC_Setter;

			Update.UpdateManager.Register(100, Update100);
		}

		public void AllocateFor(IMyTerminalBlock block)
		{
			TryGetEntityValue(block, true);
		}

		private StringBuilder TC_Getter(IMyTerminalBlock block)
		{
			EntityVariables vars;
			if (m_active.TryGetValue(block, out vars))
			{
				Logger.DebugLog("active: " + vars.Text, context: block.nameWithId());
				return new StringBuilder(vars.Text);
			}
			EntityValue<T> ev = TryGetEntityValue(block, false);
			if (ev != null)
			{
				Logger.DebugLog("stored value: " + ev.Value, context: block.nameWithId());
				return new StringBuilder(ev.Value.ToString());
			}
			//Logger.DebugLog("new", context: block.nameWithId());
			return new StringBuilder();
		}

		private void TC_Setter(IMyTerminalBlock block, StringBuilder builder)
		{
			//foreach (var pair in m_active)
			//	if (block != pair.Key && object.ReferenceEquals(builder, pair.Value.Builder))
			//		Logger.DebugLog("supplied builder is same instance as a stored builder: " + pair.Key.nameWithId(), Logger.severity.WARNING, context: block.nameWithId());

			EntityVariables vars;
			if (m_active.TryGetValue(block, out vars))
			{
				//Logger.DebugLog("updated: " + builder, context: block.nameWithId());
				//vars.Builder = builder;
				vars.Text = builder.ToString();
				vars.SinceChanged = 0;
			}
			else
			{
				//Logger.DebugLog("new: " + builder, context: block.nameWithId());
				m_active.Add(block, new EntityVariables() { /*Builder = builder,*/ Text = builder.ToString() });
			}
		}

		private EntityValue<T> TryGetEntityValue(IMyEntity entity, bool create)
		{
			EntityValue value = EntityValue.TryGetEntityValue(entity.EntityId, m_valueId);
			if (value == null)
			{
				if (!create)
					return null;
				Logger.DebugLog("New EntityValue for " + entity.nameWithId(), context: entity.nameWithId());
				return new EntityValue<T>(entity, m_valueId, m_onValueChanged, m_invalidValue);
			}
			else
			{
				EntityValue<T> result = value as EntityValue<T>;
				if (result == null)
					Logger.AlwaysLog("EntityValue is of wrong type. Got: " + value.GetValueType() + ", expected: " + typeof(T), Logger.severity.ERROR, context: entity.nameWithId());
				return result;
			}
		}

		private void Update100()
		{
			List<IMyEntity> remove = null;
			foreach (var pair in m_active)
			{
				//Logger.DebugLog("Key: " + pair.Key + ", SinceChanged: " + pair.Value.SinceChanged + ", Text: " + pair.Value.Text, context: pair.Key.nameWithId());
				if (pair.Value.SinceChanged < 2)
				{
					pair.Value.SinceChanged++;
					continue;
				}
				if (remove == null)
					remove = new List<IMyEntity>();
				remove.Add(pair.Key);

				EntityValue<T> ev = TryGetEntityValue(pair.Key, true);

				string str = pair.Value.Text;

				T result;
				try { result = (T)Convert.ChangeType(str, typeof(T)); }
				catch
				{
					double fromPretty;
					object toConvert = PrettySI.TryParse(str, out fromPretty) ? (object)fromPretty : (object)str;

					try { result = (T)Convert.ChangeType(str, typeof(T)); }
					catch
					{
						Logger.DebugLog("Failed to convert. string: " + str, Logger.severity.WARNING, context: pair.Key.nameWithId());
						result = m_invalidValue;
						continue;
					}
				}

				Logger.DebugLog("Text:" + pair.Value.Text + ", set value to " + result, context: pair.Key.nameWithId());
				ev.Value = result;
			}
			if (remove != null)
				foreach (IMyEntity entity in remove)
					m_active.Remove(entity);
		}

	}
}
