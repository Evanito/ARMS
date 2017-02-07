﻿using System;
using Sandbox.ModAPI;

namespace Rynchodon.Utility.Network
{
	/// <summary>
	/// For synchronizing and saving a value that is not directly tied to a terminal control.
	/// </summary>
	/// <typeparam name="TValue">The type of value</typeparam>
	/// <typeparam name="TScript">The script that contains the value</typeparam>
	public sealed class ValueSync<TValue, TScript> : AValueSync<TValue, TScript>
	{

		/// <summary>
		/// Synchronize a value that is not directly tied to a terminal control. The value will be synchronized every time it changes.
		/// </summary>
		/// <param name="valueId">Identifier for the value</param>
		/// <param name="getter">Method to get the value from a script</param>
		/// <param name="setter">Method to set the value in a script</param>
		/// <param name="save">Save the value to disk</param>
		/// <param name="defaultValue">Do not get value from server when it equals defaultValue. The value in the script will NOT be set to defaultValue by ValueSync.</param>
		public ValueSync(string valueId, GetterDelegate getter, SetterDelegate setter, bool save = true, TValue defaultValue = default(TValue)) 
			: base(valueId, getter, setter, save, defaultValue) { }

		/// <summary>
		/// Synchronize a value that is not directly tied to a terminal control. The value will be synchronized every time it changes.
		/// </summary>
		/// <param name="valueId">Identifier for the value</param>
		/// <param name="fieldName">The name of a field in the script to get/set the value from/to. If the field has a default value, the DefaultValueAttribute should be used.</param>
		/// <param name="save">Save the value to disk</param>
		public ValueSync(string valueId, string fieldName, bool save = true)
			: base(valueId, fieldName, save) { }


		protected override void SetValue(long entityId, TScript script, TValue value, bool send)
		{
			traceLog("entered");

			TValue currentValue = _getter(script);
			if (!EqualityComparer.Equals(value, currentValue))
			{
				traceLog("value changed from " + currentValue + " to " + value);
				_setter(script, value);
				if (send)
					SendValue(entityId, value);
			}
			else
				traceLog("equals previous value");
		}

	}
}
