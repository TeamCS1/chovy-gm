using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace GMAssetCompiler
{
	// Cross-references every GML source string this compiler is about to hand
	// to the runner against RunnerSupportedFunctions.Names, so a call to a
	// function this specific PSP runner doesn't implement (e.g. draw_self(),
	// d3d_light_define_ambient()) becomes an immediate, readable warning here
	// on the PC instead of a silent PrepareGame failure discovered only via
	// live PPSSPP breakpoints - see CLAUDE.md's "Silent GML compile failure"
	// and Pillar of Autumn sections for what that failure mode actually looks
	// like and costs to diagnose without this.
	public static class GMLFunctionValidator
	{
		// GML language keywords/control structures - identifier-shaped tokens
		// immediately followed by '(' that are not function calls at all.
		private static readonly HashSet<string> Keywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
		{
			"if", "while", "for", "do", "until", "repeat", "with", "switch", "case", "default",
			"return", "exit", "var", "globalvar", "break", "continue", "not", "and", "or", "xor",
			"else", "then", "begin", "end", "div", "mod"
		};

		private static readonly Regex CallPattern = new Regex(@"\b([A-Za-z_][A-Za-z0-9_]*)\s*\(", RegexOptions.Compiled);

		// Scans one piece of GML source and returns the distinct function-call-
		// shaped identifiers in it that aren't GML keywords, aren't one of the
		// caller's own known-valid names (script names, object/room/sprite
		// names used as action_execute_script-style targets, etc.), and
		// aren't in the runner's own supported-function table.
		public static IEnumerable<string> FindUnsupportedCalls(string _gmlSource, ICollection<string> _knownNames)
		{
			if (string.IsNullOrEmpty(_gmlSource))
			{
				yield break;
			}
			HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (Match match in CallPattern.Matches(_gmlSource))
			{
				string name = match.Groups[1].Value;
				if (Keywords.Contains(name)) continue;
				if (_knownNames != null && _knownNames.Contains(name)) continue;
				if (RunnerSupportedFunctions.Names.Contains(name)) continue;
				if (!seen.Add(name)) continue;
				yield return name;
			}
		}

		// Validates every script and every ACT_CODE action's GML in the given
		// assets graph, printing a warning per (location, unsupported call)
		// pair found. Does not fail the build - matches this codebase's
		// existing convention of warning and continuing (see
		// WarnIfUnsupportedResources's predecessor / the drag-and-drop action
		// skip in Loader.LoadGMS14Action) rather than hard-erroring, since the
		// call might be dead code, or the developer may be deliberately
		// targeting a different runner build with broader support.
		public static void ValidateAssets(GMAssets _assets)
		{
			HashSet<string> knownNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (KeyValuePair<string, GMScript> script in _assets.Scripts)
			{
				knownNames.Add(script.Key);
			}

			foreach (KeyValuePair<string, GMScript> script in _assets.Scripts)
			{
				if (script.Value == null) continue;
				foreach (string unsupported in FindUnsupportedCalls(script.Value.Script, knownNames))
				{
					Console.WriteLine("Warning: script '{0}' calls '{1}', which is not in this runner's supported GML function table - it will fail PrepareGame silently on-device if actually reached.", script.Key, unsupported);
				}
			}

			foreach (KeyValuePair<string, GMObject> obj in _assets.Objects)
			{
				if (obj.Value == null) continue;
				ValidateEventSlots(obj.Key, obj.Value.Events, knownNames);
			}

			foreach (KeyValuePair<string, GMTimeLine> timeline in _assets.TimeLines)
			{
				if (timeline.Value == null) continue;
				foreach (KeyValuePair<int, GMEvent> entry in timeline.Value.Entries)
				{
					ValidateEvent(timeline.Key, entry.Value, knownNames);
				}
			}
		}

		private static void ValidateEventSlots(string _ownerName, IList<IList<KeyValuePair<int, GMEvent>>> _eventSlots, ICollection<string> _knownNames)
		{
			if (_eventSlots == null) return;
			foreach (IList<KeyValuePair<int, GMEvent>> slot in _eventSlots)
			{
				if (slot == null) continue;
				foreach (KeyValuePair<int, GMEvent> entry in slot)
				{
					ValidateEvent(_ownerName, entry.Value, _knownNames);
				}
			}
		}

		private static void ValidateEvent(string _ownerName, GMEvent _event, ICollection<string> _knownNames)
		{
			if (_event == null || _event.Actions == null) return;
			foreach (GMAction action in _event.Actions)
			{
				if (action == null || action.Kind != eAction.ACT_CODE) continue;
				string code = action.Args != null && action.Args.Count > 0 ? action.Args[0] : action.Code;
				foreach (string unsupported in FindUnsupportedCalls(code, _knownNames))
				{
					Console.WriteLine("Warning: '{0}' calls '{1}', which is not in this runner's supported GML function table - it will fail PrepareGame silently on-device if actually reached.", _ownerName, unsupported);
				}
			}
		}
	}
}
