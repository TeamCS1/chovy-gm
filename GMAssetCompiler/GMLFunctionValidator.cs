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

		// Which resource dictionary a given call's argument names into, and at
		// which (zero-based) argument index. This is a genuinely different bug
		// class from FindUnsupportedCalls above: draw_set_font() itself is a
		// perfectly real, supported function, so the scan above has nothing to
		// say about it - but if the font name it's given doesn't exist as a
		// real resource, the real compiler (GMLCompile.Code_Constant_Find,
		// which tries FindResourceIndexFromName first) silently falls through
		// to treating the identifier as an ordinary variable instead
		// (Code_Variable_Find just creates one on the spot) - no error, ever,
		// and the runner gets a garbage/undefined value at the call site
		// instead of the intended resource. Caught live: a GMS1.4 build
		// compiled clean twice with every text draw silently dead, because
		// draw_set_font() named a font the project didn't actually have.
		// Not exhaustive - the functions below are the highest-traffic ones;
		// extend the table rather than writing a parallel check.
		private enum ResourceCategory { Sprites, Backgrounds, Fonts, Sounds, Objects, Rooms, Paths, TimeLines, Any }

		// The other half of the same blindness: a scan that walks *calls* never
		// sees `sprite_index = spr_typo;`, which is how a sprite resource is
		// most often named in real GML in the first place. These are the
		// built-in variables that are (a) writable in GM8.1 and (b) hold a
		// resource index, so a bare identifier assigned into one is a resource
		// name in exactly the same way a draw_sprite() argument is.
		// Deliberately excluded: object_index and path_index (read-only in
		// GM8.1 - assigning them is a different bug, not ours to report).
		private static readonly Dictionary<string, ResourceCategory> ResourceAssignTargets =
			new Dictionary<string, ResourceCategory>(StringComparer.OrdinalIgnoreCase)
			{
				{ "sprite_index", ResourceCategory.Sprites },
				{ "mask_index", ResourceCategory.Sprites },
				{ "timeline_index", ResourceCategory.TimeLines },
				{ "room", ResourceCategory.Rooms },
				{ "background_index", ResourceCategory.Backgrounds },
			};

		// LHS (optionally `other.`-qualified or `[n]`-subscripted, which \b and
		// the optional group handle) = bare RHS identifier, terminated rather
		// than continuing into an expression. `==`/`!=`/`+=` can't match: the
		// (?!=) rules out `==`, and any other operator character sits between
		// the name and the `=` where only whitespace is allowed. A non-trivial
		// RHS (`choose(a,b)`, `spr_a + 1`) fails the terminator lookahead and
		// is skipped as too dynamic, same as on the call side.
		private static readonly Regex AssignPattern = new Regex(
			@"\b([A-Za-z_][A-Za-z0-9_]*)\s*(?:\[[^\]]*\])?\s*=(?!=)\s*([A-Za-z_][A-Za-z0-9_]*)\s*(?=[;\r\n)]|$)",
			RegexOptions.Compiled);

		private static readonly Dictionary<string, KeyValuePair<int, ResourceCategory>> ResourceArgs =
			new Dictionary<string, KeyValuePair<int, ResourceCategory>>(StringComparer.OrdinalIgnoreCase)
			{
				{ "draw_sprite", Pair(0, ResourceCategory.Sprites) },
				{ "draw_sprite_ext", Pair(0, ResourceCategory.Sprites) },
				{ "draw_sprite_general", Pair(0, ResourceCategory.Sprites) },
				{ "draw_sprite_part", Pair(0, ResourceCategory.Sprites) },
				{ "draw_sprite_part_ext", Pair(0, ResourceCategory.Sprites) },
				{ "draw_sprite_stretched", Pair(0, ResourceCategory.Sprites) },
				{ "draw_sprite_stretched_ext", Pair(0, ResourceCategory.Sprites) },
				{ "draw_sprite_tiled", Pair(0, ResourceCategory.Sprites) },
				{ "draw_sprite_tiled_ext", Pair(0, ResourceCategory.Sprites) },
				{ "draw_background", Pair(0, ResourceCategory.Backgrounds) },
				{ "draw_background_ext", Pair(0, ResourceCategory.Backgrounds) },
				{ "draw_background_general", Pair(0, ResourceCategory.Backgrounds) },
				{ "draw_background_part", Pair(0, ResourceCategory.Backgrounds) },
				{ "draw_background_part_ext", Pair(0, ResourceCategory.Backgrounds) },
				{ "draw_background_stretched", Pair(0, ResourceCategory.Backgrounds) },
				{ "draw_background_stretched_ext", Pair(0, ResourceCategory.Backgrounds) },
				{ "draw_background_tiled", Pair(0, ResourceCategory.Backgrounds) },
				{ "draw_background_tiled_ext", Pair(0, ResourceCategory.Backgrounds) },
				{ "draw_set_font", Pair(0, ResourceCategory.Fonts) },
				{ "sound_play", Pair(0, ResourceCategory.Sounds) },
				{ "sound_loop", Pair(0, ResourceCategory.Sounds) },
				{ "sound_stop", Pair(0, ResourceCategory.Sounds) },
				{ "sound_isplaying", Pair(0, ResourceCategory.Sounds) },
				{ "sound_volume", Pair(0, ResourceCategory.Sounds) },
				{ "sound_pan", Pair(0, ResourceCategory.Sounds) },
				{ "sound_fade", Pair(0, ResourceCategory.Sounds) },
				{ "sound_discard", Pair(0, ResourceCategory.Sounds) },
				{ "sound_restore", Pair(0, ResourceCategory.Sounds) },
				{ "sound_exists", Pair(0, ResourceCategory.Sounds) },
				{ "instance_create", Pair(2, ResourceCategory.Objects) },
				{ "room_goto", Pair(0, ResourceCategory.Rooms) },
				{ "path_start", Pair(0, ResourceCategory.Paths) },
				{ "asset_get_index", Pair(0, ResourceCategory.Any) },
			};

		private static KeyValuePair<int, ResourceCategory> Pair(int index, ResourceCategory category)
		{
			return new KeyValuePair<int, ResourceCategory>(index, category);
		}

		// Functions genuinely missing from the stock runner but patched in as
		// real native code for the GMS1.4 target specifically (see CLAUDE.md's
		// "draw_self() patched into the runner" section and
		// tools/runner_patch/). Only ever consulted when validating a GMS1.4
		// build - the GM8.1 target always uses the standard, unpatched runner,
		// so a GM8.1 project calling draw_self() should still warn.
		private static readonly HashSet<string> Gms14PatchedAdditions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
		{
			"draw_self",
			"clamp",
			"lerp",
			"dot_product",
		};

		// Scans one piece of GML source and returns the distinct function-call-
		// shaped identifiers in it that aren't GML keywords, aren't one of the
		// caller's own known-valid names (script names, object/room/sprite
		// names used as action_execute_script-style targets, etc.), and
		// aren't in the runner's own supported-function table.
		public static IEnumerable<string> FindUnsupportedCalls(string _gmlSource, ICollection<string> _knownNames, bool _isGms14Target = false)
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
				if (_isGms14Target && Gms14PatchedAdditions.Contains(name)) continue;
				if (!seen.Add(name)) continue;
				yield return name;
			}
		}

		private static bool ResolvesToKnownResource(GMAssets _assets, string _name, ResourceCategory _category)
		{
			if (_assets == null || string.IsNullOrEmpty(_name)) return false;
			// Same category order FindResourceIndexFromName uses, so "Any" (the
			// asset_get_index case, which searches across every resource type by
			// design) can never disagree with what the real compiler would find.
			switch (_category)
			{
				case ResourceCategory.Sprites: return GMLCompile.Find(_assets.Sprites, _name) >= 0;
				case ResourceCategory.Backgrounds: return GMLCompile.Find(_assets.Backgrounds, _name) >= 0;
				case ResourceCategory.Fonts: return GMLCompile.Find(_assets.Fonts, _name) >= 0;
				case ResourceCategory.Sounds: return GMLCompile.Find(_assets.Sounds, _name) >= 0;
				case ResourceCategory.Objects: return GMLCompile.Find(_assets.Objects, _name) >= 0;
				case ResourceCategory.Rooms: return GMLCompile.Find(_assets.Rooms, _name) >= 0;
				case ResourceCategory.Paths: return GMLCompile.Find(_assets.Paths, _name) >= 0;
				case ResourceCategory.TimeLines: return GMLCompile.Find(_assets.TimeLines, _name) >= 0;
				case ResourceCategory.Any:
					return GMLCompile.Find(_assets.Objects, _name) >= 0
						|| GMLCompile.Find(_assets.Sprites, _name) >= 0
						|| GMLCompile.Find(_assets.Sounds, _name) >= 0
						|| GMLCompile.Find(_assets.Backgrounds, _name) >= 0
						|| GMLCompile.Find(_assets.Paths, _name) >= 0
						|| GMLCompile.Find(_assets.Fonts, _name) >= 0
						|| GMLCompile.Find(_assets.TimeLines, _name) >= 0
						|| GMLCompile.Find(_assets.Scripts, _name) >= 0
						|| GMLCompile.Find(_assets.Rooms, _name) >= 0;
					// Triggers deliberately omitted - FindTriggerConstName isn't a
					// KeyValuePair<string,T> list, and trigger names in a resource
					// argument position is vanishingly rare; extend here if it
					// ever turns up in practice.
				default:
					return false;
			}
		}

		private static readonly Regex SimpleIdentifier = new Regex(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

		// Both maskers preserve length exactly (every removed character becomes
		// a space), so offsets into the masked text still line up with the
		// original - CallPattern's match.Index is used to walk forward into the
		// real source, and that only works if nothing shifts.
		//
		// Worth doing rather than skipping: this scanner's regexes read raw
		// source, and commented-out code is the single most likely place to
		// find a call or assignment naming a resource that was deliberately
		// deleted. Without this, `// sprite_index = spr_old_name;` warns
		// forever about a resource the developer removed on purpose.
		private static string MaskComments(string _source)
		{
			char[] chars = _source.ToCharArray();
			int i = 0;
			while (i < chars.Length)
			{
				char c = chars[i];
				if (c == '"' || c == '\'')
				{
					char quote = c;
					i++;
					while (i < chars.Length && chars[i] != quote) i++;
					i++;
					continue;
				}
				if (c == '/' && i + 1 < chars.Length && chars[i + 1] == '/')
				{
					while (i < chars.Length && chars[i] != '\n') { chars[i] = ' '; i++; }
					continue;
				}
				if (c == '/' && i + 1 < chars.Length && chars[i + 1] == '*')
				{
					while (i < chars.Length && !(chars[i] == '*' && i + 1 < chars.Length && chars[i + 1] == '/'))
					{
						if (chars[i] != '\n') chars[i] = ' ';
						i++;
					}
					if (i < chars.Length) { chars[i] = ' '; i++; }
					if (i < chars.Length) { chars[i] = ' '; i++; }
					continue;
				}
				i++;
			}
			return new string(chars);
		}

		// Blanks string *contents* but keeps the quotes, so `"sprite_index = spr_x"`
		// inside a message string can't be mistaken for real code. Only used for
		// the assignment scan - the call scan deliberately keeps string contents,
		// since asset_get_index("name") needs to read them.
		private static string MaskStringContents(string _source)
		{
			char[] chars = _source.ToCharArray();
			int i = 0;
			while (i < chars.Length)
			{
				char c = chars[i];
				if (c == '"' || c == '\'')
				{
					char quote = c;
					i++;
					while (i < chars.Length && chars[i] != quote) { chars[i] = ' '; i++; }
					i++;
					continue;
				}
				i++;
			}
			return new string(chars);
		}

		// The exact false positive flagged in review: a name that reads like a
		// resource (fnt_test, spr_player, ...) but is deliberately assigned or
		// declared as an ordinary variable elsewhere in the same script/action
		// - a dynamically-obtained font/sprite/sound handle, not a typo'd
		// resource name. Since the real compiler resolves resource constants
		// BEFORE local variables (see FindResourceIndexFromName's callers), a
		// var of the same name as a real resource does NOT actually shadow it
		// at runtime - but we still skip flagging here, because we can't tell
		// "deliberately reused a resource's name for a local" apart from
		// "deliberately holds a dynamic handle under a resource-shaped name"
		// from source text alone, and a false "missing resource" warning here
		// is worse than staying quiet on a genuinely ambiguous case.
		private static bool AssignedElsewhereInSource(string _gmlSource, string _name)
		{
			string escaped = Regex.Escape(_name);
			if (Regex.IsMatch(_gmlSource, @"\bvar\b[^;]*\b" + escaped + @"\b")) return true;
			if (Regex.IsMatch(_gmlSource, @"\b" + escaped + @"\s*(\[[^\]]*\])?\s*=(?!=)")) return true;
			return false;
		}

		// Splits one call's argument list on top-level commas only - a nested
		// call's or array literal's own commas don't count, and commas inside
		// a quoted string never do either. Good enough for the fixed-arity,
		// well-formed calls this check targets; not a general GML parser.
		private static List<string> SplitTopLevelArgs(string _argsText)
		{
			List<string> args = new List<string>();
			int depth = 0;
			char quote = '\0';
			int start = 0;
			for (int i = 0; i < _argsText.Length; i++)
			{
				char c = _argsText[i];
				if (quote != '\0')
				{
					if (c == quote) quote = '\0';
					continue;
				}
				if (c == '"' || c == '\'') { quote = c; continue; }
				if (c == '(' || c == '[') { depth++; continue; }
				if (c == ')' || c == ']') { depth--; continue; }
				if (c == ',' && depth == 0)
				{
					args.Add(_argsText.Substring(start, i - start));
					start = i + 1;
				}
			}
			args.Add(_argsText.Substring(start));
			return args;
		}

		// Scans for calls to functions this compiler knows take a resource
		// name (ResourceArgs above) and flags any resource-shaped argument
		// that doesn't resolve to a real asset AND isn't explained by a local
		// assignment of the same name (see AssignedElsewhereInSource). This is
		// blind to anything more dynamic than a bare identifier or string
		// literal by design - draw_set_font(font_add(...)) or
		// draw_set_font(myFontVar) are exactly the "resolved by name at run
		// time" cases a static scan cannot and should not guess at, so they're
		// silently skipped rather than risking a wrong answer. A run that
		// finds nothing is therefore NOT proof the project is clean - it may
		// just mean every resource reference here happens to be dynamic.
		// _shadowCheckSource widens the "assigned elsewhere" search beyond the
		// one code blob being scanned for calls - an instance variable set in
		// Create and used in Draw is completely ordinary GML, and looking only
		// at the Draw action's own text would flag every one of those. Pass
		// the owning object's/timeline's full combined source here; for a
		// script (already one self-contained blob) it's the same string as
		// _gmlSource. Defaults to _gmlSource for any other caller.
		public static IEnumerable<string> FindUndefinedResourceReferences(string _gmlSource, GMAssets _assets, string _shadowCheckSource = null)
		{
			if (string.IsNullOrEmpty(_gmlSource) || _assets == null)
			{
				yield break;
			}
			if (_shadowCheckSource == null) _shadowCheckSource = _gmlSource;
			_gmlSource = MaskComments(_gmlSource);
			_shadowCheckSource = MaskComments(_shadowCheckSource);
			HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

			// Assignment form: sprite_index = spr_typo; (see ResourceAssignTargets).
			// Runs on the string-masked text so a quoted string containing
			// something assignment-shaped can't trip it.
			foreach (Match assign in AssignPattern.Matches(MaskStringContents(_gmlSource)))
			{
				string target = assign.Groups[1].Value;
				ResourceCategory assignCategory;
				if (!ResourceAssignTargets.TryGetValue(target, out assignCategory)) continue;
				string assigned = assign.Groups[2].Value;
				if (ResolvesToKnownResource(_assets, assigned, assignCategory)) continue;
				if (_assets.Options != null && _assets.Options.Constants != null && _assets.Options.Constants.ContainsKey(assigned)) continue;
				if (AssignedElsewhereInSource(_shadowCheckSource, assigned)) continue;
				string assignKey = target + "=|" + assigned;
				if (!seen.Add(assignKey)) continue;
				yield return string.Format("{0} = {1}", target, assigned);
			}

			foreach (Match match in CallPattern.Matches(_gmlSource))
			{
				string funcName = match.Groups[1].Value;
				KeyValuePair<int, ResourceCategory> spec;
				if (!ResourceArgs.TryGetValue(funcName, out spec)) continue;

				int openParen = match.Index + match.Length - 1; // CallPattern's match ends on '('
				int depth = 0;
				int closeParen = -1;
				char quote = '\0';
				for (int i = openParen; i < _gmlSource.Length; i++)
				{
					char c = _gmlSource[i];
					if (quote != '\0') { if (c == quote) quote = '\0'; continue; }
					if (c == '"' || c == '\'') { quote = c; continue; }
					if (c == '(') depth++;
					else if (c == ')') { depth--; if (depth == 0) { closeParen = i; break; } }
				}
				if (closeParen < 0) continue; // unbalanced - malformed source, not our problem here

				List<string> args = SplitTopLevelArgs(_gmlSource.Substring(openParen + 1, closeParen - openParen - 1));
				if (spec.Key >= args.Count) continue; // fewer args than expected - a different bug, not ours to report
				string rawArg = args[spec.Key].Trim();
				if (rawArg.Length == 0) continue;

				string candidate;
				bool isStringLiteral = rawArg.Length >= 2 && (rawArg[0] == '"' || rawArg[0] == '\'') && rawArg[rawArg.Length - 1] == rawArg[0];
				if (isStringLiteral)
				{
					candidate = rawArg.Substring(1, rawArg.Length - 2);
				}
				else if (SimpleIdentifier.IsMatch(rawArg))
				{
					candidate = rawArg;
				}
				else
				{
					continue; // an expression, indexed variable, nested call, numeric literal - too dynamic to judge statically
				}

				if (ResolvesToKnownResource(_assets, candidate, spec.Value)) continue;
				if (_assets.Options != null && _assets.Options.Constants != null && _assets.Options.Constants.ContainsKey(candidate)) continue;
				if (!isStringLiteral && AssignedElsewhereInSource(_shadowCheckSource, candidate)) continue;

				string key = funcName + "|" + candidate;
				if (!seen.Add(key)) continue;
				yield return string.Format("{0}('{1}')", funcName, candidate);
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
		public static void ValidateAssets(GMAssets _assets, bool _isGms14Target = false)
		{
			HashSet<string> knownNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (KeyValuePair<string, GMScript> script in _assets.Scripts)
			{
				knownNames.Add(script.Key);
			}

			foreach (KeyValuePair<string, GMScript> script in _assets.Scripts)
			{
				if (script.Value == null) continue;
				foreach (string unsupported in FindUnsupportedCalls(script.Value.Script, knownNames, _isGms14Target))
				{
					Console.WriteLine("Warning: script '{0}' calls '{1}', which is not in this runner's supported GML function table - it will fail PrepareGame silently on-device if actually reached.", script.Key, unsupported);
				}
				foreach (string badRef in FindUndefinedResourceReferences(script.Value.Script, _assets))
				{
					Console.WriteLine("Warning: script '{0}' references {1}, but that name isn't a real resource in this project - it will silently become an ordinary (likely undefined) variable at compile time and fail at runtime.", script.Key, badRef);
				}
			}

			foreach (KeyValuePair<string, GMObject> obj in _assets.Objects)
			{
				if (obj.Value == null) continue;
				string combined = CollectAllCode(obj.Value.Events);
				ValidateEventSlots(obj.Key, obj.Value.Events, knownNames, _assets, combined, _isGms14Target);
			}

			foreach (KeyValuePair<string, GMTimeLine> timeline in _assets.TimeLines)
			{
				if (timeline.Value == null) continue;
				string combined = CollectAllCode(timeline.Value.Entries);
				foreach (KeyValuePair<int, GMEvent> entry in timeline.Value.Entries)
				{
					ValidateEvent(timeline.Key, entry.Value, knownNames, _assets, combined, _isGms14Target);
				}
			}
		}

		private static string CollectAllCode(IList<IList<KeyValuePair<int, GMEvent>>> _eventSlots)
		{
			if (_eventSlots == null) return string.Empty;
			System.Text.StringBuilder sb = new System.Text.StringBuilder();
			foreach (IList<KeyValuePair<int, GMEvent>> slot in _eventSlots)
			{
				if (slot == null) continue;
				foreach (KeyValuePair<int, GMEvent> entry in slot)
				{
					AppendEventCode(sb, entry.Value);
				}
			}
			return sb.ToString();
		}

		private static string CollectAllCode(IEnumerable<KeyValuePair<int, GMEvent>> _entries)
		{
			if (_entries == null) return string.Empty;
			System.Text.StringBuilder sb = new System.Text.StringBuilder();
			foreach (KeyValuePair<int, GMEvent> entry in _entries)
			{
				AppendEventCode(sb, entry.Value);
			}
			return sb.ToString();
		}

		private static void AppendEventCode(System.Text.StringBuilder _sb, GMEvent _event)
		{
			if (_event == null || _event.Actions == null) return;
			foreach (GMAction action in _event.Actions)
			{
				if (action == null || action.Kind != eAction.ACT_CODE) continue;
				string code = action.Args != null && action.Args.Count > 0 ? action.Args[0] : action.Code;
				if (!string.IsNullOrEmpty(code)) _sb.Append(code).Append('\n');
			}
		}

		private static void ValidateEventSlots(string _ownerName, IList<IList<KeyValuePair<int, GMEvent>>> _eventSlots, ICollection<string> _knownNames, GMAssets _assets, string _shadowCheckSource, bool _isGms14Target)
		{
			if (_eventSlots == null) return;
			foreach (IList<KeyValuePair<int, GMEvent>> slot in _eventSlots)
			{
				if (slot == null) continue;
				foreach (KeyValuePair<int, GMEvent> entry in slot)
				{
					ValidateEvent(_ownerName, entry.Value, _knownNames, _assets, _shadowCheckSource, _isGms14Target);
				}
			}
		}

		private static void ValidateEvent(string _ownerName, GMEvent _event, ICollection<string> _knownNames, GMAssets _assets, string _shadowCheckSource, bool _isGms14Target)
		{
			if (_event == null || _event.Actions == null) return;
			foreach (GMAction action in _event.Actions)
			{
				if (action == null || action.Kind != eAction.ACT_CODE) continue;
				string code = action.Args != null && action.Args.Count > 0 ? action.Args[0] : action.Code;
				foreach (string unsupported in FindUnsupportedCalls(code, _knownNames, _isGms14Target))
				{
					Console.WriteLine("Warning: '{0}' calls '{1}', which is not in this runner's supported GML function table - it will fail PrepareGame silently on-device if actually reached.", _ownerName, unsupported);
				}
				foreach (string badRef in FindUndefinedResourceReferences(code, _assets, _shadowCheckSource))
				{
					Console.WriteLine("Warning: '{0}' references {1}, but that name isn't a real resource in this project - it will silently become an ordinary (likely undefined) variable at compile time and fail at runtime.", _ownerName, badRef);
				}
			}
		}
	}
}
